using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed class RecoveryBundleService(PanelPaths paths, IDbContextFactory<StateDbContext> factory,
    BackupService backups, OperationQueue jobs, AsyncKeyedLock locks, IOptions<PanelOptions> options,
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
        var held = new List<IDisposable>();
        var id = Guid.NewGuid(); var stage = Path.Combine(paths.Staging, $"recovery-{id:N}");
        var temporary = Path.Combine(DirectoryPath, $".{id:N}.partial");
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var servers = await db.Servers.AsNoTracking().OrderBy(x => x.Id).ToListAsync(token);
            foreach (var server in servers) held.Add(await locks.AcquireAsync(server.Id, token));
            servers = await db.Servers.AsNoTracking().Where(x => servers.Select(s => s.Id).Contains(x.Id)).ToListAsync(token);
            if (servers.Any(x => x.RecoveryRequired || x.State is not (ServerState.Stopped or ServerState.Running)))
                throw PanelProblems.Conflict("RECOVERY_BUSY", "Finish installation, updates, or repairs before capturing panel recovery.");
            var required = servers.Sum(x => ArchiveIO.Measure(paths.Instance(x.Id)).Bytes + ArchiveIO.Measure(paths.ServerModpack(x.Id)).Bytes);
            required = checked(required + ArchiveIO.Measure(paths.Keys).Bytes + ArchiveIO.Measure(paths.Icons).Bytes + ArchiveIO.Measure(paths.Config).Bytes);
            foreach (var file in new[] { paths.StateDatabase, paths.StateDatabase + "-wal", paths.ConsoleDatabase, paths.ConsoleDatabase + "-wal" })
                if (File.Exists(file)) required = checked(required + new FileInfo(file).Length);
            using var diskReservation = ArchiveIO.ReserveSpace(paths.Data, checked(required * 3 + 64 * 1024 * 1024), options.Value.ReservedDiskBytes);
            Directory.CreateDirectory(stage); Directory.CreateDirectory(DirectoryPath);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(stage, (UnixFileMode)448); File.SetUnixFileMode(DirectoryPath, (UnixFileMode)448); }
            var data = Path.Combine(stage, "data"); Directory.CreateDirectory(Path.Combine(data, "instances"));
            var capturedAt = DateTimeOffset.UtcNow;
            foreach (var server in servers)
            {
                await jobs.ProgressAsync(jobId, 10, $"Capturing {server.Name}", token);
                var target = Path.Combine(data, "instances", server.Id.ToString("N"));
                if (server.Kind == ServerKind.Gate) await ArchiveIO.CopyAsync(paths.Instance(server.Id), target, token);
                else
                {
                    var backup = await backups.CreateLockedAsync(server.Id, jobId, "Panel recovery", token);
                    await backups.ExtractForRecoveryAsync(Path.Combine(paths.ServerBackups(server.Id), backup.FileName), target, token);
                }
                var modpack = paths.ServerModpack(server.Id);
                if (Directory.Exists(modpack)) await ArchiveIO.CopyAsync(modpack, Path.Combine(data, "modpacks", server.Id.ToString("N")), token);
            }
            foreach (var name in new[] { "keys", "icons" })
                if (Directory.Exists(Path.Combine(paths.Data, name))) await ArchiveIO.CopyAsync(Path.Combine(paths.Data, name), Path.Combine(data, name), token);
            await ArchiveIO.CopyAsync(paths.Config, Path.Combine(stage, "config"), token);
            foreach (var name in new[] { "state.db", "console.db" })
            {
                if (!File.Exists(Path.Combine(paths.Data, name))) continue;
                await using var source = new SqliteConnection($"Data Source={Path.Combine(paths.Data, name)};Mode=ReadOnly;Pooling=False");
                await source.OpenAsync(token);
                await SchemaMigration.SnapshotAsync(source, Path.Combine(data, name), token);
            }
            // Only captured servers belong to this point. Historical backups remain on the source host.
            await using (var snapshot = new StateDbContext(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={Path.Combine(data, "state.db")};Pooling=False").Options))
            {
                var ids = servers.Select(x => x.Id).ToArray();
                await snapshot.Servers.Where(x => !ids.Contains(x.Id)).ExecuteDeleteAsync(token);
                await snapshot.Backups.ExecuteDeleteAsync(token);
                await snapshot.RecoveryPoints.ExecuteDeleteAsync(token);
            }
            foreach (var heldLock in held) heldLock.Dispose(); held.Clear();
            await jobs.ProgressAsync(jobId, 80, "Packaging and checksumming recovery data", token);
            await RecoveryArchive.PackAsync(stage, temporary, "panel", capturedAt, token);
            var fileName = $"panel-{capturedAt:yyyyMMddTHHmmss}-{id:N}.zip";
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
            foreach (var heldLock in held) heldLock.Dispose();
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
