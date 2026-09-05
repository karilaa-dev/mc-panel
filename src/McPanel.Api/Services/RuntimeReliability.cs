using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using McPanel.Api.Configuration;
using Microsoft.Data.Sqlite;

namespace McPanel.Api.Services;

public sealed record RuntimeCapabilities(int ProtocolVersion, int ConsoleSchema, string Version, string[] Features);
public sealed record RuntimeSaveLease(Guid ServerId, Guid Token, DateTimeOffset ExpiresAt);
public sealed record RuntimeRecoveryPolicy(Guid ServerId, bool Enabled);
public sealed record RuntimeIncident(Guid ServerId, string Code, string Message, DateTimeOffset Timestamp, bool Resolved = false);

internal sealed partial class RuntimeEngine
{
    private PanelOptions Settings => configuredOptions?.Value ?? new PanelOptions();
    private readonly Channel<RuntimeLog> _logQueue = Channel.CreateBounded<RuntimeLog>(new BoundedChannelOptions(Math.Clamp(configuredOptions?.Value.ConsoleBufferLines ?? 4096, 16, 65536)) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource _maintenanceStop = new();
    private readonly ConcurrentDictionary<Guid, RuntimeSaveLease> _leases = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _leaseLocks = new();
    private readonly ConcurrentDictionary<Guid, RuntimeLaunchRequest> _launches = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _recoveries = new();
    private readonly ConcurrentDictionary<Guid, int> _recoveryAttempts = new();
    private readonly Dictionary<Guid, int> _memoryReservations = new();
    private readonly object _admissionGate = new();
    private Task _logWorker = Task.CompletedTask;
    private Task _maintenanceWorker = Task.CompletedTask;
    private Task _retentionWorker = Task.CompletedTask;
    private readonly Infrastructure.AsyncKeyedLock _lifecycleLocks = new();
    private long _droppedLogLines;
    private readonly ConcurrentDictionary<string, string> _storageErrors = new();
    private readonly ConcurrentDictionary<Guid, long> _dropsByServer = new();
    private int _initialized;
    private int _disposed;
    internal TimeSpan RecoveryDelay { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromSeconds(1);
    private sealed record RuntimeLog(Guid Id, string Stream, string Text);

    public RuntimeCapabilities Capabilities() => new(RuntimeWire.Version, Data.SchemaMigration.ConsoleVersion,
        Infrastructure.RecoveryArchive.Version,
        ["save-leases", "runtime-recovery", "bounded-logging", "health", "gate-feature-memory"]);

    private Task QueueLogAsync(Guid id, string stream, string text, CancellationToken _)
    {
        if (!_logQueue.Writer.TryWrite(new(id, stream, text.Length > 16384 ? text[..16384] : text)))
        { Interlocked.Increment(ref _droppedLogLines); _dropsByServer.AddOrUpdate(id, 1, (_, count) => count + 1); }
        return Task.CompletedTask;
    }

    private async Task WriteLogsAsync(CancellationToken token)
    {
        try
        {
            await foreach (var line in _logQueue.Reader.ReadAllAsync(token))
            {
                try
                {
                    await RuntimeConsoleWriter.AppendAsync(paths.ConsoleDatabase, line.Id, line.Stream, line.Text, token);
                    if (_launches.TryGetValue(line.Id, out var launch) && launch.WorkloadKind == RuntimeWorkloadKind.Gate)
                        await AppendGateLogAsync(line.Id, line.Stream, line.Text);
                    _storageErrors.TryRemove("logs", out _);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref _droppedLogLines);
                    _dropsByServer.AddOrUpdate(line.Id, 1, (_, count) => count + 1);
                    _storageErrors["logs"] = "Runtime log persistence failed; process control remains available.";
                    logger.LogWarning(exception, "Could not persist runtime output");
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task TryPersistAsync(RuntimeServerSnapshot snapshot, CancellationToken token)
    {
        try { await PersistAsync(snapshot, token); _storageErrors.TryRemove("state", out _); }
        catch (Exception exception)
        {
            _storageErrors["state"] = "Runtime state persistence failed; process control remains available.";
            logger.LogWarning(exception, "Could not persist runtime state for {ServerId}", snapshot.ServerId);
        }
    }

    private Task EnsureRuntimeAdmissionAsync(RuntimeLaunchRequest launch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_admissionGate)
        {
            var reserved = _memoryReservations.Where(x => x.Key != launch.ServerId).Sum(x => (long)x.Value);
            if ((reserved + launch.MemoryLimitMb) * 1024L * 1024 > HostMetricsService.ReadMemory().Total * Settings.MemoryAllocationFraction)
                throw new InvalidOperationException("Starting this workload would exceed the host memory allocation limit.");
            _memoryReservations[launch.ServerId] = launch.MemoryLimitMb;
        }
        return Task.CompletedTask;
    }

    public async Task<RuntimeSaveLease> AcquireSaveLeaseAsync(Guid id, CancellationToken token)
    {
        var gate = _leaseLocks.GetOrAdd(id, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (_leases.ContainsKey(id)) throw new InvalidOperationException("A save suspension already exists for this server.");
            if (!_active.TryGetValue(id, out var managed) || managed.WorkloadKind != RuntimeWorkloadKind.Minecraft || managed.StopRequested)
                throw new InvalidOperationException("Minecraft must be running to suspend saves.");
            var lease = new RuntimeSaveLease(id, Guid.NewGuid(), DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(Settings.BackupLeaseSeconds, 1, 120)));
            await PersistLeaseAsync(lease, token);
            _leases[id] = lease;
            managed.SaveFlushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(25, Settings.BackupLeaseSeconds)));
            try
            {
                await WriteAsync(managed, "save-off", deadline.Token);
                await WriteAsync(managed, "save-all flush", deadline.Token);
                await managed.SaveFlushed.Task.WaitAsync(deadline.Token);
                if (DateTimeOffset.UtcNow >= lease.ExpiresAt) throw new TimeoutException("The save suspension expired during flush.");
                return lease;
            }
            catch { await ResumeLeaseLockedAsync(lease, expired: true); throw; }
        }
        finally { gate.Release(); }
    }

    public async Task<RuntimeSaveLease> RenewSaveLeaseAsync(RuntimeSaveLease requested, CancellationToken token)
    {
        var gate = _leaseLocks.GetOrAdd(requested.ServerId, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (!_leases.TryGetValue(requested.ServerId, out var lease) || lease.Token != requested.Token || lease.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("The save suspension expired. Discard this snapshot.");
            var next = lease with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(Settings.BackupLeaseSeconds, 1, 120)) };
            await PersistLeaseAsync(next, token); _leases[lease.ServerId] = next;
            return next;
        }
        finally { gate.Release(); }
    }

    public async Task ReleaseSaveLeaseAsync(RuntimeSaveLease requested, CancellationToken token)
    {
        var gate = _leaseLocks.GetOrAdd(requested.ServerId, _ => new(1, 1));
        await gate.WaitAsync(token);
        try
        {
            if (!_leases.TryGetValue(requested.ServerId, out var lease) || lease.Token != requested.Token)
                throw new InvalidOperationException("The save suspension is no longer valid. Discard this snapshot.");
            var expired = lease.ExpiresAt <= DateTimeOffset.UtcNow;
            await ResumeLeaseLockedAsync(lease, expired);
            if (expired) throw new InvalidOperationException("The save suspension expired. Discard this snapshot.");
        }
        finally { gate.Release(); }
    }

    private async Task ResumeLeaseLockedAsync(RuntimeSaveLease lease, bool expired)
    {
        try
        {
            if (_active.TryGetValue(lease.ServerId, out var managed) && !managed.StopRequested)
            {
                managed.SaveResumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await WriteAsync(managed, "save-on", deadline.Token);
                await managed.SaveResumed.Task.WaitAsync(deadline.Token);
            }
            _leases.TryRemove(lease.ServerId, out _);
            var file = LeaseFile(lease.ServerId);
            try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
            await RecordRuntimeIncidentAsync(lease.ServerId, "SAVE_RESUME_FAILED", "Minecraft saving has resumed.", resolved: true);
            if (expired) await QueueLogAsync(lease.ServerId, "system", "Backup save suspension expired; saves resumed and the incomplete snapshot must be discarded.", CancellationToken.None);
        }
        catch (Exception exception)
        {
            await RecordRuntimeIncidentAsync(lease.ServerId, "SAVE_RESUME_FAILED", "Minecraft did not confirm save-on. The runtime is retrying; inspect the server console.");
            logger.LogError(exception, "Could not confirm save-on for {ServerId}", lease.ServerId);
            throw;
        }
    }

    private string LeaseFile(Guid id) => Path.Combine(paths.Runtime, "leases", $"{id:N}.json");
    private Task PersistLeaseAsync(RuntimeSaveLease lease, CancellationToken token) => AtomicJsonAsync(LeaseFile(lease.ServerId), lease, token);

    internal async Task RecordRuntimeIncidentAsync(Guid id, string code, string message, bool resolved = false)
    {
        try { await AtomicJsonAsync(Path.Combine(paths.Runtime, "incidents", $"{id:N}-{code}.json"), new RuntimeIncident(id, code, message, DateTimeOffset.UtcNow, resolved), CancellationToken.None); _storageErrors.TryRemove("incidents", out _); }
        catch (Exception exception) { _storageErrors["incidents"] = message; logger.LogError(exception, "Could not persist runtime incident {Code}", code); }
    }

    private static async Task AtomicJsonAsync<T>(string destination, T value, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            { await JsonSerializer.SerializeAsync(file, value, cancellationToken: token); await file.FlushAsync(token); file.Flush(true); }
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void SetRecoveryPolicy(RuntimeRecoveryPolicy policy)
    {
        if (_launches.TryGetValue(policy.ServerId, out var launch)) _launches[policy.ServerId] = launch with { CrashRecovery = policy.Enabled };
        if (!policy.Enabled) CancelRecovery(policy.ServerId, resetAttempts: true);
    }

    private void CancelRecovery(Guid id, bool resetAttempts)
    {
        if (_recoveries.TryRemove(id, out var previous)) previous.Cancel();
        if (resetAttempts) _recoveryAttempts.TryRemove(id, out _);
    }

    private void ScheduleRecovery(Guid id)
    {
        if (!_launches.TryGetValue(id, out var launch) || !launch.CrashRecovery || _maintenanceStop.IsCancellationRequested) return;
        var pending = CancellationTokenSource.CreateLinkedTokenSource(_maintenanceStop.Token);
        if (!_recoveries.TryAdd(id, pending)) { pending.Dispose(); return; }
        var attempt = _recoveryAttempts.AddOrUpdate(id, 1, (_, count) => count + 1);
        if (attempt > 3)
        {
            _recoveries.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(id, pending)); pending.Dispose();
            _ = RecordRuntimeIncidentAsync(id, "CRASH_RECOVERY_EXHAUSTED", "Minecraft exceeded three automatic recovery attempts. Inspect the console and start it manually."); return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RecoveryDelay * new[] { 1, 3, 12 }[attempt - 1], pending.Token);
                if (!_launches.TryGetValue(id, out var current) || !current.CrashRecovery || _active.ContainsKey(id)) return;
                if (!_recoveries.TryGetValue(id, out var activeRecovery) || !ReferenceEquals(activeRecovery, pending)) return;
                pending.Token.ThrowIfCancellationRequested();
                if (!await DesiredRecoveryAsync(id, pending.Token)) { SetRecoveryPolicy(new(id, false)); return; }
                await StartAsync(current, pending.Token, recovering: true);
                await RecordRuntimeIncidentAsync(id, "CRASH_RECOVERY_EXHAUSTED", "Minecraft recovered.", resolved: true);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { logger.LogError(exception, "Runtime recovery failed for {ServerId}", id); }
            finally
            {
                var removed = _recoveries.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(id, pending));
                var canceled = pending.IsCancellationRequested; pending.Dispose();
                if (removed && !canceled && !_active.ContainsKey(id)) ScheduleRecovery(id);
            }
        });
    }

    private async Task<bool> DesiredRecoveryAsync(Guid id, CancellationToken token)
    {
        // Read the committed policy immediately before restarting, including when the
        // panel is offline or its reconciliation notification has not arrived yet.
        if (!File.Exists(paths.StateDatabase))
            return !Environment.GetCommandLineArgs().Contains(PersistentRuntimeHost.Argument, StringComparer.Ordinal);
        await using var connection = new SqliteConnection($"Data Source={paths.StateDatabase};Mode=ReadOnly;Pooling=False;Default Timeout=1");
        await connection.OpenAsync(token);
        await using var shape = connection.CreateCommand();
        shape.CommandText = "SELECT count(*) FROM pragma_table_info('Servers') WHERE name='RecoveryRequired';";
        var hasRecoveryFlag = Convert.ToInt32(await shape.ExecuteScalarAsync(token)) != 0;
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CrashRecovery, State, " + (hasRecoveryFlag ? "RecoveryRequired" : "0") + " FROM Servers WHERE upper(Id)=upper($id);";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) && reader.GetBoolean(0) && !reader.GetBoolean(2) && reader.GetString(1) is not ("Stopped" or "Stopping" or "Error" or "Installing" or "Updating");
    }

    private async Task MaintainAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.WhenAll(_leases.Values.Where(x => x.ExpiresAt <= DateTimeOffset.UtcNow).Select(async lease =>
                {
                    var gate = _leaseLocks.GetOrAdd(lease.ServerId, _ => new(1, 1));
                    if (!await gate.WaitAsync(0, token)) return;
                    try { if (_leases.TryGetValue(lease.ServerId, out var current) && current.ExpiresAt <= DateTimeOffset.UtcNow) await ResumeLeaseLockedAsync(current, expired: true); }
                    catch (Exception exception) { logger.LogWarning(exception, "Save resumption will be retried"); }
                    finally { gate.Release(); }
                }));
                await Task.Delay(MaintenanceInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task RetainLogsAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                try { await PruneLogsAsync(token); _storageErrors.TryRemove("retention", out _); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { _storageErrors["retention"] = "Runtime console retention failed."; logger.LogWarning(exception, "Console retention failed"); }
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    internal async Task PruneLogsAsync(CancellationToken token)
    {
        await using var connection = new SqliteConnection($"Data Source={paths.ConsoleDatabase};Default Timeout=1");
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Lines WHERE Timestamp < $cutoff OR Sequence IN (SELECT Sequence FROM (SELECT Sequence, row_number() OVER (PARTITION BY ServerId ORDER BY Sequence DESC) AS position FROM Lines) WHERE position > $limit); PRAGMA wal_checkpoint(PASSIVE);";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-Math.Max(1, Settings.ConsoleRetentionDays)).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", Math.Max(1, Settings.ConsoleLinesPerServer));
        await command.ExecuteNonQueryAsync(token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var id in _recoveries.Keys) CancelRecovery(id, true);
        await StopAllAsync(CancellationToken.None);
        _logQueue.Writer.TryComplete();
        await _maintenanceStop.CancelAsync();
        await Task.WhenAll(_logWorker, _maintenanceWorker, _retentionWorker);
    }
}
