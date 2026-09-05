using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed record InstanceExportRequest(bool All, Guid[]? ServerIds);
internal sealed record InstanceExportMetadata(ServerEntity[] Servers, ScheduleEntity[] Schedules, PlayerEntity[] Players,
    GateSettingsEntity[] GateSettings, GateBackendEntity[] GateBackends, GateExternalBackendEntity[] GateExternalBackends);

public sealed class InstanceExportService(PanelPaths paths, IDbContextFactory<StateDbContext> factory, BackupService backups,
    OperationQueue jobs, AsyncKeyedLock locks, IOptions<PanelOptions> options)
{
    public string FilePath(Guid job) => Path.Combine(paths.Data, "exports", $"instances-{job:N}.zip");

    public async Task<JobDto> QueueAsync(InstanceExportRequest request, CancellationToken token)
    {
        if (request.All && request.ServerIds is { Length: > 0 } || !request.All && request.ServerIds is not { Length: > 0 })
            throw new PanelException(400, "INVALID_SELECTION", "Choose all instances or provide the IDs of selected instances.");
        await using var db = await factory.CreateDbContextAsync(token);
        var ids = request.All ? await db.Servers.Select(x => x.Id).ToArrayAsync(token) : request.ServerIds!.Distinct().ToArray();
        if (ids.Length == 0) throw new PanelException(400, "EMPTY_SELECTION", "There are no instances to export.");
        if (await db.Servers.CountAsync(x => ids.Contains(x.Id), token) != ids.Length) throw PanelProblems.NotFound("Selected instance");
        // Freeze 'all' at acceptance; newly created instances do not silently join this job.
        return await jobs.EnqueueAsync("InstancesExport", null, (_, job, ct) => CreateAsync(ids, job, ct), token,
            inputJson: JsonSerializer.Serialize(new InstanceExportRequest(false, ids)));
    }

    private async Task CreateAsync(Guid[] ids, Guid job, CancellationToken token)
    {
        var held = new List<IDisposable>();
        var stage = Path.Combine(paths.Staging, $"instances-export-{job:N}");
        var file = FilePath(job); var temporary = file + ".partial";
        try
        {
            foreach (var id in ids.Order()) held.Add(await locks.AcquireAsync(id, token));
            await using var db = await factory.CreateDbContextAsync(token);
            var servers = await db.Servers.AsNoTracking().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).ToArrayAsync(token);
            if (servers.Length != ids.Length) throw PanelProblems.NotFound("Selected instance");
            if (servers.Any(x => x.RecoveryRequired || x.State is not (ServerState.Stopped or ServerState.Running)))
                throw PanelProblems.Conflict("EXPORT_BUSY", "Finish installation, updates, or repairs on the selected instances before exporting.");
            var bytes = servers.Sum(x => checked(ArchiveIO.Measure(paths.Instance(x.Id)).Bytes + ArchiveIO.Measure(paths.ServerModpack(x.Id)).Bytes));
            using var reservation = ArchiveIO.ReserveSpace(paths.Data, checked(bytes * 3 + 64 * 1024 * 1024), options.Value.ReservedDiskBytes);
            Directory.CreateDirectory(stage); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(stage, (UnixFileMode)448); File.SetUnixFileMode(Path.GetDirectoryName(file)!, (UnixFileMode)448); }
            var capturedAt = DateTimeOffset.UtcNow;
            foreach (var server in servers)
            {
                await jobs.ProgressAsync(job, 10, $"Exporting {server.Name}", token);
                var target = Path.Combine(stage, "instances", server.Id.ToString("N"));
                if (server.Kind == ServerKind.Gate) await ArchiveIO.CopyAsync(paths.Instance(server.Id), target, token);
                else
                {
                    var backup = await backups.CreateLockedAsync(server.Id, job, "Instance export", token);
                    await backups.ExtractForRecoveryAsync(Path.Combine(paths.ServerBackups(server.Id), backup.FileName), target, token);
                }
                if (Directory.Exists(paths.ServerModpack(server.Id)))
                    await ArchiveIO.CopyAsync(paths.ServerModpack(server.Id), Path.Combine(stage, "modpacks", server.Id.ToString("N")), token);
                if (server.IconRevision is { } revision && ValidIcon(revision) && File.Exists(Path.Combine(paths.Icons, revision + ".png")))
                {
                    Directory.CreateDirectory(Path.Combine(stage, "icons"));
                    File.Copy(Path.Combine(paths.Icons, revision + ".png"), Path.Combine(stage, "icons", revision + ".png"), true);
                }
            }
            var gates = await db.GateSettings.AsNoTracking().Where(x => ids.Contains(x.ServerId)).ToArrayAsync(token);
            foreach (var gate in gates)
            {
                if (gate.DefaultBackendServerId is { } backend && !ids.Contains(backend)) gate.DefaultBackendServerId = null;
                // Regenerate generated config after import, using only exported backend relationships.
                gate.ConfigurationDirty = true;
            }
            var metadata = new InstanceExportMetadata(servers,
                await db.Schedules.AsNoTracking().Where(x => ids.Contains(x.ServerId)).ToArrayAsync(token),
                await db.Players.AsNoTracking().Where(x => ids.Contains(x.ServerId)).ToArrayAsync(token), gates,
                await db.GateBackends.AsNoTracking().Where(x => ids.Contains(x.GateServerId) && ids.Contains(x.BackendServerId)).ToArrayAsync(token),
                await db.GateExternalBackends.AsNoTracking().Where(x => ids.Contains(x.GateServerId)).ToArrayAsync(token));
            await File.WriteAllTextAsync(Path.Combine(stage, "instances.json"), JsonSerializer.Serialize(metadata), token);
            foreach (var item in held) item.Dispose(); held.Clear();
            await jobs.ProgressAsync(job, 80, "Packaging instance export", token);
            await RecoveryArchive.PackAsync(stage, temporary, "instances", capturedAt, token);
            File.Move(temporary, file);
        }
        finally
        {
            foreach (var item in held) item.Dispose();
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool ValidIcon(string revision) => revision.Length == 64 && revision.All(char.IsAsciiHexDigit);

    // Called by the existing offline import command after checksummed extraction and panel locking.
    internal static async Task ImportExtractedAsync(string stage, PanelPaths paths, CancellationToken token)
    {
        var metadata = JsonSerializer.Deserialize<InstanceExportMetadata>(await File.ReadAllTextAsync(Path.Combine(stage, "instances.json"), token))
            ?? throw new InvalidDataException("Instance metadata is missing.");
        var servers = metadata.Servers;
        if (servers is not { Length: > 0 } || servers.Any(x => x.Id == Guid.Empty) || servers.Select(x => x.Id).Distinct().Count() != servers.Length ||
            metadata.Schedules is null || metadata.Players is null || metadata.GateSettings is null || metadata.GateBackends is null || metadata.GateExternalBackends is null)
            throw new InvalidDataException("Invalid instance metadata.");
        var ids = servers.Select(x => x.Id).ToArray();
        var gateIds = servers.Where(x => x.Kind == ServerKind.Gate).Select(x => x.Id).ToArray();
        if (metadata.Schedules.Any(x => !ids.Contains(x.ServerId)) || metadata.Players.Any(x => !ids.Contains(x.ServerId)) ||
            metadata.GateSettings.Any(x => !gateIds.Contains(x.ServerId) || x.DefaultBackendServerId is { } id && !ids.Contains(id) ||
                x.DefaultExternalBackendId is { } external && !metadata.GateExternalBackends.Any(e => e.Id == external && e.GateServerId == x.ServerId)) ||
            metadata.GateBackends.Any(x => !gateIds.Contains(x.GateServerId) || !ids.Contains(x.BackendServerId)) ||
            metadata.GateExternalBackends.Any(x => !gateIds.Contains(x.GateServerId)))
            throw new InvalidDataException("Instance metadata references instances outside this export.");
        await SchemaMigration.MigrateAsync(paths.StateDatabase, token);
        await using var db = new StateDbContext(new DbContextOptionsBuilder<StateDbContext>().UseSqlite($"Data Source={paths.StateDatabase};Pooling=False").Options);
        var existing = await db.Servers.AsNoTracking().ToListAsync(token);
        if (servers.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != servers.Length || servers.Select(x => x.Port).Distinct().Count() != servers.Length ||
            servers.Any(x => existing.Any(e => e.Id == x.Id || e.Port == x.Port || string.Equals(e.Name, x.Name, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("The export conflicts with an existing instance ID, name, or port. No instances were imported.");
        foreach (var server in servers)
        {
            var source = Path.Combine(stage, "instances", server.Id.ToString("N"));
            if (!Directory.Exists(source)) throw new InvalidDataException("Instance files are missing.");
            if (server.Kind != ServerKind.Gate && !File.Exists(ProcessSupervisor.ResolveLaunchTarget(source, server.LaunchTarget)))
                throw new InvalidDataException("The server launch target is missing.");
            if (Directory.Exists(paths.Instance(server.Id)) || Directory.Exists(paths.ServerModpack(server.Id)))
                throw new IOException("Existing instance data would be replaced. No instances were imported.");
            server.State = ServerState.Stopped; server.ProcessId = null; server.StartedAt = null;
            server.StartOnBoot = false; server.CrashRecovery = false; server.CrashAttempts = 0;
            if (server.IconRevision is { } revision && !ValidIcon(revision)) throw new InvalidDataException("Invalid instance icon revision.");
        }
        var moved = new List<(string Source, string Destination, bool Directory)>(); var committed = false;
        try
        {
            foreach (var server in servers)
            {
                MoveDirectory(Path.Combine(stage, "instances", server.Id.ToString("N")), paths.Instance(server.Id));
                var modpack = Path.Combine(stage, "modpacks", server.Id.ToString("N"));
                if (Directory.Exists(modpack)) MoveDirectory(modpack, paths.ServerModpack(server.Id));
                if (server.IconRevision is { } revision)
                {
                    var source = Path.Combine(stage, "icons", revision + ".png"); var destination = Path.Combine(paths.Icons, revision + ".png");
                    if (File.Exists(source) && !File.Exists(destination)) { File.Move(source, destination); moved.Add((source, destination, false)); }
                    if (!File.Exists(destination)) server.IconRevision = null;
                }
            }
            foreach (var schedule in metadata.Schedules) { schedule.Enabled = false; schedule.IsRunning = false; schedule.NextRunAt = null; }
            foreach (var player in metadata.Players) { player.Id = 0; player.Online = false; }
            foreach (var gate in metadata.GateSettings) gate.ConfigurationDirty = true;
            db.Servers.AddRange(servers); db.Schedules.AddRange(metadata.Schedules); db.Players.AddRange(metadata.Players);
            db.GateSettings.AddRange(metadata.GateSettings); db.GateBackends.AddRange(metadata.GateBackends); db.GateExternalBackends.AddRange(metadata.GateExternalBackends);
            db.AuditEvents.Add(new() { Actor = "local-recovery", Action = "instances-import", Target = string.Join(",", ids), Outcome = "succeeded" });
            await db.SaveChangesAsync(token); committed = true;
        }
        finally
        {
            if (!committed)
                foreach (var move in moved.AsEnumerable().Reverse())
                { if (move.Directory) Directory.Move(move.Destination, move.Source); else File.Move(move.Destination, move.Source); }
        }
        void MoveDirectory(string source, string destination) { Directory.Move(source, destination); moved.Add((source, destination, true)); }
    }
}
