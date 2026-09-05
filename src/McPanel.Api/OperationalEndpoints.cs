using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api;

public static class OperationalEndpoints
{
    public static void MapOperationalEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        app.MapGet("/health/ready", async (PanelPaths paths, PersistentRuntimeClient runtime, CancellationToken token) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                await using var db = new SqliteConnection($"Data Source={paths.StateDatabase};Mode=ReadOnly;Default Timeout=1");
                await db.OpenAsync(deadline.Token);
                await using var command = db.CreateCommand(); command.CommandText = "SELECT Version FROM __McPanelSchema ORDER BY Version DESC LIMIT 1;";
                if (Convert.ToInt32(await command.ExecuteScalarAsync(deadline.Token)) != SchemaMigration.CurrentVersion) throw new InvalidDataException("Database migration is incomplete.");
                var capabilities = runtime.Enabled ? await runtime.CapabilitiesAsync(deadline.Token) : null;
                if (runtime.Enabled && (capabilities?.ConsoleSchema != SchemaMigration.ConsoleVersion || (!capabilities.Features.Contains("save-leases") || !capabilities.Features.Contains("gate-feature-memory"))))
                    throw new InvalidDataException("The active runtime needs a compatible update.");
                return Results.Ok(new { status = "ready", schema = SchemaMigration.CurrentVersion, panelVersion = RecoveryArchive.Version, runtimeVersion = capabilities?.Version });
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
            { return Results.Json(new { status = "unavailable", message = "Database or runtime readiness could not be confirmed." }, statusCode: 503); }
        }).AllowAnonymous();
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapPost("/servers/{id:guid}/export", async (Guid id, ServerExportService exports, CancellationToken token) => Results.Accepted(value: await exports.QueueAsync(id, token)));
        api.MapPost("/exports/instances", async (InstanceExportRequest request, InstanceExportService exports, CancellationToken token) => Results.Accepted(value: await exports.QueueAsync(request, token)));
        api.MapGet("/exports/{id:guid}/download", async (Guid id, ServerExportService exports, InstanceExportService instances, OperationQueue jobs, CancellationToken token) =>
        {
            var job = await jobs.GetAsync(id, token) ?? throw PanelProblems.NotFound("Export");
            var file = job.Type == "InstancesExport" ? instances.FilePath(id) : exports.FilePath(id);
            if (job.Type is not ("ServerExport" or "InstancesExport") || job.State != JobState.Completed || !File.Exists(file)) throw PanelProblems.NotFound("Completed export");
            return Results.File(file, "application/zip", Path.GetFileName(file), enableRangeProcessing: true);
        });
        api.MapGet("/recovery", async (IDbContextFactory<StateDbContext> factory, Microsoft.Extensions.Options.IOptions<PanelOptions> settings, CancellationToken token) =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var points = await db.RecoveryPoints.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(token);
            return Results.Ok(new { configured = !string.IsNullOrWhiteSpace(settings.Value.ReplicationDirectory), intervalMinutes = settings.Value.RecoveryIntervalMinutes, points = points.Select(point => new { point.Id, point.FileName, point.Sha256, point.CreatedAt, point.Size, point.VerifiedAt, point.ReplicatedAt, point.Error, includesInstances = !point.FileName.StartsWith("panel-settings-", StringComparison.Ordinal) }) });
        });
        api.MapPost("/recovery", async (RecoveryBundleService recovery, CancellationToken token) => Results.Accepted(value: await recovery.QueueAsync(token)));
        api.MapGet("/recovery/{id:guid}/download", async (Guid id, RecoveryBundleService recovery, IDbContextFactory<StateDbContext> factory, CancellationToken token) =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var point = await db.RecoveryPoints.FindAsync([id], token) ?? throw PanelProblems.NotFound("Recovery point");
            var file = Path.Combine(recovery.DirectoryPath, point.FileName);
            if (!File.Exists(file)) throw PanelProblems.NotFound("Recovery bundle");
            return Results.File(file, "application/zip", point.FileName, enableRangeProcessing: true);
        });
        api.MapGet("/incidents", async (IDbContextFactory<StateDbContext> factory, CancellationToken token) =>
        { await using var db = await factory.CreateDbContextAsync(token); return await db.Incidents.AsNoTracking().OrderByDescending(x => x.OpenedAt).Take(200).ToListAsync(token); });
        api.MapGet("/audit", async (IDbContextFactory<StateDbContext> factory, CancellationToken token) =>
        { await using var db = await factory.CreateDbContextAsync(token); return await db.AuditEvents.AsNoTracking().OrderByDescending(x => x.Timestamp).Take(200).ToListAsync(token); });
        api.MapPost("/servers/{id:guid}/recover", async (Guid id, IDbContextFactory<StateDbContext> factory, AsyncKeyedLock keyedLock,
            BackupService backups, SoftwareActivationService software, ProcessSupervisor supervisor, PanelPaths paths, ILoggerFactory loggers, CancellationToken token) =>
        {
            using var serverLock = await keyedLock.AcquireAsync(id, token);
            if (supervisor.IsRunning(id)) throw PanelProblems.Conflict("SERVER_NOT_STOPPED", "Stop the workload before attempting recovery.");
            await using var db = await factory.CreateDbContextAsync(token);
            await ServerImportService.RecoverInterruptedActivationsAsync(paths, db, loggers.CreateLogger<ServerImportService>(), token, id);
            await backups.RecoverInterruptedRestoresAsync(db, token, id);
            await software.RecoverInterruptedAsync(db, token, id);
            var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == id, token) ?? throw PanelProblems.NotFound("Server");
            if (server.RecoveryRequired) throw PanelProblems.Conflict("RECOVERY_REQUIRED", server.RecoveryReason ?? "Recovery still requires repair. Artifacts were preserved.");
            return Results.Ok(new { recovered = true });
        });
    }
}
