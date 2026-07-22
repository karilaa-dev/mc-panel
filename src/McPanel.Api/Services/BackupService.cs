using System.IO.Compression;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed class BackupService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    ProcessSupervisor supervisor,
    ConsoleService console,
    OperationQueue operations,
    AsyncKeyedLock keyedLock,
    SafePathResolver resolver,
    IOptions<PanelOptions> options)
{
    public async Task<IReadOnlyList<BackupDto>> ListAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await EnsureServerAsync(serverId, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        return await db.Backups.Where(x => x.ServerId == serverId).OrderByDescending(x => x.CreatedAt)
            .Select(x => new BackupDto(x.Id, x.FileName, x.Size, x.CreatedAt, x.Reason, x.State)).ToListAsync(cancellationToken);
    }

    public async Task<JobDto> QueueCreateAsync(Guid serverId, string reason, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await EnsureCreateAllowedAsync(serverId, cancellationToken);
        return await operations.EnqueueAsync("Backup", serverId, (_, jobId, token) => CreateAsync(serverId, jobId, reason, token), cancellationToken);
    }

    public Task<JobDto> QueueRestoreAsync(Guid serverId, Guid backupId, CancellationToken cancellationToken) =>
        operations.EnqueueAsync("Restore", serverId, (_, jobId, token) => RestoreAsync(serverId, backupId, jobId, token), cancellationToken);

    public async Task RunScheduledAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var job = await QueueCreateAsync(serverId, "Scheduled", cancellationToken);
        await WaitJobAsync(job.Id, cancellationToken);
    }

    public async Task<(string Path, string Name)> DownloadAsync(Guid serverId, Guid backupId, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var backup = await db.Backups.AsNoTracking().SingleOrDefaultAsync(x => x.ServerId == serverId && x.Id == backupId, cancellationToken) ?? throw PanelProblems.NotFound("Backup");
        var path = Path.Combine(paths.ServerBackups(serverId), backup.FileName);
        if (!File.Exists(path)) throw PanelProblems.NotFound("Backup file");
        return (path, backup.FileName);
    }

    public async Task DeleteAsync(Guid serverId, Guid backupId, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var backup = await db.Backups.SingleOrDefaultAsync(x => x.ServerId == serverId && x.Id == backupId, cancellationToken) ?? throw PanelProblems.NotFound("Backup");
        var path = Path.Combine(paths.ServerBackups(serverId), backup.FileName);
        if (File.Exists(path)) File.Delete(path);
        db.Backups.Remove(backup); await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CreateAsync(Guid serverId, Guid jobId, string reason, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await CreateLockedAsync(serverId, jobId, reason, cancellationToken);
    }

    private async Task<BackupEntity> CreateLockedAsync(Guid serverId, Guid jobId, string reason, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([serverId], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        var running = supervisor.IsRunning(serverId);
        EnsureCreateAllowed(server.State, running);
        var stage = Path.Combine(paths.Staging, $"backup-{serverId:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        server.State = ServerState.BackingUp; await db.SaveChangesAsync(cancellationToken);
        var saveDisabled = false;
        try
        {
            try
            {
                if (running)
                {
                    await operations.ProgressAsync(jobId, 10, "Pausing world saves", cancellationToken);
                    var cursor = await console.LatestSequenceAsync(serverId, cancellationToken);
                    saveDisabled = true;
                    await supervisor.CommandAsync(serverId, "save-off", cancellationToken);
                    await supervisor.CommandAsync(serverId, "save-all flush", cancellationToken);
                    var saved = await console.WaitForAsync(serverId, cursor,
                        line => line.Text.Contains("Saved the game", StringComparison.OrdinalIgnoreCase) || line.Text.Contains("Saved the world", StringComparison.OrdinalIgnoreCase),
                        TimeSpan.FromSeconds(30), cancellationToken);
                    if (!saved) throw new PanelException(504, "OPERATION_FAILED", "Minecraft did not confirm a flushed save within 30 seconds.");
                }
                await operations.ProgressAsync(jobId, 35, "Staging a consistent file snapshot", cancellationToken);
                CopySnapshot(paths.Instance(serverId), stage);
            }
            finally
            {
                try
                {
                    if (saveDisabled) await supervisor.CommandAsync(serverId, "save-on", CancellationToken.None);
                }
                catch
                {
                    try { await console.AppendAsync(serverId, "system", "WARNING: save-on could not be sent after backup."); } catch { }
                }
                finally
                {
                    server.State = running ? ServerState.Running : ServerState.Stopped;
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
            await operations.ProgressAsync(jobId, 65, "Compressing snapshot after saves resumed", cancellationToken);
            var directory = paths.ServerBackups(serverId); Directory.CreateDirectory(directory);
            var id = Guid.NewGuid();
            var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{id:N}.zip";
            var destination = Path.Combine(directory, fileName);
            var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
            var committed = false;
            try
            {
                ZipFile.CreateFromDirectory(stage, temporary, CompressionLevel.Fastest, false);
                using (var archive = ZipFile.OpenRead(temporary)) ValidateArchiveLimits(archive);
                File.Move(temporary, destination);
                var backup = new BackupEntity { Id = id, ServerId = serverId, FileName = fileName, Size = new FileInfo(destination).Length, Reason = reason };
                db.Backups.Add(backup); await db.SaveChangesAsync(cancellationToken);
                committed = true;
                await console.AppendAsync(serverId, "system", $"Backup {fileName} completed.", cancellationToken);
                return backup;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (!committed && File.Exists(destination)) File.Delete(destination);
            }
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private async Task RestoreAsync(Guid serverId, Guid backupId, Guid jobId, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([serverId], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (server.State != ServerState.Stopped || supervisor.IsRunning(serverId)) throw PanelProblems.Conflict("SERVER_NOT_STOPPED", "Stop the server before restoring a backup.");
        var backup = await db.Backups.AsNoTracking().SingleOrDefaultAsync(x => x.ServerId == serverId && x.Id == backupId, cancellationToken) ?? throw PanelProblems.NotFound("Backup");
        var archivePath = Path.Combine(paths.ServerBackups(serverId), backup.FileName);
        if (!File.Exists(archivePath)) throw PanelProblems.NotFound("Backup file");
        await operations.ProgressAsync(jobId, 5, "Creating mandatory safety backup", cancellationToken);
        await CreateLockedAsync(serverId, jobId, "Pre-restore safety", cancellationToken);
        var stage = Path.Combine(paths.Staging, $"restore-{serverId:N}-{Guid.NewGuid():N}");
        var old = paths.Instance(serverId) + $".restore-old-{Guid.NewGuid():N}";
        Directory.CreateDirectory(stage);
        try
        {
            await operations.ProgressAsync(jobId, 40, "Validating and extracting backup", cancellationToken);
            await ExtractSafeAsync(archivePath, stage, cancellationToken);
            var launchTarget = ProcessSupervisor.ResolveLaunchTarget(stage, server.LaunchTarget);
            if (!File.Exists(launchTarget))
                throw new PanelException(400, "OPERATION_FAILED", "The backup does not contain this server's launch target.");
            var current = paths.Instance(serverId);
            Directory.Move(current, old);
            try { Directory.Move(stage, current); }
            catch { Directory.Move(old, current); throw; }
            Directory.Delete(old, true);
            server.RestartRequired = false; server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await console.AppendAsync(serverId, "system", $"Restored backup {backup.FileName}.", cancellationToken);
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            // If rollback itself failed, preserve the old directory beside the instance for manual recovery.
        }
    }

    private async Task ExtractSafeAsync(string archivePath, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        ValidateArchiveLimits(archive);
        foreach (var entry in archive.Entries)
        {
            _ = resolver.Resolve(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
        }
        long actual = 0;
        foreach (var entry in archive.Entries)
        {
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new PanelException(400, "PATH_OUTSIDE_SERVER", "Backup contains a symbolic link.");
            var output = resolver.Resolve(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(output); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var source = entry.Open(); await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var buffer = new byte[128 * 1024]; int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                actual = checked(actual + read);
                if (actual > options.Value.MaxExtractedBytes) throw new PanelException(400, "ZIP_LIMIT_EXCEEDED", "The backup expands beyond the safe limit.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
    }

    private void ValidateArchiveLimits(ZipArchive archive)
    {
        if (archive.Entries.Count > options.Value.MaxArchiveEntries)
            throw new PanelException(400, "ZIP_LIMIT_EXCEEDED", "The backup contains too many entries.");
        long declared = 0;
        foreach (var entry in archive.Entries)
        {
            try { declared = checked(declared + entry.Length); }
            catch (OverflowException) { throw new PanelException(400, "ZIP_LIMIT_EXCEEDED", "The backup expands beyond the safe limit."); }
            var excessiveRatio = entry.CompressedLength > 0 && entry.Length > 100L * 1024 * 1024 &&
                (entry.CompressedLength > long.MaxValue / 100 ? false : entry.Length > entry.CompressedLength * 100);
            if (declared > options.Value.MaxExtractedBytes || excessiveRatio)
                throw new PanelException(400, "ZIP_LIMIT_EXCEEDED", "The backup expands beyond the safe limit.");
        }
    }

    private static void CopySnapshot(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Name.Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(destination, info.Name), true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var info = new DirectoryInfo(directory);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            CopySnapshot(directory, Path.Combine(destination, info.Name));
        }
    }

    private async Task EnsureServerAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Servers.AnyAsync(x => x.Id == id, cancellationToken)) throw PanelProblems.NotFound("Server");
    }

    private async Task EnsureCreateAllowedAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw PanelProblems.NotFound("Server");
        EnsureCreateAllowed(server.State, supervisor.IsRunning(id));
    }

    private static void EnsureCreateAllowed(ServerState state, bool processRunning)
    {
        if ((state == ServerState.Running && processRunning) || (state == ServerState.Stopped && !processRunning)) return;
        throw PanelProblems.Conflict("SERVER_BUSY", "The server cannot be backed up in its current state.");
    }

    private async Task WaitJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var job = await operations.GetAsync(jobId, cancellationToken) ?? throw PanelProblems.NotFound("Job");
            if (job.State == JobState.Completed) return;
            if (job.State == JobState.Failed) throw new PanelException(500, "OPERATION_FAILED", "Scheduled backup failed.", job.Error);
            await Task.Delay(500, cancellationToken);
        }
    }
}
