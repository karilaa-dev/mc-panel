using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed class RecoveryBundleService(PanelPaths paths, IDbContextFactory<StateDbContext> factory,
    OperationQueue jobs, IOptions<PanelOptions> options,
    OperationsMonitor monitor, ILogger<RecoveryBundleService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string DirectoryPath => Path.Combine(paths.Data, "recovery");

    public Task<Contracts.JobDto> QueueAsync(CancellationToken token) => jobs.EnqueueAsync("PanelRecovery", null,
        (_, job, cancellation) => CreateAsync(job, cancellation), token, inputJson: "{}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(options.Value.ReplicationDirectory))
                {
                    await using var db = await factory.CreateDbContextAsync(stoppingToken);
                    var latest = await db.RecoveryPoints.OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(stoppingToken);
                    var retryCutoff = DateTimeOffset.UtcNow.AddMinutes(-5);
                    if ((latest is null || latest.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(-options.Value.RecoveryIntervalMinutes) || latest.VerifiedAt is null && latest.CreatedAt < retryCutoff) &&
                        !await db.Jobs.AnyAsync(x => x.Type == "PanelRecovery" && x.CreatedAt > retryCutoff, stoppingToken) &&
                        !await db.Jobs.AnyAsync(x => x.Type == "PanelRecovery" && (x.State == JobState.Queued || x.State == JobState.Running), stoppingToken))
                        await QueueAsync(stoppingToken);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogError(exception, "Recovery scheduling failed"); }
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    internal async Task CreateAsync(Guid jobId, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        var id = Guid.NewGuid(); var stage = Path.Combine(paths.Staging, $"recovery-{id:N}");
        var temporary = Path.Combine(DirectoryPath, $".{id:N}.partial");
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var required = checked(ArchiveIO.Measure(paths.Keys).Bytes + ArchiveIO.Measure(paths.Icons).Bytes + ArchiveIO.Measure(paths.Config).Bytes);
            foreach (var file in new[] { paths.StateDatabase, paths.StateDatabase + "-wal" })
                if (File.Exists(file)) required = checked(required + new FileInfo(file).Length);
            using var diskReservation = ArchiveIO.ReserveSpace(paths.Data, checked(required * 3 + 64 * 1024 * 1024), options.Value.ReservedDiskBytes);
            Directory.CreateDirectory(stage); Directory.CreateDirectory(DirectoryPath);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(stage, (UnixFileMode)448); File.SetUnixFileMode(DirectoryPath, (UnixFileMode)448); }
            var data = Path.Combine(stage, "data"); Directory.CreateDirectory(data);
            var capturedAt = DateTimeOffset.UtcNow;
            foreach (var name in new[] { "keys", "icons" })
                if (Directory.Exists(Path.Combine(paths.Data, name))) await ArchiveIO.CopyAsync(Path.Combine(paths.Data, name), Path.Combine(data, name), token);
            await ArchiveIO.CopyAsync(paths.Config, Path.Combine(stage, "config"), token);
            foreach (var name in new[] { "state.db" })
            {
                if (!File.Exists(Path.Combine(paths.Data, name))) continue;
                await using var source = new SqliteConnection($"Data Source={Path.Combine(paths.Data, name)};Mode=ReadOnly;Pooling=False");
                await source.OpenAsync(token);
                await SchemaMigration.SnapshotAsync(source, Path.Combine(data, name), token);
            }
            // Only panel-owned state belongs to this archive. Instances are exported separately.
            await using (var snapshot = new StateDbContext(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={Path.Combine(data, "state.db")};Pooling=False").Options))
            {
                await snapshot.Servers.ExecuteDeleteAsync(token);
                await snapshot.Backups.ExecuteDeleteAsync(token);
                await snapshot.Schedules.ExecuteDeleteAsync(token);
                await snapshot.ScheduleRuns.ExecuteDeleteAsync(token);
                await snapshot.Players.ExecuteDeleteAsync(token);
                await snapshot.GateBackends.ExecuteDeleteAsync(token);
                await snapshot.GateExternalBackends.ExecuteDeleteAsync(token);
                await snapshot.GateSettings.ExecuteDeleteAsync(token);
                await snapshot.Jobs.ExecuteDeleteAsync(token);
                await snapshot.Incidents.ExecuteDeleteAsync(token);
                await snapshot.RecoveryPoints.ExecuteDeleteAsync(token);
                await snapshot.Admins.ExecuteUpdateAsync(update => update.SetProperty(x => x.LastConsoleSequence, 0), token);
                // Deleted instance records must not remain in SQLite's free pages.
                await snapshot.Database.ExecuteSqlRawAsync("VACUUM;", token);
                await snapshot.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", token);
            }
            await jobs.ProgressAsync(jobId, 80, "Packaging and checksumming recovery data", token);
            await RecoveryArchive.PackAsync(stage, temporary, "panel-settings", capturedAt, token);
            var fileName = $"panel-settings-{capturedAt:yyyyMMddTHHmmss}-{id:N}.zip";
            var destination = Path.Combine(DirectoryPath, fileName); File.Move(temporary, destination);
            var point = new RecoveryPointEntity { Id = id, CreatedAt = capturedAt, FileName = fileName, Size = new FileInfo(destination).Length, Sha256 = await ArchiveIO.Sha256Async(destination, token) };
            db.RecoveryPoints.Add(point); await db.SaveChangesAsync(token);
            try
            {
                if (options.Value.ReplicationDirectory is { Length: > 0 } remote)
                {
                    await jobs.ProgressAsync(jobId, 90, "Replicating recovery bundle and verifying destination", token);
                    await ReplicateAsync(destination, remote, point.Sha256, token);
                    point.ReplicatedAt = DateTimeOffset.UtcNow; point.VerifiedAt = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync(token);
                await RetainAsync(db, token);
                await monitor.SetIncidentAsync("RECOVERY_BUNDLE_FAILED", null, "Panel recovery completed.", false, token);
            }
            catch (Exception exception)
            { point.Error = exception.Message; await db.SaveChangesAsync(CancellationToken.None); throw; }
        }
        catch (Exception exception)
        {
            try { await monitor.SetIncidentAsync("RECOVERY_BUNDLE_FAILED", null, exception.Message, true, CancellationToken.None); } catch { }
            throw;
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); if (File.Exists(temporary)) File.Delete(temporary); }
            finally { _gate.Release(); }
        }
    }

    internal static async Task ReplicateAsync(string source, string remote, string sha256, CancellationToken token)
    {
        if (!Directory.Exists(remote)) throw new IOException("Replication destination must already exist; check the off-host mount.");
        if (ArchiveIO.DataDrive(remote).DriveType != DriveType.Network || ArchiveIO.DataDrive(source).Name == ArchiveIO.DataDrive(remote).Name)
            throw new IOException("Replication requires an off-host network filesystem such as NFS or SMB. Local filesystems do not count as off-host recovery.");
        ArchiveIO.RequireSpace(remote, new FileInfo(source).Length, 0);
        var destination = Path.Combine(remote, Path.GetFileName(source)); var temporary = destination + ".partial";
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            { await input.CopyToAsync(output, token); output.Flush(true); }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, (UnixFileMode)384);
            if (await ArchiveIO.Sha256Async(temporary, token) != sha256) throw new IOException("Replicated recovery checksum failed.");
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task RetainAsync(StateDbContext db, CancellationToken token)
    {
        var points = await db.RecoveryPoints.OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        var protectedId = points.FirstOrDefault(x => x.VerifiedAt != null)?.Id;
        long bytes = 0; int count = 0;
        foreach (var point in points)
        {
            if (point.Id == protectedId || count < Math.Max(2, options.Value.BackupRetentionCount) && bytes + point.Size <= options.Value.BackupRetentionBytes && point.CreatedAt > DateTimeOffset.UtcNow.AddDays(options.Value.BackupRetentionDays * -1))
            { bytes += point.Size; count++; continue; }
            File.Delete(Path.Combine(DirectoryPath, point.FileName));
            // Remote retention belongs to the destination administrator: never prune the off-host last copy.
            db.RecoveryPoints.Remove(point);
        }
        await db.SaveChangesAsync(token);
    }
}
