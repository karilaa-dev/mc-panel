using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api;

public static partial class ApiEndpoints
{
    public static void MapPanelApi(this WebApplication app, string prefix)
    {
        var root = app.MapGroup(prefix);
        MapAuth(root);
        var api = root.MapGroup("").RequireAuthorization();

        api.MapGet("/servers", (ServerQueryService query, CancellationToken token) => query.ListAsync(token));
        api.MapGet("/servers/{id:guid}", (Guid id, ServerQueryService query, CancellationToken token) => query.GetAsync(id, token));
        api.MapPut("/servers/{id:guid}/public-address", async (Guid id, UpdateServerPublicAddressRequest request, GateProxyService gate, ServerQueryService query, CancellationToken token) =>
        {
            await gate.SetAdvertisedAddressAsync(id, request, token);
            return await query.GetAsync(id, token);
        });
        api.MapPost("/servers", async (CreateServerRequest request, ServerInstallerService installer, CancellationToken token) =>
        {
            var (_, job) = await installer.CreateAsync(request, token);
            return Results.Accepted($"/api/v1/jobs/{job.Id}", job);
        });
        api.MapPost("/servers/modpack", async (CreateModpackServerRequest request, ServerInstallerService installer, CancellationToken token) =>
        {
            var (_, job) = await installer.CreateModpackAsync(request, token);
            return Results.Accepted($"/api/v1/jobs/{job.Id}", job);
        });
        api.MapGet("/catalog/gate", async (GateReleaseService releases, CancellationToken token) =>
            (await releases.ListAsync(token)).Select(x => x.Version));
        api.MapPost("/servers/gate", async (CreateGateServerRequest request, ServerInstallerService installer, CancellationToken token) =>
        {
            var (_, job) = await installer.CreateGateAsync(request, token);
            return Results.Accepted($"/api/v1/jobs/{job.Id}", job);
        });
        api.MapPost("/server-jars/imports", PrepareCustomJarUploadAsync);
        api.MapGet("/servers/{id:guid}/software", (Guid id, ServerSoftwareService software, CancellationToken token) =>
            software.GetAsync(id, token));
        api.MapPost("/servers/{id:guid}/software/change", async (
            Guid id, ChangeServerSoftwareRequest request, ServerSoftwareService software, CancellationToken token) =>
        {
            var job = await software.QueueChangeAsync(id, request, token);
            return Results.Accepted($"/api/v1/jobs/{job.Id}", job);
        });
        api.MapDelete("/servers/{id:guid}", DeleteServerAsync);
        api.MapPost("/servers/{id:guid}/actions/{action}", HandleActionAsync);

        api.MapGet("/servers/{id:guid}/configuration", (Guid id, PropertiesService service, CancellationToken token) => service.ReadAsync(id, token));
        api.MapPut("/servers/{id:guid}/configuration", (Guid id, ServerConfigurationDto request, PropertiesService service, CancellationToken token) => service.SaveAsync(id, request, token));
        api.MapGet("/servers/{id:guid}/properties", (Guid id, PropertiesService service, CancellationToken token) => service.ReadPropertiesAsync(id, token));
        api.MapPut("/servers/{id:guid}/properties", (Guid id, SaveServerPropertiesRequest request, PropertiesService service, CancellationToken token) => service.SavePropertiesAsync(id, request, token));
        api.MapGet("/servers/{id:guid}/runtime", (Guid id, PropertiesService service, CancellationToken token) => service.ReadRuntimeAsync(id, token));
        api.MapPut("/servers/{id:guid}/runtime", (Guid id, RuntimeConfigurationDto request, PropertiesService service, CancellationToken token) => service.SaveRuntimeAsync(id, request, token));
        api.MapGet("/servers/{id:guid}/icon", async (Guid id, ServerIconService icons, CancellationToken token) =>
            Results.File(await icons.GetPathAsync(id, token), "image/png"));
        api.MapPut("/servers/{id:guid}/icon", UploadIconAsync);
        api.MapPut("/servers/{id:guid}/icon/library", (Guid id, SelectServerIconRequest request, ServerIconService icons, CancellationToken token) =>
            icons.SelectAsync(id, request.Revision, token));
        api.MapDelete("/servers/{id:guid}/icon", async (Guid id, ServerIconService icons, CancellationToken token) =>
        { await icons.DeleteAsync(id, token); return Results.NoContent(); });
        api.MapGet("/icons", (ServerIconService icons, CancellationToken token) => icons.ListAsync(token));
        api.MapPost("/icons", UploadLibraryIconAsync);
        api.MapGet("/icons/{revision}", async (string revision, ServerIconService icons, CancellationToken token) =>
            Results.File(await icons.GetLibraryPathAsync(revision, token), "image/png"));
        api.MapDelete("/icons/{revision}", async (string revision, ServerIconService icons, CancellationToken token) =>
        { await icons.DeleteLibraryAsync(revision, token); return Results.NoContent(); });

        api.MapGet("/servers/{id:guid}/console", async (Guid id, long? after, int? limit, IDbContextFactory<StateDbContext> factory, ConsoleService service, CancellationToken token) =>
        {
            await EnsureServerAsync(factory, id, token); return await service.ReadAsync(id, after.GetValueOrDefault(), limit ?? 1_000, token);
        });
        api.MapPost("/servers/{id:guid}/console", async (Guid id, CommandRequest request, ProcessSupervisor supervisor, HttpContext context, CancellationToken token) =>
        {
            context.Items["audit-action"] = "console-command";
            context.Items["audit-target"] = $"{id}: {request.Command}";
            await supervisor.CommandAsync(id, request.Command, token); return Results.NoContent();
        });

        api.MapGet("/servers/{id:guid}/players", (Guid id, PlayerService service, CancellationToken token) => service.ListAsync(id, token));
        api.MapPost("/servers/{id:guid}/players/{name}/{action}", (Guid id, string name, string action, PlayerService service, CancellationToken token) => service.ActionAsync(id, name, action, token));
        api.MapGet("/servers/{id:guid}/players/{uuid}/inventory", (Guid id, string uuid, PlayerInventoryService service, CancellationToken token) => service.GetAsync(id, uuid, token));
        api.MapGet("/servers/{id:guid}/players/{uuid}/inventory/backups", (Guid id, string uuid, PlayerInventoryService service, CancellationToken token) => service.ListBackupsAsync(id, uuid, token));
        api.MapPost("/servers/{id:guid}/players/{uuid}/inventory/backups", (Guid id, string uuid, CreatePlayerInventoryBackupRequest request, PlayerInventoryService service, CancellationToken token) => service.CreateBackupAsync(id, uuid, request, token));
        api.MapGet("/servers/{id:guid}/players/{uuid}/inventory/backups/{backupId:guid}", (Guid id, string uuid, Guid backupId, PlayerInventoryService service, CancellationToken token) => service.PreviewBackupAsync(id, uuid, backupId, token));
        api.MapPost("/servers/{id:guid}/players/{uuid}/inventory/backups/{backupId:guid}/restore", (Guid id, string uuid, Guid backupId, RestorePlayerInventoryRequest request, PlayerInventoryService service, CancellationToken token) => service.RestoreAsync(id, uuid, backupId, request, token));
        api.MapGet("/servers/{id:guid}/mods", (Guid id, ModMetadataService service, CancellationToken token) => service.ListAsync(id, token));
        api.MapPost("/servers/{id:guid}/mods/modrinth", async (
            Guid id, InstallModrinthModRequest request, ModrinthModInstallerService service,
            CancellationToken token) =>
        {
            var job = await service.QueueAsync(id, request, token);
            return Results.Accepted($"/api/v1/jobs/{job.Id}", job);
        });
        api.MapGet("/servers/{id:guid}/plugins", (Guid id, ModMetadataService service, CancellationToken token) =>
            service.ListPluginsAsync(id, token));
        api.MapPost("/servers/{id:guid}/plugins/modrinth", async (
            Guid id, InstallModrinthPluginRequest request, ModrinthModInstallerService service,
            CancellationToken token) =>
        {
            var job = await service.QueuePluginAsync(id, request, token);
            return Results.Accepted($"/api/v1/jobs/{job.Id}", job);
        });
        api.MapGet("/servers/{id:guid}/modpack/changes", (
            Guid id, ModpackService service, CancellationToken token) => service.ChangesAsync(id, token));

        api.MapGet("/servers/{id:guid}/files", (Guid id, string? path, FileManagerService files) => files.List(id, path ?? ""));
        api.MapPost("/servers/{id:guid}/files", async (Guid id, CreateFileRequest request, FileManagerService files, CancellationToken token) => { await files.CreateAsync(id, request.Path, request.Directory, token); return Results.NoContent(); });
        api.MapDelete("/servers/{id:guid}/files", async (Guid id, string path, FileManagerService files, CancellationToken token) => { await files.DeleteAsync(id, path, token); return Results.NoContent(); });
        api.MapGet("/servers/{id:guid}/files/content", (Guid id, string path, FileManagerService files, CancellationToken token) => files.ReadSnapshotAsync(id, path, token));
        api.MapPut("/servers/{id:guid}/files/content", async (Guid id, string path, SaveFileRequest request, FileManagerService files, CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(request.Revision)) throw new PanelException(428, "FILE_REVISION_REQUIRED", "Read the current file revision before saving.");
            await files.WriteTextAsync(id, path, request.Content, token, request.Revision); return Results.NoContent();
        });
        api.MapGet("/servers/{id:guid}/files/download", (Guid id, string path, FileManagerService files) => { var item = files.Download(id, path); return Results.File(item.Path, "application/octet-stream", item.Name, enableRangeProcessing: true); });
        api.MapPost("/servers/{id:guid}/files/upload", UploadAsync).DisableAntiforgery();
        api.MapPost("/servers/{id:guid}/files/move", async (Guid id, MoveFileRequest request, FileManagerService files, CancellationToken token) => { await files.MoveAsync(id, request.Source, request.Destination, token); return Results.NoContent(); });
        api.MapPost("/servers/{id:guid}/files/extract", async (Guid id, ExtractFileRequest request, FileManagerService files, CancellationToken token) => { await files.ExtractAsync(id, request.Path, request.Destination, token); return Results.NoContent(); });

        api.MapGet("/servers/{id:guid}/backups", (Guid id, BackupService backups, CancellationToken token) => backups.ListAsync(id, token));
        api.MapPost("/servers/{id:guid}/backups", async (Guid id, BackupService backups, CancellationToken token) => { var job = await backups.QueueCreateAsync(id, "Manual", token); return Results.Accepted($"/api/v1/jobs/{job.Id}", job); });
        api.MapGet("/servers/{id:guid}/backups/{backupId:guid}", async (Guid id, Guid backupId, BackupService backups, CancellationToken token) => { var item = await backups.DownloadAsync(id, backupId, token); return Results.File(item.Path, "application/zip", item.Name, enableRangeProcessing: true); });
        api.MapDelete("/servers/{id:guid}/backups/{backupId:guid}", async (Guid id, Guid backupId, BackupService backups, CancellationToken token) => { await backups.DeleteAsync(id, backupId, token); return Results.NoContent(); });
        api.MapPost("/servers/{id:guid}/backups/{backupId:guid}/restore", async (Guid id, Guid backupId, BackupService backups, CancellationToken token) => { var job = await backups.QueueRestoreAsync(id, backupId, token); return Results.Accepted($"/api/v1/jobs/{job.Id}", job); });

        api.MapGet("/servers/{id:guid}/schedules", (Guid id, SchedulerService scheduler, CancellationToken token) => scheduler.ListAsync(id, token));
        api.MapGet("/servers/{id:guid}/schedules/{scheduleId:guid}/runs", (Guid id, Guid scheduleId, SchedulerService scheduler, CancellationToken token) => scheduler.HistoryAsync(id, scheduleId, token));
        api.MapPost("/servers/{id:guid}/schedules/{scheduleId:guid}/run", async (Guid id, Guid scheduleId, SchedulerService scheduler, CancellationToken token) => { await scheduler.RunNowAsync(id, scheduleId, token); return Results.Accepted(); });
        api.MapPost("/servers/{id:guid}/schedules", (Guid id, SaveScheduleRequest request, SchedulerService scheduler, CancellationToken token) => scheduler.CreateAsync(id, request, token));
        api.MapPut("/servers/{id:guid}/schedules/{scheduleId:guid}", (Guid id, Guid scheduleId, SaveScheduleRequest request, SchedulerService scheduler, CancellationToken token) => scheduler.UpdateAsync(id, scheduleId, request, token));
        api.MapPatch("/servers/{id:guid}/schedules/{scheduleId:guid}", async (Guid id, Guid scheduleId, ToggleScheduleRequest request, SchedulerService scheduler, CancellationToken token) => { await scheduler.ToggleAsync(id, scheduleId, request.Enabled, token); return Results.NoContent(); });
        api.MapDelete("/servers/{id:guid}/schedules/{scheduleId:guid}", async (Guid id, Guid scheduleId, SchedulerService scheduler, CancellationToken token) => { await scheduler.DeleteAsync(id, scheduleId, token); return Results.NoContent(); });

        api.MapGet("/jobs/{id:guid}", async (Guid id, OperationQueue operations, CancellationToken token) => await operations.GetAsync(id, token) is { } job ? Results.Ok(job) : throw PanelProblems.NotFound("Job"));
        api.MapGet("/jobs", (Guid? serverId, int? limit, OperationQueue operations, CancellationToken token) => operations.ListAsync(serverId, limit ?? 100, token));
        api.MapPost("/jobs/{id:guid}/cancel", (Guid id, OperationQueue operations, CancellationToken token) => operations.CancelAsync(id, token));
        api.MapPost("/jobs/{id:guid}/retry", (Guid id, JobRecoveryService recovery, CancellationToken token) => recovery.RetryAsync(id, token));
        api.MapPut("/servers/{id:guid}/backups/{backupId:guid}/pin", async (Guid id, Guid backupId, PinBackupRequest request, BackupService backups, CancellationToken token) =>
        { await backups.SetPinnedAsync(id, backupId, request.Pinned, token); return Results.NoContent(); });
        api.MapGet("/java", (JavaDiscoveryService java, CancellationToken token) => java.GetAsync(token));
        api.MapPost("/java/rescan", (JavaDiscoveryService java, CancellationToken token) => java.ScanAsync(token));
        api.MapPost("/java/custom", (AddJavaRequest request, JavaDiscoveryService java, CancellationToken token) =>
            string.IsNullOrWhiteSpace(request?.Path) ? throw PanelProblems.Validation("A Java executable path is required.") : java.AddCustomAsync(request.Path, token));
        api.MapGet("/catalog", (bool? experimental, DistributionCatalogService catalog, CancellationToken token) => catalog.GetCatalogAsync(experimental ?? false, token));
        api.MapGet("/modrinth/search", (
            string projectType, string? query, int? offset, int? limit, Guid? serverId,
            string? gameVersion, string? loader,
            ModrinthService service, CancellationToken token) =>
            service.SearchAsync(projectType, query, offset ?? 0, limit, serverId, gameVersion, loader, token));
        api.MapGet("/modrinth/projects/{projectId}/versions", (
            string projectId, Guid? serverId, string? projectType, string? gameVersion,
            string? loader, ModrinthService service, CancellationToken token) =>
            service.VersionsAsync(projectId, serverId, projectType, gameVersion, loader, token));
        api.MapPost("/modrinth/modpacks/imports/modrinth", (
            PrepareModrinthPackRequest request, ModpackService service, CancellationToken token) =>
            service.PrepareRemoteAsync(request, token));
        api.MapPost("/modrinth/modpacks/imports/upload", PrepareModpackUploadAsync);
        api.MapGet("/catalog/paper/{version}/builds", (string version, bool? experimental, DistributionCatalogService catalog, CancellationToken token) => catalog.PaperBuildsAsync(version, experimental ?? false, token));
        api.MapGet("/system/status", (HostMetricsService metrics) => metrics.GetStatus());
        api.MapGet("/servers/{id:guid}/gate", (Guid id, GateProxyService gate, CancellationToken token) => gate.GetAsync(id, token));
        api.MapPut("/servers/{id:guid}/gate/config", (Guid id, UpdateGateConfigurationRequest request, GateProxyService gate, CancellationToken token) => gate.UpdateAsync(id, request, token));
        api.MapPost("/servers/{id:guid}/gate/prepare-backends", async (Guid id, PrepareGateBackendsRequest request, GateBackendConfigurationService backends, CancellationToken token) =>
        {
            await backends.PrepareAsync(id, request.ExpectedRevision, token);
            return Results.NoContent();
        });
        api.MapPost("/servers/{id:guid}/gate/update", async (Guid id, GateActionRequest request, GateProxyService gate, CancellationToken token) =>
        {
            var job = await gate.QueueUpdateAsync(id, request.ConfirmDisconnectPlayers, token, request.Version);
            return Results.Accepted($"/api/v1/jobs/{job.Id}", job);
        });
        api.MapPost("/servers/{id:guid}/gate/secrets/{kind}/reveal", async (Guid id, string kind, HttpResponse response, GateProxyService gate, CancellationToken token) =>
        {
            response.Headers.CacheControl = "no-store";
            return await gate.RevealSecretAsync(id, kind, token);
        });
        api.MapPost("/servers/{id:guid}/gate/secrets/{kind}/rotate", async (Guid id, string kind, HttpResponse response, GateProxyService gate, CancellationToken token) =>
        {
            response.Headers.CacheControl = "no-store";
            return await gate.RotateSecretAsync(id, kind, token);
        });
        api.MapPost("/servers/{id:guid}/gate/secrets/{kind}/generate", async (Guid id, string kind, GenerateGateSecretRequest request, HttpResponse response, GateProxyService gate, CancellationToken token) =>
        {
            response.Headers.CacheControl = "no-store";
            return await gate.GenerateSecretAsync(id, kind, request.ConfirmReplace, token);
        });
        api.MapGet("/system/info", (PanelPaths paths, IOptions<PanelOptions> options) =>
        {
            var total = HostMetricsService.ReadMemory().Total;
            return new SystemInfoDto(typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0", paths.Data, paths.Instances, (long)(total * options.Value.MemoryAllocationFraction));
        });
        api.MapGet("/system/settings", async (IDbContextFactory<StateDbContext> factory, CancellationToken token) =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var settings = await db.PanelSettings.AsNoTracking().SingleAsync(x => x.Id == 1, token);
            return new PanelSettingsDto(settings.KeepServersRunningOnPanelStop, settings.GlobalServerHost, settings.Revision);
        });
        api.MapPut("/system/settings", async (PanelSettingsDto request, IDbContextFactory<StateDbContext> factory, GateProxyService gate, CancellationToken token) =>
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var settings = await db.PanelSettings.SingleAsync(x => x.Id == 1, token);
            if (!string.Equals(settings.Revision, request.Revision, StringComparison.Ordinal))
                throw new PanelException(409, "PANEL_SETTINGS_CHANGED", "Panel settings changed after they were loaded. Refresh and try again.");
            settings.GlobalServerHost = GateConfigurationService.NormalizeHost(request.GlobalServerHost);
            settings.KeepServersRunningOnPanelStop = request.KeepServersRunningOnPanelStop;
            settings.Revision = Guid.NewGuid().ToString("N");
            settings.UpdatedAt = DateTimeOffset.UtcNow;
            await gate.MarkGlobalAddressChangedAsync(db, token);
            await db.SaveChangesAsync(token);
            return new PanelSettingsDto(settings.KeepServersRunningOnPanelStop, settings.GlobalServerHost, settings.Revision);
        });
    }

    private static void MapAuth(RouteGroupBuilder root)
    {
        var auth = root.MapGroup("/auth");
        auth.MapGet("/status", (HttpContext context, AdminAuthService service, CancellationToken token) => service.StatusAsync(context.User, token)).AllowAnonymous();
        auth.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context); return Results.Ok(new { token = tokens.RequestToken });
        }).AllowAnonymous();
        auth.MapPost("/setup", (HttpContext context, SetupRequest request, AdminAuthService service, CancellationToken token) => service.SetupAsync(context, request, token)).AllowAnonymous();
        auth.MapPost("/login", (HttpContext context, LoginRequest request, AdminAuthService service, CancellationToken token) => service.LoginAsync(context, request, token)).AllowAnonymous().RequireRateLimiting("login");
        auth.MapPost("/logout", async (HttpContext context, AdminAuthService service) => { await service.LogoutAsync(context); return Results.NoContent(); }).RequireAuthorization();
        auth.MapPut("/password", async (HttpContext context, ChangePasswordRequest request, AdminAuthService service, CancellationToken token) => { await service.ChangePasswordAsync(context, request, token); return Results.NoContent(); }).RequireAuthorization();
    }

    private static async Task<IResult> HandleActionAsync(Guid id, string action, HttpRequest request, ProcessSupervisor supervisor, ServerInstallerService installer, CancellationToken token)
    {
        var confirm = false;
        if (action.Equals("kill", StringComparison.OrdinalIgnoreCase))
        {
            var body = await request.ReadFromJsonAsync<ConfirmKillRequest>(cancellationToken: token);
            confirm = body?.Confirm == true;
        }
        return await QueueAction(id, action, confirm, supervisor, installer, token);
    }

    private static async Task<IResult> QueueAction(Guid id, string action, bool confirm, ProcessSupervisor supervisor, ServerInstallerService installer, CancellationToken token)
    {
        var normalized = action.ToLowerInvariant();
        JobDto job;
        if (normalized == "update") job = await installer.QueueUpdateAsync(id, token);
        else if (normalized is "start" or "stop" or "restart" or "kill") job = await supervisor.QueueActionAsync(id, normalized, confirm, token);
        else throw PanelProblems.Validation("Unknown server action.");
        return Results.Accepted($"/api/v1/jobs/{job.Id}", job);
    }

    private static async Task<IResult> DeleteServerAsync(Guid id, IDbContextFactory<StateDbContext> stateFactory, IDbContextFactory<ConsoleDbContext> consoleFactory, PanelPaths paths, AsyncKeyedLock keyedLock, ProcessSupervisor supervisor, ModpackService modpacks, GateProxyService gate, ILoggerFactory loggerFactory, CancellationToken token)
    {
        using var serverLock = await keyedLock.AcquireAsync(id, token);
        await using var db = await stateFactory.CreateDbContextAsync(token);
        var server = await db.Servers.FindAsync([id], token) ?? throw PanelProblems.NotFound("Server");
        if (supervisor.IsRunning(id) || server.State is not (ServerState.Stopped or ServerState.Error or ServerState.Crashed))
            throw PanelProblems.Conflict("SERVER_NOT_STOPPED", "Stop the server before deleting it.");
        await gate.EnsureCanDeleteAsync(id, token);

        var stage = Path.Combine(paths.Staging, $"server-delete-{id:N}-{Guid.NewGuid():N}");
        var instance = paths.Instance(id);
        var backup = paths.ServerBackups(id);
        var stagedInstance = Path.Combine(stage, "instance");
        var stagedBackup = Path.Combine(stage, "backup");
        ValidateDeletionPath(instance, paths.Instances);
        ValidateDeletionPath(backup, paths.Backups);
        Directory.CreateDirectory(stage);
        var committed = false;
        try
        {
            if (Directory.Exists(instance)) Directory.Move(instance, stagedInstance);
            if (Directory.Exists(backup)) Directory.Move(backup, stagedBackup);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            try
            {
                await db.Schedules.Where(x => x.ServerId == id).ExecuteDeleteAsync(token);
                await db.Players.Where(x => x.ServerId == id).ExecuteDeleteAsync(token);
                await db.Backups.Where(x => x.ServerId == id).ExecuteDeleteAsync(token);
                await gate.RemoveMembershipsForDeleteAsync(db, id, token);
                db.Servers.Remove(server);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                committed = true;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                if (Directory.Exists(stagedInstance) && !Directory.Exists(instance)) Directory.Move(stagedInstance, instance);
                if (Directory.Exists(stagedBackup) && !Directory.Exists(backup)) Directory.Move(stagedBackup, backup);
                throw;
            }
        }
        catch
        {
            if (!committed)
            {
                try { if (Directory.Exists(stagedInstance) && !Directory.Exists(instance)) Directory.Move(stagedInstance, instance); } catch { }
                try { if (Directory.Exists(stagedBackup) && !Directory.Exists(backup)) Directory.Move(stagedBackup, backup); } catch { }
            }
            if (Directory.Exists(stage) && !Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage);
            throw;
        }

        var logger = loggerFactory.CreateLogger("ServerDeletion");
        try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not purge staged files for deleted server {ServerId}", id); }
        try { modpacks.Delete(id); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not remove modpack state for deleted server {ServerId}", id); }
        try
        {
            await using var consoleDb = await consoleFactory.CreateDbContextAsync(CancellationToken.None);
            await consoleDb.Lines.Where(x => x.ServerId == id).ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Could not remove console rows for deleted server {ServerId}", id); }
        return Results.NoContent();
    }

    private static void ValidateDeletionPath(string target, string expectedParent)
    {
        var parent = Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(target);
        if (!resolved.StartsWith(parent, StringComparison.Ordinal) || resolved == parent.TrimEnd(Path.DirectorySeparatorChar))
            throw new PanelException(500, "DELETE_PATH_INVALID", "The server deletion target is outside its managed directory.");
    }

    private static async Task<ModpackInspectionDto> PrepareModpackUploadAsync(
        HttpRequest request, ModpackService service, CancellationToken token)
    {
        if (!request.HasFormContentType) throw PanelProblems.Validation("Modpack upload must use multipart/form-data.");
        IFormCollection form;
        try { form = await request.ReadFormAsync(token); }
        catch (InvalidDataException exception) when (exception.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        { throw new PanelException(413, "FILE_TOO_LARGE", "The uploaded modpack exceeds the configured limit."); }
        catch (InvalidDataException) { throw PanelProblems.Validation("The multipart form data is invalid."); }
        var file = form.Files.GetFile("file") ?? throw PanelProblems.Validation("Multipart field 'file' is required.");
        return await service.PrepareUploadAsync(file, token);
    }

    private static async Task<CustomJarImportDto> PrepareCustomJarUploadAsync(
        HttpRequest request, CustomJarService service, CancellationToken token)
    {
        if (!request.HasFormContentType) throw PanelProblems.Validation("Custom JAR upload must use multipart/form-data.");
        IFormCollection form;
        try { form = await request.ReadFormAsync(token); }
        catch (InvalidDataException exception) when (exception.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        { throw new PanelException(413, "UPLOAD_TOO_LARGE", "The uploaded JAR exceeds the configured limit."); }
        catch (InvalidDataException) { throw PanelProblems.Validation("The multipart form data is invalid."); }
        if (form.Files.Count != 1) throw PanelProblems.Validation("Upload exactly one JAR file.");
        var file = form.Files.GetFile("file") ?? throw PanelProblems.Validation("Multipart field 'file' is required.");
        return await service.PrepareAsync(file, token);
    }

    private static async Task<IResult> UploadAsync(Guid id, string? path, HttpRequest request, FileManagerService files, CancellationToken token)
    {
        if (!request.HasFormContentType) throw PanelProblems.Validation("Upload must use multipart/form-data.");
        IFormCollection form;
        try { form = await request.ReadFormAsync(token); }
        catch (InvalidDataException exception) when (exception.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        {
            throw new PanelException(413, "FILE_TOO_LARGE", "The uploaded file exceeds the configured limit.");
        }
        catch (InvalidDataException)
        {
            throw PanelProblems.Validation("The multipart form data is invalid.");
        }
        var file = form.Files.GetFile("file") ?? throw PanelProblems.Validation("Multipart field 'file' is required.");
        await files.UploadAsync(id, path ?? "", file, token, request.Query["overwrite"] == "true"); return Results.NoContent();
    }

    private static async Task<IResult> UploadIconAsync(Guid id, HttpRequest request, ServerIconService icons, CancellationToken token)
    {
        return Results.Ok(await icons.SaveAsync(id, await ReadIconUploadAsync(request, token), token));
    }

    private static async Task<IResult> UploadLibraryIconAsync(HttpRequest request, ServerIconService icons, CancellationToken token)
    {
        return Results.Ok(await icons.SaveLibraryAsync(await ReadIconUploadAsync(request, token), token));
    }

    private static async Task<IFormFile> ReadIconUploadAsync(HttpRequest request, CancellationToken token)
    {
        if (!request.HasFormContentType) throw PanelProblems.Validation("Icon upload must use multipart/form-data.");
        IFormCollection form;
        try { form = await request.ReadFormAsync(token); }
        catch (InvalidDataException exception) when (exception.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        { throw new PanelException(413, "ICON_TOO_LARGE", "The final server icon cannot exceed 256 KiB."); }
        catch (InvalidDataException) { throw PanelProblems.Validation("The multipart form data is invalid."); }
        return form.Files.GetFile("file") ?? throw PanelProblems.Validation("Multipart field 'file' is required.");
    }

    private static async Task EnsureServerAsync(IDbContextFactory<StateDbContext> factory, Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Servers.AnyAsync(x => x.Id == id, token)) throw PanelProblems.NotFound("Server");
    }

}
