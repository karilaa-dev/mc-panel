using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    IOptions<PanelOptions> options,
    InstancePermissionService? permissions = null,
    ILogger<BackupService>? logger = null)
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

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
        var modpack = BackupModpackState(serverId, backupId);
        if (Directory.Exists(modpack)) Directory.Delete(modpack, true);
        db.Backups.Remove(backup); await db.SaveChangesAsync(cancellationToken);
    }

    private async Task CreateAsync(Guid serverId, Guid jobId, string reason, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await CreateLockedAsync(serverId, jobId, reason, cancellationToken);
    }

    internal async Task<BackupEntity> CreateLockedAsync(Guid serverId, Guid jobId, string reason, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([serverId], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        var running = supervisor.IsRunning(serverId);
        EnsureCreateAllowed(server.State, running);
        var softwareMetadataJson = JsonSerializer.Serialize(
            SoftwareActivationService.SoftwareMetadataSnapshot.Capture(server), MetadataJsonOptions);
        var id = Guid.NewGuid();
        var stage = Path.Combine(paths.Staging, $"backup-{serverId:N}-{id:N}");
        var modpackStage = stage + "-modpack";
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
                if (Directory.Exists(paths.ServerModpack(serverId)))
                    CopySnapshot(paths.ServerModpack(serverId), modpackStage);
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
            var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{id:N}.zip";
            var destination = Path.Combine(directory, fileName);
            var modpackDestination = BackupModpackState(serverId, id);
            var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
            var committed = false;
            try
            {
                ZipFile.CreateFromDirectory(stage, temporary, CompressionLevel.Fastest, false);
                using (var archive = ZipFile.OpenRead(temporary)) ValidateArchiveLimits(archive);
                File.Move(temporary, destination);
                if (Directory.Exists(modpackStage)) Directory.Move(modpackStage, modpackDestination);
                var backup = new BackupEntity
                {
                    Id = id,
                    ServerId = serverId,
                    FileName = fileName,
                    Size = new FileInfo(destination).Length,
                    Reason = reason,
                    SoftwareMetadataJson = softwareMetadataJson
                };
                db.Backups.Add(backup); await db.SaveChangesAsync(cancellationToken);
                committed = true;
                await console.AppendAsync(serverId, "system", $"Backup {fileName} completed.", cancellationToken);
                return backup;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (!committed && File.Exists(destination)) File.Delete(destination);
                if (!committed && Directory.Exists(modpackDestination)) Directory.Delete(modpackDestination, true);
            }
        }
        finally
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (Directory.Exists(modpackStage)) Directory.Delete(modpackStage, true);
        }
    }

    private async Task RestoreAsync(Guid serverId, Guid backupId, Guid jobId, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([serverId], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (server.State != ServerState.Stopped || supervisor.IsRunning(serverId)) throw PanelProblems.Conflict("SERVER_NOT_STOPPED", "Stop the server before restoring a backup.");
        var backup = await db.Backups.AsNoTracking().SingleOrDefaultAsync(x => x.ServerId == serverId && x.Id == backupId, cancellationToken) ?? throw PanelProblems.NotFound("Backup");
        var backupMetadata = ReadSoftwareMetadata(backup);
        var originalMetadata = SoftwareActivationService.SoftwareMetadataSnapshot.Capture(server);
        var modpackBackup = BackupModpackState(serverId, backupId);
        var restoreModpack = backupMetadata?.ModpackName is not null && Directory.Exists(modpackBackup);
        var targetMetadata = (backupMetadata ?? originalMetadata) with { RestartRequired = false };
        if (backupMetadata?.ModpackName is not null && !restoreModpack)
            targetMetadata = WithoutModpack(targetMetadata);
        var archivePath = Path.Combine(paths.ServerBackups(serverId), backup.FileName);
        if (!File.Exists(archivePath)) throw PanelProblems.NotFound("Backup file");
        await operations.ProgressAsync(jobId, 5, "Creating mandatory safety backup", cancellationToken);
        await CreateLockedAsync(serverId, jobId, "Pre-restore safety", cancellationToken);
        var restore = new RestoreTransaction(paths, Guid.NewGuid(), serverId, originalMetadata,
            targetMetadata, backupMetadata is not null, restoreModpack);
        Directory.CreateDirectory(restore.Stage);
        try
        {
            await operations.ProgressAsync(jobId, 40, "Validating and extracting backup", cancellationToken);
            await ExtractSafeAsync(archivePath, restore.Stage, cancellationToken);
            if (restoreModpack) CopySnapshot(modpackBackup, restore.ModpackStage);
            var launchTarget = ProcessSupervisor.ResolveLaunchTarget(restore.Stage, targetMetadata.LaunchTarget);
            if (!File.Exists(launchTarget))
                throw new PanelException(400, "OPERATION_FAILED", "The backup does not contain this server's launch target.");
            restore.Activate();
            if (permissions is not null) await permissions.NormalizeInstanceAsync(serverId, cancellationToken);
            targetMetadata.Restore(server);
            await db.SaveChangesAsync(cancellationToken);
            restore.MarkCommitted();
            try { restore.Commit(); }
            catch (Exception exception)
            {
                logger?.LogWarning(exception,
                    "Backup restore committed for {ServerId}, but cleanup will be retried at startup", serverId);
            }
            try { await console.AppendAsync(serverId, "system", $"Restored backup {backup.FileName}.", cancellationToken); }
            catch (Exception exception)
            { logger?.LogWarning(exception, "Could not append backup restore log for {ServerId}", serverId); }
        }
        catch (Exception exception)
        {
            if (!restore.IsCommitRecorded && restore.IsStarted)
            {
                try
                {
                    restore.RollbackFiles();
                    originalMetadata.Restore(server);
                    await db.SaveChangesAsync(CancellationToken.None);
                    restore.FinishRollback();
                }
                catch (Exception rollbackException)
                {
                    server.State = ServerState.Error;
                    server.ProcessId = null;
                    try { await db.SaveChangesAsync(CancellationToken.None); } catch { }
                    throw new AggregateException(
                        "The backup restore failed and the original server could not be fully recovered.",
                        exception, rollbackException);
                }
            }
            throw;
        }
        finally
        {
            if (!restore.HasJournal) restore.CleanupStaging();
        }
    }

    private static SoftwareActivationService.SoftwareMetadataSnapshot? ReadSoftwareMetadata(BackupEntity backup)
    {
        if (string.IsNullOrWhiteSpace(backup.SoftwareMetadataJson)) return null;
        try
        {
            return JsonSerializer.Deserialize<SoftwareActivationService.SoftwareMetadataSnapshot>(
                       backup.SoftwareMetadataJson, MetadataJsonOptions)
                   ?? throw new JsonException("The metadata document is empty.");
        }
        catch (JsonException)
        {
            throw new PanelException(400, "BACKUP_METADATA_INVALID",
                "The backup's server core metadata is invalid.");
        }
    }

    private static SoftwareActivationService.SoftwareMetadataSnapshot WithoutModpack(
        SoftwareActivationService.SoftwareMetadataSnapshot metadata) => metadata with
    {
        ModpackName = null,
        ModpackVersion = null,
        ProjectId = null,
        VersionId = null,
        ModpackSource = null
    };

    private string BackupModpackState(Guid serverId, Guid backupId) =>
        Path.Combine(paths.ServerBackups(serverId), $".modpack-{backupId:N}");

    public async Task<IReadOnlySet<Guid>> RecoverInterruptedRestoresAsync(
        StateDbContext state, CancellationToken cancellationToken)
    {
        var unrecovered = new HashSet<Guid>();
        if (!Directory.Exists(paths.Staging)) return unrecovered;
        foreach (var journal in Directory.EnumerateFiles(
                     paths.Staging, "backup-restore-*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestoreTransaction? restore = null;
            Guid? serverId = null;
            try
            {
                restore = RestoreTransaction.Open(paths, journal);
                serverId = restore.ServerId;
                var server = await state.Servers.SingleOrDefaultAsync(
                                 entity => entity.Id == restore.ServerId, cancellationToken)
                             ?? throw new InvalidDataException("The restored server no longer exists.");
                if (restore.IsCommitRecorded)
                {
                    restore.TargetMetadata.Restore(server);
                    await state.SaveChangesAsync(cancellationToken);
                    if (permissions is not null)
                        await permissions.NormalizeInstanceAsync(restore.ServerId, cancellationToken);
                    restore.Commit();
                    logger?.LogInformation("Finalized interrupted backup restore for {ServerId}", restore.ServerId);
                }
                else
                {
                    restore.RollbackFiles();
                    restore.OriginalMetadata.Restore(server);
                    await state.SaveChangesAsync(cancellationToken);
                    restore.FinishRollback();
                    logger?.LogWarning("Rolled back interrupted backup restore for {ServerId}", restore.ServerId);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (serverId is { } id)
                {
                    unrecovered.Add(id);
                    try
                    {
                        var server = await state.Servers.SingleOrDefaultAsync(
                            entity => entity.Id == id, cancellationToken);
                        if (server is not null)
                        {
                            server.State = ServerState.Error;
                            server.ProcessId = null;
                            server.UpdatedAt = DateTimeOffset.UtcNow;
                            await state.SaveChangesAsync(cancellationToken);
                        }
                    }
                    catch (Exception stateException) when (stateException is not OperationCanceledException)
                    { logger?.LogError(stateException, "Could not block server {ServerId} after backup restore recovery failed", id); }
                }
                logger?.LogError(exception,
                    "Could not recover backup restore journal {Journal}; preserving it for recovery", journal);
            }
        }
        return unrecovered;
    }

    internal sealed class RestoreTransaction
    {
        private const int JournalVersion = 1;
        private readonly PanelPaths _paths;
        private RestoreJournal _journal;
        private bool _started;
        private bool _finished;

        public RestoreTransaction(
            PanelPaths paths,
            Guid operationId,
            Guid serverId,
            SoftwareActivationService.SoftwareMetadataSnapshot originalMetadata,
            SoftwareActivationService.SoftwareMetadataSnapshot targetMetadata,
            bool changeModpackState,
            bool restoreModpack)
        {
            _paths = paths;
            _journal = new RestoreJournal(JournalVersion, operationId, serverId, false,
                originalMetadata, targetMetadata, changeModpackState, restoreModpack,
                Directory.Exists(paths.ServerModpack(serverId)));
        }

        private RestoreTransaction(PanelPaths paths, RestoreJournal journal)
        {
            _paths = paths;
            _journal = journal;
            _started = true;
        }

        public Guid ServerId => _journal.ServerId;
        public bool IsStarted => _started;
        public bool IsCommitRecorded => _journal.CommitRecorded;
        public bool HasJournal => File.Exists(JournalPath);
        public SoftwareActivationService.SoftwareMetadataSnapshot OriginalMetadata => _journal.OriginalMetadata;
        public SoftwareActivationService.SoftwareMetadataSnapshot TargetMetadata => _journal.TargetMetadata;
        public string Stage => Path.Combine(_paths.Staging, $"backup-restore-stage-{_journal.OperationId:N}");
        public string ModpackStage => Path.Combine(_paths.Staging, $"backup-restore-modpack-stage-{_journal.OperationId:N}");
        private string Old => Path.Combine(_paths.Staging, $"backup-restore-old-{_journal.OperationId:N}");
        private string ModpackOld => Path.Combine(_paths.Staging, $"backup-restore-modpack-old-{_journal.OperationId:N}");
        private string Current => _paths.Instance(ServerId);
        private string CurrentModpack => _paths.ServerModpack(ServerId);
        private string JournalPath => Path.Combine(_paths.Staging, $"backup-restore-{_journal.OperationId:N}.json");

        public void Activate()
        {
            if (_started) throw new InvalidOperationException("The backup restore is already activated.");
            WriteJournal(_journal);
            _started = true;
            Directory.Move(Current, Old);
            Directory.Move(Stage, Current);
            if (!_journal.ChangeModpackState) return;
            if (Directory.Exists(CurrentModpack)) Directory.Move(CurrentModpack, ModpackOld);
            if (_journal.RestoreModpack) Directory.Move(ModpackStage, CurrentModpack);
        }

        public void MarkCommitted()
        {
            if (!_started || _finished) throw new InvalidOperationException("The backup restore is not active.");
            var committed = _journal with { CommitRecorded = true };
            WriteJournal(committed);
            _journal = committed;
        }

        public void Commit()
        {
            if (_finished) return;
            if (!_journal.CommitRecorded) throw new InvalidOperationException("The backup restore is not committed.");
            DeleteDirectory(Old);
            DeleteDirectory(ModpackOld);
            DeleteDirectory(Stage);
            DeleteDirectory(ModpackStage);
            File.Delete(JournalPath);
            _finished = true;
        }

        public void RollbackFiles()
        {
            if (_finished || _journal.CommitRecorded)
                throw new InvalidOperationException("The backup restore cannot be rolled back.");
            RestoreOriginal(Current, Old, originalRequired: true);
            if (_journal.ChangeModpackState)
                RestoreOriginal(CurrentModpack, ModpackOld, _journal.HadOriginalModpack);
            DeleteDirectory(Stage);
            DeleteDirectory(ModpackStage);
        }

        public void FinishRollback()
        {
            if (_journal.CommitRecorded) throw new InvalidOperationException("A committed backup restore cannot finish rollback.");
            File.Delete(JournalPath);
            _finished = true;
        }

        public void CleanupStaging()
        {
            if (HasJournal) return;
            DeleteDirectory(Stage);
            DeleteDirectory(ModpackStage);
        }

        public static RestoreTransaction Open(PanelPaths paths, string journalPath)
        {
            var name = Path.GetFileNameWithoutExtension(journalPath);
            const string prefix = "backup-restore-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out var operationId))
                throw new InvalidDataException("The backup restore journal name is invalid.");
            var journal = JsonSerializer.Deserialize<RestoreJournal>(
                              File.ReadAllText(journalPath), MetadataJsonOptions)
                          ?? throw new InvalidDataException("The backup restore journal is empty.");
            if (journal.Version != JournalVersion || journal.OperationId != operationId ||
                journal.ServerId == Guid.Empty || journal.OriginalMetadata is null || journal.TargetMetadata is null ||
                journal.RestoreModpack && !journal.ChangeModpackState)
                throw new InvalidDataException("The backup restore journal is invalid.");
            return new RestoreTransaction(paths, journal);
        }

        private void WriteJournal(RestoreJournal journal)
        {
            Directory.CreateDirectory(_paths.Staging);
            var temporary = JournalPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, journal, MetadataJsonOptions);
                    stream.Flush(true);
                }
                File.Move(temporary, JournalPath, true);
                ServerImportService.FlushJournalDirectory(_paths.Staging);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        private static void RestoreOriginal(string current, string old, bool originalRequired)
        {
            if (Directory.Exists(old))
            {
                DeleteDirectory(current);
                Directory.Move(old, current);
                return;
            }
            if (!originalRequired)
            {
                DeleteDirectory(current);
                return;
            }
            if (!Directory.Exists(current))
                throw new DirectoryNotFoundException($"The original restore directory is missing: {current}");
        }

        private static void DeleteDirectory(string directory)
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            else if (File.Exists(directory)) throw new IOException($"A file blocks restore recovery: {directory}");
        }

        private sealed record RestoreJournal(
            int Version,
            Guid OperationId,
            Guid ServerId,
            bool CommitRecorded,
            SoftwareActivationService.SoftwareMetadataSnapshot OriginalMetadata,
            SoftwareActivationService.SoftwareMetadataSnapshot TargetMetadata,
            bool ChangeModpackState,
            bool RestoreModpack,
            bool HadOriginalModpack);
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
