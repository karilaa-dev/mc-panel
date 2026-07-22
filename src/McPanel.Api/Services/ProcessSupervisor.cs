using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Hubs;
using McPanel.Api.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed record ServerRuntimeMetrics(
    double CpuPercent, double MemoryUsedMb, double MemoryPeakMb, double SwapUsedMb,
    double AnonymousMemoryMb, double FileMemoryMb, double KernelMemoryMb, double SocketMemoryMb,
    bool MemoryEnforced, long UptimeSeconds);

public interface IServerProcessStatus
{
    bool IsRunning(Guid id);
}

public sealed class ProcessSupervisor(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    ConsoleService console,
    AsyncKeyedLock keyedLock,
    JavaDiscoveryService javaDiscovery,
    CgroupMemoryService cgroups,
    PersistentRuntimeClient persistentRuntime,
    OperationQueue operations,
    IOptions<PanelOptions> options,
    IHubContext<PanelHub> hub,
    SessionAudience audience,
    IHostApplicationLifetime lifetime,
    ILogger<ProcessSupervisor> logger) : BackgroundService, IServerProcessStatus
{
    public static IReadOnlyList<string> AikarFlags { get; } = new[]
    {
        "-XX:+UseG1GC",
        "-XX:+ParallelRefProcEnabled",
        "-XX:MaxGCPauseMillis=200",
        "-XX:+UnlockExperimentalVMOptions",
        "-XX:+DisableExplicitGC",
        "-XX:+AlwaysPreTouch",
        "-XX:G1NewSizePercent=30",
        "-XX:G1MaxNewSizePercent=40",
        "-XX:G1HeapRegionSize=8M",
        "-XX:G1ReservePercent=20",
        "-XX:G1HeapWastePercent=5",
        "-XX:G1MixedGCCountTarget=4",
        "-XX:InitiatingHeapOccupancyPercent=15",
        "-XX:G1MixedGCLiveThresholdPercent=90",
        "-XX:G1RSetUpdatingPauseTimePercent=5",
        "-XX:SurvivorRatio=32",
        "-XX:+PerfDisableSharedMem",
        "-XX:MaxTenuringThreshold=1",
        "-Dusing.aikars.flags=https://mcflags.emc.gs",
        "-Daikars.new.flags=true"
    };
    private readonly ConcurrentDictionary<Guid, ManagedProcess> _processes = new();
    private readonly SemaphoreSlim _memoryAdmission = new(1, 1);
    private readonly ConcurrentDictionary<Guid, RuntimeProcessState> _runtimeStates = new();
    private bool _persistentInitialized;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (persistentRuntime.Enabled)
        {
            IReadOnlyList<RuntimeServerSnapshot>? snapshots = null;
            Exception? lastFailure = null;
            for (var attempt = 0; attempt < 40 && snapshots is null; attempt++)
            {
                try { snapshots = await persistentRuntime.RefreshAsync(cancellationToken); }
                catch (PanelException exception) when (exception.Code == "RUNTIME_UNAVAILABLE")
                {
                    lastFailure = exception;
                    await Task.Delay(250, cancellationToken);
                }
            }
            if (snapshots is null) throw lastFailure ?? new InvalidOperationException("The persistent runtime did not become ready.");
            await ReconcilePersistentAsync(snapshots, true, cancellationToken);
            await RelayRuntimeConsoleAsync(cancellationToken);
            _persistentInitialized = true;
        }
        await base.StartAsync(cancellationToken);
    }

    public async Task<JobDto> QueueActionAsync(Guid id, string action, bool confirmKill, CancellationToken cancellationToken)
    {
        var normalized = action.ToLowerInvariant();
        if (normalized == "kill" && !confirmKill) throw PanelProblems.Validation("Emergency kill requires confirm=true.");
        if (normalized is not ("start" or "stop" or "restart" or "kill")) throw PanelProblems.Validation("Unknown server action.");
        using (await keyedLock.AcquireAsync(id, cancellationToken))
        {
            await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                ?? throw PanelProblems.NotFound("Server");
            if (await db.Jobs.AsNoTracking().AnyAsync(x => x.ServerId == id && (x.State == JobState.Queued || x.State == JobState.Running), cancellationToken))
                throw PanelProblems.Conflict("SERVER_BUSY", "Another operation is already running for this server.");
            if (normalized == "start") EnsurePortAvailable(server.Port);
            return await operations.EnqueueAsync(char.ToUpperInvariant(normalized[0]) + normalized[1..], id, async (_, _, token) =>
            {
                switch (normalized)
                {
                    case "start": await StartAsync(id, false, token); break;
                    case "stop": await StopAsync(id, token); break;
                    case "restart": await RestartAsync(id, token); break;
                    case "kill": await KillAsync(id, token); break;
                    default: throw PanelProblems.Validation("Unknown server action.");
                }
            }, cancellationToken);
        }
    }

    public async Task StartAsync(Guid id, bool recovery, CancellationToken cancellationToken)
    {
        if (persistentRuntime.Enabled) { await StartPersistentAsync(id, recovery, cancellationToken); return; }
        StartAttempt attempt;
        using (await keyedLock.AcquireAsync(id, cancellationToken))
            attempt = await BeginStartLockedAsync(id, recovery, cancellationToken);
        await CompleteStartAsync(id, attempt, cancellationToken);
    }

    private async Task StartPersistentAsync(Guid id, bool recovery, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(id, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (persistentRuntime.IsRunning(id) || server.State is ServerState.Running or ServerState.Starting or ServerState.Stopping)
            throw PanelProblems.Conflict("SERVER_BUSY", "The server already has a managed process.");
        if (server.State is ServerState.Installing or ServerState.Updating or ServerState.BackingUp or ServerState.Error)
            throw PanelProblems.Conflict("SERVER_BUSY", "The server cannot be started in its current state.");
        if (server.MemoryMb < PanelOptions.MinimumServerMemoryMb || server.MemoryLimitMb < PanelOptions.MinimumServerTotalMemoryMb || server.MemoryMb >= server.MemoryLimitMb)
            throw new PanelException(409, "MEMORY_LIMIT_TOO_LOW", "The configured server memory is below the supported minimum.",
                $"Allocate at least {PanelOptions.MinimumServerTotalMemoryMb} MiB total and leave native-memory headroom above the Java heap.");
        var java = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == server.JavaRuntimeId, cancellationToken)
            ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
        var probed = await javaDiscovery.ProbeAsync(java.Path, java.IsCustom, cancellationToken);
        var required = server.RequiredJavaMajor > 0 ? server.RequiredJavaMajor : RequiredJava(server.Version);
        if (probed.Major < required) throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Minecraft {server.Version} requires Java {required} or newer.");
        if (server.Kind == ServerKind.Forge && required == 8 && probed.Major != 8)
            throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Legacy Forge for Minecraft {server.Version} requires Java 8.");
        var instance = paths.Instance(id);
        var launchTarget = ResolveLaunchTarget(instance, server.LaunchTarget);
        if (!File.Exists(launchTarget)) throw new PanelException(409, "OPERATION_FAILED", "The server launch target is missing.");
        await _memoryAdmission.WaitAsync(cancellationToken);
        try
        {
            var allocationMb = await db.Servers.Where(x => x.Id != id && (x.State == ServerState.Running || x.State == ServerState.Starting)).SumAsync(x => (long)x.MemoryLimitMb, cancellationToken);
            var total = HostMetricsService.ReadMemory().Total;
            if ((allocationMb + server.MemoryLimitMb) * 1024 * 1024 > total * options.Value.MemoryAllocationFraction)
                throw new PanelException(409, "MEMORY_LIMIT_EXCEEDED", "Starting this server would exceed the host memory allocation limit.");
            EnsurePortAvailable(server.Port);
            var startInfo = BuildStartInfo(server, java.Path, instance);
            server.State = ServerState.Starting; server.ProcessId = null; server.UpdatedAt = DateTimeOffset.UtcNow;
            if (!recovery) server.CrashAttempts = 0;
            await db.SaveChangesAsync(cancellationToken); await PublishStateAsync(server, cancellationToken);
            RuntimeServerSnapshot snapshot;
            try
            {
                snapshot = await persistentRuntime.StartAsync(new RuntimeLaunchRequest(id, java.Path, paths.Instance(id), startInfo.ArgumentList.ToList(), server.MemoryLimitMb, options.Value.GracefulStopSeconds), cancellationToken);
            }
            catch
            {
                server.State = ServerState.Crashed; server.ProcessId = null; server.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None); await PublishStateAsync(server, CancellationToken.None);
                throw;
            }
            server.State = snapshot.State == RuntimeProcessState.Running ? ServerState.Running : ServerState.Starting;
            server.ProcessId = snapshot.ProcessId; server.StartedAt = snapshot.StartedAt;
            server.RestartRequired = false; server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken); await PublishStateAsync(server, cancellationToken);
            _runtimeStates[id] = snapshot.State;
        }
        finally { _memoryAdmission.Release(); }
    }

    private async Task StopPersistentAsync(Guid id, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(id, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (!persistentRuntime.IsRunning(id))
        {
            if (server.State == ServerState.Stopped) return;
            if (server.State == ServerState.Crashed)
            {
                server.State = ServerState.Stopped; server.CrashAttempts = 0; server.StartedAt = null; server.ProcessId = null;
                await db.SaveChangesAsync(cancellationToken); await PublishStateAsync(server, cancellationToken); return;
            }
            throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server is not running.");
        }
        server.State = ServerState.Stopping; server.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken); await PublishStateAsync(server, cancellationToken);
        var snapshot = await persistentRuntime.StopAsync(id, cancellationToken);
        ApplySnapshot(server, snapshot); server.CrashAttempts = 0;
        await db.SaveChangesAsync(cancellationToken); await PublishStateAsync(server, cancellationToken);
        _runtimeStates[id] = snapshot.State;
    }

    private async Task KillPersistentAsync(Guid id, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(id, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (!persistentRuntime.IsRunning(id)) throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server is not running.");
        var snapshot = await persistentRuntime.KillAsync(id, cancellationToken);
        ApplySnapshot(server, snapshot); server.CrashAttempts = 0;
        await db.SaveChangesAsync(cancellationToken); await PublishStateAsync(server, cancellationToken);
        _runtimeStates[id] = snapshot.State;
    }

    private async Task ExecutePersistentAsync(CancellationToken stoppingToken)
    {
        var initial = !_persistentInitialized;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshots = initial ? await persistentRuntime.RefreshAsync(stoppingToken) : await persistentRuntime.SubscribeAsync(stoppingToken);
                await ReconcilePersistentAsync(snapshots, initial, stoppingToken);
                initial = false;
                await RelayRuntimeConsoleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Persistent runtime reconciliation failed; retrying"); }
            try { await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }

        await using var db = await stateFactory.CreateDbContextAsync();
        var keepRunning = await db.Admins.Select(x => x.KeepServersRunningOnPanelStop).SingleOrDefaultAsync();
        if (!keepRunning)
        {
            try
            {
                var stops = (await persistentRuntime.RefreshAsync(CancellationToken.None)).Where(x => PersistentRuntimeClient.IsActive(x.State)).Select(x => StopForShutdownAsync(x.ServerId)).ToArray();
                await Task.WhenAll(stops);
            }
            catch (Exception exception) { logger.LogWarning(exception, "Could not apply stop-on-panel-shutdown policy"); }
        }
    }

    private async Task ReconcilePersistentAsync(IReadOnlyList<RuntimeServerSnapshot> snapshots, bool initial, CancellationToken cancellationToken)
    {
        var byId = snapshots.ToDictionary(x => x.ServerId);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var servers = await db.Servers.ToListAsync(cancellationToken);
        var known = servers.Select(x => x.Id).ToHashSet();
        foreach (var orphan in snapshots.Where(x => !known.Contains(x.ServerId) && PersistentRuntimeClient.IsActive(x.State)))
        {
            try { await persistentRuntime.StopAsync(orphan.ServerId, cancellationToken); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not stop orphan runtime server {ServerId}", orphan.ServerId); }
        }
        foreach (var server in servers)
        {
            var oldState = server.State; var oldProcessId = server.ProcessId; var oldStartedAt = server.StartedAt;
            var previous = _runtimeStates.GetValueOrDefault(server.Id, RuntimeProcessState.Stopped);
            if (byId.TryGetValue(server.Id, out var snapshot))
            {
                ApplySnapshot(server, snapshot, preserveOperationState: !initial);
                _runtimeStates[server.Id] = snapshot.State;
                if (!initial && PersistentRuntimeClient.IsActive(previous) && snapshot.State == RuntimeProcessState.Crashed)
                {
                    server.CrashAttempts++;
                    if (server.CrashRecovery && server.CrashAttempts <= 3) SchedulePersistentRecovery(server.Id, server.CrashAttempts);
                }
            }
            else if (server.State is ServerState.Running or ServerState.Starting or ServerState.Stopping or ServerState.BackingUp or ServerState.Updating)
            {
                server.State = ServerState.Crashed; server.ProcessId = null;
            }
            if (server.State != oldState || server.ProcessId != oldProcessId || server.StartedAt != oldStartedAt)
            {
                server.UpdatedAt = DateTimeOffset.UtcNow;
                await PublishStateAsync(server, cancellationToken);
            }
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(cancellationToken);
        if (initial)
        {
            var startIds = servers.Where(x => x.StartOnBoot && x.State == ServerState.Stopped).Select(x => x.Id).ToList();
            foreach (var id in startIds)
            {
                try { await StartPersistentAsync(id, false, cancellationToken); }
                catch (Exception exception) { logger.LogError(exception, "Start-on-boot failed for {ServerId}", id); }
            }
        }
    }

    private void SchedulePersistentRecovery(Guid id, int attempt)
    {
        var delay = new[] { 5, 15, 60 }[attempt - 1];
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.ApplicationStopping);
                await StartPersistentAsync(id, true, lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Persistent crash recovery failed for {ServerId}", id); }
        });
    }

    private async Task RelayRuntimeConsoleAsync(CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var admin = await db.Admins.SingleOrDefaultAsync(cancellationToken);
        if (admin is null) return;
        while (true)
        {
            await console.NormalizeRuntimeServerIdsAsync(admin.LastConsoleSequence, cancellationToken);
            var lines = await console.ReadGlobalAsync(admin.LastConsoleSequence, 500, cancellationToken);
            if (lines.Count == 0) break;
            await console.PublishExistingAsync(lines, cancellationToken);
            admin.LastConsoleSequence = lines[^1].Sequence;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static void ApplySnapshot(ServerEntity server, RuntimeServerSnapshot snapshot, bool preserveOperationState = false)
    {
        server.State = snapshot.State switch
        {
            RuntimeProcessState.Starting => ServerState.Starting,
            RuntimeProcessState.Running => preserveOperationState && server.State is (ServerState.BackingUp or ServerState.Updating) ? server.State : ServerState.Running,
            RuntimeProcessState.Stopping => ServerState.Stopping,
            RuntimeProcessState.Crashed => ServerState.Crashed,
            _ => ServerState.Stopped
        };
        server.ProcessId = PersistentRuntimeClient.IsActive(snapshot.State) ? snapshot.ProcessId : null;
        server.StartedAt = snapshot.State == RuntimeProcessState.Stopped ? null : snapshot.StartedAt;
    }

    private async Task<StartAttempt> BeginStartLockedAsync(Guid id, bool recovery, CancellationToken cancellationToken)
    {
        var readinessCursor = await console.LatestSequenceAsync(id, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (_processes.ContainsKey(id) || server.State is ServerState.Running or ServerState.Starting or ServerState.Stopping)
            throw PanelProblems.Conflict("SERVER_BUSY", "The server already has a managed process.");
        if (server.State is ServerState.Installing or ServerState.Updating or ServerState.BackingUp or ServerState.Error)
            throw PanelProblems.Conflict("SERVER_BUSY", "The server cannot be started in its current state.");
        if (server.MemoryMb < PanelOptions.MinimumServerMemoryMb || server.MemoryLimitMb < PanelOptions.MinimumServerTotalMemoryMb || server.MemoryMb >= server.MemoryLimitMb)
            throw new PanelException(409, "MEMORY_LIMIT_TOO_LOW", "The configured server memory is below the supported minimum.",
                $"Allocate at least {PanelOptions.MinimumServerTotalMemoryMb} MiB total and leave native-memory headroom above the Java heap.");
        var runtime = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == server.JavaRuntimeId, cancellationToken)
            ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
        var probed = await javaDiscovery.ProbeAsync(runtime.Path, runtime.IsCustom, cancellationToken);
        var required = server.RequiredJavaMajor > 0 ? server.RequiredJavaMajor : RequiredJava(server.Version);
        if (probed.Major < required)
            throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Minecraft {server.Version} requires Java {required} or newer.");
        if (server.Kind == ServerKind.Forge && required == 8 && probed.Major != 8)
            throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Legacy Forge for Minecraft {server.Version} requires Java 8.");
        var instance = paths.Instance(id);
        var launchTarget = ResolveLaunchTarget(instance, server.LaunchTarget);
        if (!File.Exists(launchTarget)) throw new PanelException(409, "OPERATION_FAILED", "The server launch target is missing.");
        await _memoryAdmission.WaitAsync(cancellationToken);
        Process process;
        ManagedProcess managedProcess;
        CgroupWorkload? workload = null;
        try
        {
            var allocationMb = await db.Servers.Where(x => x.Id != id && (x.State == ServerState.Running || x.State == ServerState.Starting)).SumAsync(x => (long)x.MemoryLimitMb, cancellationToken);
            var total = HostMetricsService.ReadMemory().Total;
            if ((allocationMb + server.MemoryLimitMb) * 1024 * 1024 > total * options.Value.MemoryAllocationFraction)
                throw new PanelException(409, "MEMORY_LIMIT_EXCEEDED", "Starting this server would exceed the host memory allocation limit.");
            EnsurePortAvailable(server.Port);
            workload = cgroups.Create(id, server.MemoryLimitMb);
            var start = cgroups.Wrap(BuildStartInfo(server, runtime.Path, instance), workload);
            server.State = ServerState.Starting; server.ProcessId = null; server.UpdatedAt = DateTimeOffset.UtcNow;
            if (!recovery) server.CrashAttempts = 0;
            await db.SaveChangesAsync(cancellationToken);
            await PublishStateAsync(server, cancellationToken);
            try { process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null."); }
            catch (Exception exception)
            {
                cgroups.Remove(workload);
                server.State = ServerState.Crashed; server.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(cancellationToken);
                throw new PanelException(500, "OPERATION_FAILED", "Java could not start.", exception.Message);
            }
            managedProcess = new ManagedProcess(process, DateTimeOffset.UtcNow, workload, cgroups.Read(workload)?.OomKillCount ?? 0);
            if (!_processes.TryAdd(id, managedProcess))
            {
                process.Kill(true); process.Dispose(); cgroups.Remove(workload); throw PanelProblems.Conflict("SERVER_BUSY", "A managed process already exists.");
            }
            _ = PumpAsync(id, process.StandardOutput, "stdout", CancellationToken.None);
            _ = PumpAsync(id, process.StandardError, "stderr", CancellationToken.None);
            _ = MonitorAsync(id, managedProcess);
            server.State = ServerState.Starting; server.ProcessId = process.Id; server.StartedAt = managedProcess.StartedAt;
            server.RestartRequired = false; server.UpdatedAt = DateTimeOffset.UtcNow;
            try { await db.SaveChangesAsync(cancellationToken); }
            catch
            {
                managedProcess.RequestStop();
                try { process.Kill(true); } catch { }
                throw;
            }
        }
        catch
        {
            if (!_processes.ContainsKey(id)) cgroups.Remove(workload);
            throw;
        }
        finally { _memoryAdmission.Release(); }
        try { await console.AppendAsync(id, "system", $"Started Java process {process.Id} using Java {probed.Major}.", cancellationToken); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not persist start message for {ServerId}", id); }
        await PublishStateAsync(server, cancellationToken);
        return new StartAttempt(readinessCursor, managedProcess);
    }

    private async Task CompleteStartAsync(Guid id, StartAttempt attempt, CancellationToken cancellationToken)
    {
        if (!_processes.TryGetValue(id, out var managed) || !ReferenceEquals(managed, attempt.Managed))
            throw new PanelException(500, "OPERATION_FAILED", "The Java process exited during startup.");
        var readyTask = console.WaitForAsync(id, attempt.ReadinessCursor,
            line => line.Text.Contains("Done (", StringComparison.OrdinalIgnoreCase) || line.Text.Contains("For help, type", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(90), cancellationToken);
        var first = await Task.WhenAny(readyTask, managed.Exit.Task);
        if (first == managed.Exit.Task) throw new PanelException(500, "OPERATION_FAILED", "The Java process exited before Minecraft became ready.");
        var ready = await readyTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (!ready)
        {
            try { await console.AppendAsync(id, "system", "Minecraft readiness text was not detected after 90 seconds; treating the running process as ready.", cancellationToken); }
            catch { }
        }
        using var readyLock = await keyedLock.AcquireAsync(id, cancellationToken);
        await using var readyDb = await stateFactory.CreateDbContextAsync(cancellationToken);
        var current = await readyDb.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (!_processes.TryGetValue(id, out var currentManaged) || !ReferenceEquals(currentManaged, attempt.Managed) || current.State != ServerState.Starting)
            throw new PanelException(409, "OPERATION_FAILED", "The server stopped while Minecraft was starting.");
        current.State = ServerState.Running; current.UpdatedAt = DateTimeOffset.UtcNow;
        await readyDb.SaveChangesAsync(cancellationToken);
        await PublishStateAsync(current, cancellationToken);
    }

    public async Task StopAsync(Guid id, CancellationToken cancellationToken)
    {
        if (persistentRuntime.Enabled) { await StopPersistentAsync(id, cancellationToken); return; }
        ManagedProcess managed;
        using (await keyedLock.AcquireAsync(id, cancellationToken))
        {
            await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
            var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
            if (!_processes.TryGetValue(id, out managed!))
            {
                if (server.State == ServerState.Stopped) return;
                if (server.State == ServerState.Crashed)
                {
                    server.State = ServerState.Stopped; server.CrashAttempts = 0; server.StartedAt = null;
                    await db.SaveChangesAsync(cancellationToken);
                    await console.AppendAsync(id, "system", "Pending crash recovery was cancelled.", cancellationToken);
                    return;
                }
                throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server is not running.");
            }
            managed.RequestStop(); server.State = ServerState.Stopping; server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await WriteCommandInternalAsync(managed, "stop", cancellationToken);
            await console.AppendAsync(id, "system", "Graceful stop requested.", cancellationToken);
            await PublishStateAsync(server, cancellationToken);
        }
        var completed = await Task.WhenAny(managed.Exit.Task, Task.Delay(TimeSpan.FromSeconds(options.Value.GracefulStopSeconds), cancellationToken));
        if (completed != managed.Exit.Task)
        {
            await console.AppendAsync(id, "system", "Graceful stop timed out; killing the process tree.", CancellationToken.None);
            try { managed.Process.Kill(true); } catch (Exception exception) { logger.LogWarning(exception, "Could not kill server {ServerId}", id); }
        }
        await managed.Exit.Task.WaitAsync(cancellationToken);
    }

    public async Task RestartAsync(Guid id, CancellationToken cancellationToken)
    {
        if (persistentRuntime.Enabled)
        {
            if (persistentRuntime.IsRunning(id)) await StopPersistentAsync(id, cancellationToken);
            await StartPersistentAsync(id, false, cancellationToken);
            return;
        }
        if (_processes.ContainsKey(id)) await StopAsync(id, cancellationToken);
        await StartAsync(id, false, cancellationToken);
    }

    public async Task KillAsync(Guid id, CancellationToken cancellationToken)
    {
        if (persistentRuntime.Enabled) { await KillPersistentAsync(id, cancellationToken); return; }
        ManagedProcess managed;
        using (await keyedLock.AcquireAsync(id, cancellationToken))
        {
            await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
            _ = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
            if (!_processes.TryGetValue(id, out managed!)) throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server is not running.");
            managed.RequestStop();
            try { managed.Process.Kill(true); }
            catch (Exception exception) { throw new PanelException(500, "OPERATION_FAILED", "The process tree could not be killed.", exception.Message); }
        }
        await console.AppendAsync(id, "system", "Emergency process-tree kill requested.", cancellationToken);
        await managed.Exit.Task.WaitAsync(cancellationToken);
    }

    public async Task CommandAsync(Guid id, string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4096 || command.Any(x => x is '\r' or '\n' or '\0'))
            throw PanelProblems.Validation("Commands must be one non-empty line of at most 4096 characters.");
        if (command.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase)) { await StopAsync(id, cancellationToken); return; }
        if (persistentRuntime.Enabled)
        {
            if (!persistentRuntime.IsRunning(id)) throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server is not running.");
            await persistentRuntime.CommandAsync(id, command, cancellationToken);
            return;
        }
        if (!_processes.TryGetValue(id, out var managed)) throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server is not running.");
        await WriteCommandInternalAsync(managed, command, cancellationToken);
    }

    public ServerRuntimeMetrics GetMetrics(Guid id)
    {
        if (persistentRuntime.Enabled)
        {
            var snapshot = persistentRuntime.Get(id);
            return snapshot is null ? new(0, 0, 0, 0, 0, 0, 0, 0, false, 0) : new(snapshot.CpuPercent, snapshot.MemoryUsedMb, snapshot.MemoryPeakMb, snapshot.SwapUsedMb,
                snapshot.AnonymousMemoryMb, snapshot.FileMemoryMb, snapshot.KernelMemoryMb, snapshot.SocketMemoryMb, snapshot.MemoryEnforced, snapshot.UptimeSeconds);
        }
        if (!_processes.TryGetValue(id, out var managed)) return new(0, 0, 0, 0, 0, 0, 0, 0, cgroups.Available, 0);
        try
        {
            if (managed.Process.HasExited) return new(0, 0, 0, 0, 0, 0, 0, 0, managed.Cgroup is not null, 0);
            lock (managed.MetricsLock)
            {
                managed.Process.Refresh();
                var now = DateTimeOffset.UtcNow;
                var cpu = managed.Process.TotalProcessorTime;
                var elapsedMs = (now - managed.LastMetricAt).TotalMilliseconds;
                var cpuPercent = elapsedMs <= 0 ? managed.LastCpuPercent : Math.Clamp((cpu - managed.LastCpuTime).TotalMilliseconds / elapsedMs / Environment.ProcessorCount * 100, 0, 100);
                managed.LastMetricAt = now; managed.LastCpuTime = cpu; managed.LastCpuPercent = cpuPercent;
                var memory = cgroups.Read(managed.Cgroup);
                return new(cpuPercent,
                    (memory?.CurrentBytes ?? managed.Process.WorkingSet64) / 1024d / 1024d,
                    (memory?.PeakBytes ?? managed.Process.PeakWorkingSet64) / 1024d / 1024d,
                    (memory?.SwapBytes ?? 0) / 1024d / 1024d,
                    (memory?.AnonymousBytes ?? 0) / 1024d / 1024d,
                    (memory?.FileBytes ?? 0) / 1024d / 1024d,
                    (memory?.KernelBytes ?? 0) / 1024d / 1024d,
                    (memory?.SocketBytes ?? 0) / 1024d / 1024d,
                    managed.Cgroup is not null,
                    Math.Max(0, (long)(now - managed.StartedAt).TotalSeconds));
            }
        }
        catch { return new(0, 0, 0, 0, 0, 0, 0, 0, managed.Cgroup is not null, Math.Max(0, (long)(DateTimeOffset.UtcNow - managed.StartedAt).TotalSeconds)); }
    }

    public bool IsRunning(Guid id) => persistentRuntime.Enabled ? persistentRuntime.IsRunning(id) : _processes.ContainsKey(id);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (persistentRuntime.Enabled) { await ExecutePersistentAsync(stoppingToken); return; }
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            await using var db = await stateFactory.CreateDbContextAsync(stoppingToken);
            var ids = await db.Servers.Where(x => x.StartOnBoot && x.State == ServerState.Stopped).Select(x => x.Id).ToListAsync(stoppingToken);
            foreach (var id in ids)
            {
                try { await StartAsync(id, false, stoppingToken); }
                catch (Exception exception) { logger.LogError(exception, "Start-on-boot failed for {ServerId}", id); }
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        var stops = _processes.Keys.Select(id => StopForShutdownAsync(id)).ToArray();
        await Task.WhenAll(stops);
    }

    private async Task StopForShutdownAsync(Guid id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(options.Value.GracefulStopSeconds + 5));
        try { await StopAsync(id, timeout.Token); }
        catch (Exception exception) { logger.LogWarning(exception, "Shutdown stop failed for {ServerId}", id); }
    }

    private async Task MonitorAsync(Guid id, ManagedProcess managed)
    {
        try { await managed.Process.WaitForExitAsync(); }
        catch (Exception exception) { logger.LogDebug(exception, "WaitForExit failed for {ServerId}", id); }
        var exitCode = managed.Process.HasExited ? managed.Process.ExitCode : -1;
        var finalMemory = cgroups.Read(managed.Cgroup);
        var memoryLimitExceeded = finalMemory is not null && finalMemory.OomKillCount > managed.InitialOomKillCount;
        _processes.TryRemove(new KeyValuePair<Guid, ManagedProcess>(id, managed));
        var requested = managed.StopRequested;
        managed.Process.Dispose();
        cgroups.Remove(managed.Cgroup);
        int attempt = 0;
        bool recover = false;
        try
        {
            using var serverLock = await keyedLock.AcquireAsync(id);
            await using var db = await stateFactory.CreateDbContextAsync();
            var server = await db.Servers.FindAsync(id);
            if (server is null) return;
            server.ProcessId = null; server.UpdatedAt = DateTimeOffset.UtcNow;
            if (requested)
            {
                server.State = ServerState.Stopped; server.CrashAttempts = 0; server.StartedAt = null;
            }
            else
            {
                if (DateTimeOffset.UtcNow - managed.StartedAt >= TimeSpan.FromMinutes(5)) server.CrashAttempts = 0;
                server.State = ServerState.Crashed; server.CrashAttempts++; attempt = server.CrashAttempts;
                recover = server.CrashRecovery && attempt <= 3;
            }
            await db.SaveChangesAsync();
            await db.Players.Where(x => x.ServerId == id && x.Online).ExecuteUpdateAsync(x => x.SetProperty(p => p.Online, false));
            var exitMessage = requested
                ? $"Server stopped (exit {exitCode})."
                : memoryLimitExceeded
                    ? $"Server exceeded its total RAM limit and was stopped by the cgroup OOM killer (exit {exitCode})."
                    : $"Server exited unexpectedly with code {exitCode}.";
            try { await console.AppendAsync(id, "system", exitMessage); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not persist exit message for {ServerId}", id); }
            await PublishStateAsync(server, CancellationToken.None);
        }
        catch (Exception exception) { logger.LogError(exception, "Could not persist exit state for {ServerId}", id); }
        finally { managed.Exit.TrySetResult(exitCode); }
        if (recover)
        {
            var delay = new[] { 5, 15, 60 }[attempt - 1];
            try { await console.AppendAsync(id, "system", $"Crash recovery attempt {attempt}/3 in {delay} seconds."); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not persist recovery message for {ServerId}", id); }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), lifetime.ApplicationStopping);
                await TryStartRecoveryAsync(id, attempt, lifetime.ApplicationStopping);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
            catch (Exception exception) { logger.LogError(exception, "Crash recovery failed for {ServerId}", id); }
        }
    }

    private async Task TryStartRecoveryAsync(Guid id, int expectedAttempt, CancellationToken cancellationToken)
    {
        StartAttempt attempt;
        using (await keyedLock.AcquireAsync(id, cancellationToken))
        {
            await using (var db = await stateFactory.CreateDbContextAsync(cancellationToken))
            {
                var current = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
                if (current is null || current.State != ServerState.Crashed || !current.CrashRecovery || current.CrashAttempts != expectedAttempt)
                    return;
            }
            attempt = await BeginStartLockedAsync(id, true, cancellationToken);
        }
        await CompleteStartAsync(id, attempt, cancellationToken);
    }

    private async Task PumpAsync(Guid id, StreamReader reader, string stream, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                try { await console.AppendAsync(id, stream, line, cancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { logger.LogError(exception, "Console persistence failed for {ServerId}; continuing to drain {Stream}", id, stream); }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        { logger.LogDebug(exception, "Console stream ended for {ServerId}", id); }
    }

    private static ProcessStartInfo BuildStartInfo(ServerEntity server, string javaPath, string instance)
    {
        var start = new ProcessStartInfo
        {
            FileName = javaPath, WorkingDirectory = instance, UseShellExecute = false,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add($"-Xms{server.InitialMemoryMb}M");
        start.ArgumentList.Add($"-Xmx{server.MemoryMb}M");
        if (server.UseAikarFlags)
            foreach (var argument in AikarFlags) start.ArgumentList.Add(argument);
        foreach (var argument in JvmArgumentParser.Parse(server.JvmArguments)) start.ArgumentList.Add(argument);
        if (server.LaunchMode == LaunchMode.ArgumentFile) start.ArgumentList.Add("@" + server.LaunchTarget.Replace(Path.DirectorySeparatorChar, '/'));
        else { start.ArgumentList.Add("-jar"); start.ArgumentList.Add(server.LaunchTarget); }
        start.ArgumentList.Add("nogui");
        start.Environment.Remove("JAVA_TOOL_OPTIONS"); start.Environment.Remove("_JAVA_OPTIONS"); start.Environment.Remove("JDK_JAVA_OPTIONS");
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("MCPANEL_", StringComparison.OrdinalIgnoreCase) || x.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        return start;
    }

    internal static string ResolveLaunchTarget(string instance, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new PanelException(409, "OPERATION_FAILED", "The server launch target is invalid.");
        var root = Path.GetFullPath(instance);
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new PanelException(409, "OPERATION_FAILED", "The server launch target is outside the instance.");
        return target;
    }

    private static async Task WriteCommandInternalAsync(ManagedProcess managed, string command, CancellationToken cancellationToken)
    {
        await managed.InputLock.WaitAsync(cancellationToken);
        try { await managed.Process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken); await managed.Process.StandardInput.FlushAsync(cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException) { throw PanelProblems.Conflict("SERVER_NOT_RUNNING", "The server console is no longer available.", exception.Message); }
        finally { managed.InputLock.Release(); }
    }

    internal static void EnsurePortAvailable(int port)
    {
        try { var listener = new TcpListener(IPAddress.Any, port); listener.Start(); listener.Stop(); }
        catch (SocketException) { throw PanelProblems.Conflict("PORT_IN_USE", $"Port {port} is already in use. Choose a different game port in Server properties, then try again."); }
    }

    private async Task PublishStateAsync(ServerEntity server, CancellationToken cancellationToken)
    {
        try { await audience.PublishAsync(group => hub.Clients.Group(group).SendAsync("ServerStateChanged", new { serverId = server.Id, state = server.State.ToString() }, cancellationToken), cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogDebug(exception, "Could not broadcast state for {ServerId}", server.Id); }
    }

    private static int RequiredJava(string version)
    {
        var pieces = version.Split('-', '+')[0].Split('.');
        if (pieces.Length > 0 && int.TryParse(pieces[0], out var calendar) && calendar >= 26) return 25;
        if (pieces.Length < 2 || !int.TryParse(pieces[1], out var minor)) return 21;
        var patch = pieces.Length > 2 && int.TryParse(pieces[2], out var p) ? p : 0;
        return minor > 20 || minor == 20 && patch >= 5 ? 21 : minor >= 18 ? 17 : minor == 17 ? 16 : 8;
    }

    private sealed record StartAttempt(long ReadinessCursor, ManagedProcess Managed);

    private sealed class ManagedProcess(Process process, DateTimeOffset startedAt, CgroupWorkload? cgroup, long initialOomKillCount)
    {
        private int _stopRequested;
        public Process Process { get; } = process;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public CgroupWorkload? Cgroup { get; } = cgroup;
        public long InitialOomKillCount { get; } = initialOomKillCount;
        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim InputLock { get; } = new(1, 1);
        public object MetricsLock { get; } = new();
        public DateTimeOffset LastMetricAt { get; set; } = startedAt;
        public TimeSpan LastCpuTime { get; set; } = process.TotalProcessorTime;
        public double LastCpuPercent { get; set; }
        public bool StopRequested => Volatile.Read(ref _stopRequested) != 0;
        public void RequestStop() => Interlocked.Exchange(ref _stopRequested, 1);
    }
}
