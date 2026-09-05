using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed class ServerExportService(PanelPaths paths, IDbContextFactory<StateDbContext> factory, BackupService backups,
    OperationQueue jobs, AsyncKeyedLock locks, IOptions<PanelOptions> options)
{
    public Task<Contracts.JobDto> QueueAsync(Guid id, CancellationToken token) => jobs.EnqueueAsync("ServerExport", id,
        (_, job, cancellation) => CreateAsync(id, job, cancellation), token, inputJson: "{}");
    public string FilePath(Guid job) => Path.Combine(paths.Data, "exports", $"server-{job:N}.zip");

    private async Task CreateAsync(Guid id, Guid job, CancellationToken token)
    {
        using var held = await locks.AcquireAsync(id, token);
        await using var db = await factory.CreateDbContextAsync(token);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw PanelProblems.NotFound("Server");
        if (server.Kind == ServerKind.Gate) throw PanelProblems.Conflict("PANEL_EXPORT_REQUIRED", "Use a panel recovery bundle to preserve Gate's backend relationships and keys.");
        if (server.RecoveryRequired) throw PanelProblems.Conflict("RECOVERY_REQUIRED", "Repair the server before exporting it.");
        var stage = Path.Combine(paths.Staging, $"export-{job:N}"); var file = FilePath(job); var temporary = file + ".partial";
        try
        {
            using var diskReservation = ArchiveIO.ReserveSpace(paths.Data, checked(ArchiveIO.Measure(paths.Instance(id)).Bytes * 3 + 64 * 1024 * 1024), options.Value.ReservedDiskBytes);
            Directory.CreateDirectory(stage); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(stage, (UnixFileMode)448); File.SetUnixFileMode(Path.GetDirectoryName(file)!, (UnixFileMode)448); }
            var backup = await backups.CreateLockedAsync(id, job, "Server export", token);
            await backups.ExtractForRecoveryAsync(Path.Combine(paths.ServerBackups(id), backup.FileName), Path.Combine(stage, "instance"), token);
            if (Directory.Exists(paths.ServerModpack(id))) await ArchiveIO.CopyAsync(paths.ServerModpack(id), Path.Combine(stage, "modpack"), token);
            await File.WriteAllTextAsync(Path.Combine(stage, "server.json"), JsonSerializer.Serialize(server), token);
            await RecoveryArchive.PackAsync(stage, temporary, "server", backup.CreatedAt, token);
            File.Move(temporary, file);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static async Task ImportAsync(string archive, PanelPaths paths, PanelOptions options, CancellationToken token)
    {
        paths.EnsureCreated();
        using var panelLock = new FileStream(paths.StateDatabase + ".panel-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var stage = Path.Combine(paths.Staging, $"export-import-{Guid.NewGuid():N}");
        string? instance = null; string? modpack = null; var committed = false; var movedInstance = false; var movedModpack = false;
        try
        {
            var manifest = await RecoveryArchive.ExtractAsync(archive, stage, options.MaxBackupBytes, options.MaxBackupEntries, options.ReservedDiskBytes, token);
            if (manifest.Kind != "server") throw new InvalidDataException("This command requires a server export.");
            var server = JsonSerializer.Deserialize<ServerEntity>(await File.ReadAllTextAsync(Path.Combine(stage, "server.json"), token)) ?? throw new InvalidDataException("Launch metadata is missing.");
            if (server.Kind == ServerKind.Gate || server.Id == Guid.Empty) throw new InvalidDataException("Unsupported server export.");
            var launch = ProcessSupervisor.ResolveLaunchTarget(Path.Combine(stage, "instance"), server.LaunchTarget);
            if (!File.Exists(launch)) throw new InvalidDataException("The server launch target is missing.");
            await SchemaMigration.MigrateAsync(paths.StateDatabase, token);
            await using var db = new StateDbContext(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={paths.StateDatabase};Pooling=False").Options);
            if (await db.Servers.AnyAsync(x => x.Id == server.Id || x.Port == server.Port || x.Name == server.Name, token))
                throw new InvalidOperationException("The export conflicts with an existing server ID, name, or port. Restore into a clean panel.");
            instance = paths.Instance(server.Id); modpack = paths.ServerModpack(server.Id);
            if (Directory.Exists(instance) || Directory.Exists(modpack)) throw new IOException("Existing server data would be replaced.");
            server.State = ServerState.Stopped; server.ProcessId = null; server.StartedAt = null; server.StartOnBoot = false; server.CrashRecovery = false; server.CrashAttempts = 0;
            Directory.Move(Path.Combine(stage, "instance"), instance); movedInstance = true;
            if (Directory.Exists(Path.Combine(stage, "modpack"))) { Directory.Move(Path.Combine(stage, "modpack"), modpack); movedModpack = true; }
            db.Servers.Add(server);
            db.AuditEvents.Add(new() { Actor = "local-recovery", Action = "server-import", Target = server.Id.ToString(), Outcome = "succeeded" });
            await db.SaveChangesAsync(token); committed = true;
        }
        finally
        {
            if (!committed) { if (movedInstance && instance is not null && Directory.Exists(instance)) Directory.Move(instance, Path.Combine(stage, "instance-rollback")); if (movedModpack && modpack is not null && Directory.Exists(modpack)) Directory.Move(modpack, Path.Combine(stage, "modpack-rollback")); }
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
    }
}
