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
        api.MapPost("/servers", async (CreateServerRequest request, ServerInstallerService installer, CancellationToken token) =>
        {
            var (_, job) = await installer.CreateAsync(request, token);
            return Results.Accepted($"/api/v1/jobs/{job.Id}", job);
        });
        api.MapDelete("/servers/{id:guid}", DeleteServerAsync);
        api.MapPost("/servers/{id:guid}/actions/{action}", HandleActionAsync);
        foreach (var action in new[] { "start", "stop", "restart", "update" })
            api.MapPost($"/servers/{{id:guid}}/{action}", (Guid id, ProcessSupervisor supervisor, ServerInstallerService installer, CancellationToken token) => QueueAction(id, action, false, supervisor, installer, token));

        api.MapGet("/servers/{id:guid}/configuration", (Guid id, PropertiesService service, CancellationToken token) => service.ReadAsync(id, token));
        api.MapPut("/servers/{id:guid}/configuration", (Guid id, ServerConfigurationDto request, PropertiesService service, CancellationToken token) => service.SaveAsync(id, request, token));
        api.MapGet("/servers/{id:guid}/properties", (Guid id, PropertiesService service, CancellationToken token) => service.ReadPropertiesAsync(id, token));
        api.MapPut("/servers/{id:guid}/properties", (Guid id, SaveServerPropertiesRequest request, PropertiesService service, CancellationToken token) => service.SavePropertiesAsync(id, request, token));
        api.MapGet("/servers/{id:guid}/runtime", (Guid id, PropertiesService service, CancellationToken token) => service.ReadRuntimeAsync(id, token));
        api.MapPut("/servers/{id:guid}/runtime", (Guid id, RuntimeConfigurationDto request, PropertiesService service, CancellationToken token) => service.SaveRuntimeAsync(id, request, token));

        api.MapGet("/servers/{id:guid}/console", async (Guid id, long? after, int? limit, IDbContextFactory<StateDbContext> factory, ConsoleService service, CancellationToken token) =>
        {
            await EnsureServerAsync(factory, id, token); return await service.ReadAsync(id, after.GetValueOrDefault(), limit ?? 1_000, token);
        });
        api.MapPost("/servers/{id:guid}/console", async (Guid id, CommandRequest request, ProcessSupervisor supervisor, CancellationToken token) =>
        { await supervisor.CommandAsync(id, request.Command, token); return Results.NoContent(); });

        api.MapGet("/servers/{id:guid}/players", (Guid id, PlayerService service, CancellationToken token) => service.ListAsync(id, token));
        api.MapPost("/servers/{id:guid}/players/{name}/{action}", (Guid id, string name, string action, PlayerService service, CancellationToken token) => service.ActionAsync(id, name, action, token));

        api.MapGet("/servers/{id:guid}/files", (Guid id, string? path, FileManagerService files) => files.List(id, path ?? ""));
        api.MapPost("/servers/{id:guid}/files", async (Guid id, CreateFileRequest request, FileManagerService files, CancellationToken token) => { await files.CreateAsync(id, request.Path, request.Directory, token); return Results.NoContent(); });
        api.MapDelete("/servers/{id:guid}/files", async (Guid id, string path, FileManagerService files, CancellationToken token) => { await files.DeleteAsync(id, path, token); return Results.NoContent(); });
        api.MapGet("/servers/{id:guid}/files/content", async (Guid id, string path, FileManagerService files, CancellationToken token) => new FileContentDto(await files.ReadTextAsync(id, path, token)));
        api.MapPut("/servers/{id:guid}/files/content", async (Guid id, string path, SaveFileRequest request, FileManagerService files, CancellationToken token) => { await files.WriteTextAsync(id, path, request.Content, token); return Results.NoContent(); });
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
        api.MapPost("/servers/{id:guid}/schedules", (Guid id, SaveScheduleRequest request, SchedulerService scheduler, CancellationToken token) => scheduler.CreateAsync(id, request, token));
        api.MapPut("/servers/{id:guid}/schedules/{scheduleId:guid}", (Guid id, Guid scheduleId, SaveScheduleRequest request, SchedulerService scheduler, CancellationToken token) => scheduler.UpdateAsync(id, scheduleId, request, token));
        api.MapPatch("/servers/{id:guid}/schedules/{scheduleId:guid}", async (Guid id, Guid scheduleId, ToggleScheduleRequest request, SchedulerService scheduler, CancellationToken token) => { await scheduler.ToggleAsync(id, scheduleId, request.Enabled, token); return Results.NoContent(); });
        api.MapDelete("/servers/{id:guid}/schedules/{scheduleId:guid}", async (Guid id, Guid scheduleId, SchedulerService scheduler, CancellationToken token) => { await scheduler.DeleteAsync(id, scheduleId, token); return Results.NoContent(); });

        api.MapGet("/jobs/{id:guid}", async (Guid id, OperationQueue operations, CancellationToken token) => await operations.GetAsync(id, token) is { } job ? Results.Ok(job) : throw PanelProblems.NotFound("Job"));
        api.MapGet("/java", (JavaDiscoveryService java, CancellationToken token) => java.GetAsync(token));
        api.MapPost("/java/rescan", (JavaDiscoveryService java, CancellationToken token) => java.ScanAsync(token));
        api.MapPost("/java/custom", (AddJavaRequest request, JavaDiscoveryService java, CancellationToken token) =>
            string.IsNullOrWhiteSpace(request?.Path) ? throw PanelProblems.Validation("A Java executable path is required.") : java.AddCustomAsync(request.Path, token));
        api.MapGet("/catalog", (bool? experimental, DistributionCatalogService catalog, CancellationToken token) => catalog.GetCatalogAsync(experimental ?? false, token));
        api.MapGet("/catalog/paper/{version}/builds", (string version, bool? experimental, DistributionCatalogService catalog, CancellationToken token) => catalog.PaperBuildsAsync(version, experimental ?? false, token));
        api.MapGet("/system/status", (HostMetricsService metrics) => metrics.GetStatus());
        api.MapGet("/system/info", (PanelPaths paths, IOptions<PanelOptions> options) =>
        {
            var total = HostMetricsService.ReadMemory().Total;
            return new SystemInfoDto(typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0", paths.Data, paths.Instances, (long)(total * options.Value.MemoryAllocationFraction));
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

    private static async Task<IResult> DeleteServerAsync(Guid id, IDbContextFactory<StateDbContext> stateFactory, IDbContextFactory<ConsoleDbContext> consoleFactory, PanelPaths paths, AsyncKeyedLock keyedLock, ProcessSupervisor supervisor, CancellationToken token)
    {
        using var serverLock = await keyedLock.AcquireAsync(id, token);
        await using var db = await stateFactory.CreateDbContextAsync(token);
        var server = await db.Servers.FindAsync([id], token) ?? throw PanelProblems.NotFound("Server");
        if (supervisor.IsRunning(id) || server.State is not (ServerState.Stopped or ServerState.Error or ServerState.Crashed))
            throw PanelProblems.Conflict("SERVER_NOT_STOPPED", "Stop the server before deleting it.");
        var directory = paths.Instance(id);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        await db.Schedules.Where(x => x.ServerId == id).ExecuteDeleteAsync(token);
        await db.Players.Where(x => x.ServerId == id).ExecuteDeleteAsync(token);
        await db.Backups.Where(x => x.ServerId == id).ExecuteDeleteAsync(token);
        db.Servers.Remove(server); await db.SaveChangesAsync(token);
        var backupDirectory = paths.ServerBackups(id);
        if (Directory.Exists(backupDirectory)) Directory.Delete(backupDirectory, true);
        await using var consoleDb = await consoleFactory.CreateDbContextAsync(token);
        await consoleDb.Lines.Where(x => x.ServerId == id).ExecuteDeleteAsync(token);
        return Results.NoContent();
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
        await files.UploadAsync(id, path ?? "", file, token); return Results.NoContent();
    }

    private static async Task EnsureServerAsync(IDbContextFactory<StateDbContext> factory, Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await db.Servers.AnyAsync(x => x.Id == id, token)) throw PanelProblems.NotFound("Server");
    }

}
