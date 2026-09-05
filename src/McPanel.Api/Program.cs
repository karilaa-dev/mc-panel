using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using McPanel.Api;
using McPanel.Api.Configuration;
using McPanel.Api.Data;
using McPanel.Api.Hubs;
using McPanel.Api.Infrastructure;
using McPanel.Api.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

if (ProductionCommand.IsInvocation(args)) return await ProductionCommand.RunAsync(args);
if (CgroupProcessLauncher.TryExec(args, out var launcherExitCode)) return launcherExitCode;
if (PersistentRuntimeHost.IsInvocation(args)) return await PersistentRuntimeHost.RunAsync(args);
if (PersistentRuntimeUpgradeCommand.IsInvocation(args)) return await PersistentRuntimeUpgradeCommand.RunAsync();
if (ServerImportCommand.IsStageInvocation(args)) return await ServerImportCommand.RunStageAsync(args);
if (ServerImportCommand.IsImportInvocation(args)) return await ServerImportCommand.RunImportAsync(args);

// systemd intentionally uses the writable data directory as its working directory.
// Anchor configuration and bundled web assets to the executable instead of the CWD.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
var panelOptions = new PanelOptions();
builder.Configuration.GetSection("Panel").Bind(panelOptions);
if (builder.Environment.IsDevelopment() && Environment.GetEnvironmentVariable("MCPANEL_DATA_DIR") is null)
{
    panelOptions.DataDirectory = Path.Combine(builder.Environment.ContentRootPath, ".data");
    if (Environment.GetEnvironmentVariable("MCPANEL_CONFIG_DIR") is null) panelOptions.ConfigDirectory = Path.Combine(panelOptions.DataDirectory, "config");
}
const long multipartOverheadAllowance = 1024 * 1024;
var uploadRequestLimit = Math.Clamp(panelOptions.MaxUploadBytes, 0, long.MaxValue - multipartOverheadAllowance) + multipartOverheadAllowance;
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = uploadRequestLimit);
builder.Services.AddSingleton(Options.Create(panelOptions));
var paths = new PanelPaths(panelOptions); paths.EnsureCreated();
builder.Services.AddSingleton(paths);

builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(paths.Keys)).SetApplicationName("McPanel");
builder.Services.AddPooledDbContextFactory<StateDbContext>(options => options.UseSqlite($"Data Source={paths.StateDatabase};Cache=Shared"));
builder.Services.AddPooledDbContextFactory<ConsoleDbContext>(options => options.UseSqlite($"Data Source={paths.ConsoleDatabase};Cache=Shared"));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "mcpanel.auth"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = panelOptions.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest; options.SlidingExpiration = true; options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = async context =>
    {
        var auth = context.HttpContext.RequestServices.GetRequiredService<AdminAuthService>();
        if (await auth.ValidateSessionAsync(context.Principal, context.HttpContext.RequestAborted)) return;
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    };
});
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(forwarding =>
{
    forwarding.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    forwarding.ForwardLimit = 1;
    forwarding.KnownProxies.Clear(); forwarding.KnownIPNetworks.Clear();
    foreach (var proxy in panelOptions.TrustedProxies)
        forwarding.KnownProxies.Add(IPAddress.Parse(proxy));
});
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddAntiforgery(options => { options.HeaderName = "X-XSRF-TOKEN"; options.Cookie.Name = "mcpanel.xsrf"; options.Cookie.SecurePolicy = panelOptions.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(5), PermitLimit = 8, QueueLimit = 0, AutoReplenishment = true
        }));
});
builder.Services.AddSignalR().AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options => options.MultipartBodyLengthLimit = uploadRequestLimit);
builder.Services.AddHttpClient("upstream", client =>
{
    client.Timeout = TimeSpan.FromMinutes(10); client.DefaultRequestHeaders.UserAgent.ParseAdd(panelOptions.PaperUserAgent);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All });
builder.Services.AddHttpClient("alerts", client => client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Logging.AddFilter("System.Net.Http.HttpClient.alerts", LogLevel.None);
builder.Services.AddHttpClient("minecraft-profile", client =>
{
    client.BaseAddress = new Uri("https://api.minecraftservices.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(panelOptions.PaperUserAgent);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All });

builder.Services.AddSingleton<AsyncKeyedLock>(); builder.Services.AddSingleton<SafePathResolver>(); builder.Services.AddSingleton<SessionAudience>();
builder.Services.AddSingleton<InstancePermissionService>();
builder.Services.AddSingleton<CustomJarService>(); builder.Services.AddSingleton<SoftwareActivationService>();
builder.Services.AddSingleton<CgroupMemoryService>();
builder.Services.AddSingleton<ValidatedDownloadClient>(); builder.Services.AddSingleton<DistributionCatalogService>();
builder.Services.AddSingleton<JavaDiscoveryService>(); builder.Services.AddSingleton<ConsoleService>();
builder.Services.AddSingleton<PersistentRuntimeClient>();
builder.Services.AddSingleton<GateReleaseService>(); builder.Services.AddSingleton<GateConfigurationService>();
builder.Services.AddSingleton<GateBackendConfigurationService>();
builder.Services.AddSingleton<GateApiClient>(); builder.Services.AddSingleton<GateProxyService>();
builder.Services.AddHostedService<GateConfigurationReconciler>();
builder.Services.AddSingleton<ServerExportService>();
builder.Services.AddSingleton<RecoveryBundleService>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<RecoveryBundleService>());
builder.Services.AddSingleton<JobRecoveryService>();
builder.Services.AddSingleton<OperationsMonitor>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<OperationsMonitor>());
builder.Services.AddSingleton<OperationQueue>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<OperationQueue>());
builder.Services.AddSingleton<ProcessSupervisor>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessSupervisor>());
builder.Services.AddSingleton<IServerProcessStatus>(sp => sp.GetRequiredService<ProcessSupervisor>());
builder.Services.AddSingleton<HostMetricsService>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<HostMetricsService>());
builder.Services.AddSingleton<SchedulerService>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
builder.Services.AddSingleton<ModrinthService>(); builder.Services.AddSingleton<ModpackService>(); builder.Services.AddSingleton<ModrinthModInstallerService>();
builder.Services.AddSingleton<ServerInstallerService>(); builder.Services.AddSingleton<PropertiesService>(); builder.Services.AddSingleton<ServerIconService>(); builder.Services.AddSingleton<FileManagerService>(); builder.Services.AddSingleton<ModMetadataService>();
builder.Services.AddSingleton<ServerSoftwareService>();
builder.Services.AddSingleton<BackupService>(); builder.Services.AddSingleton<PlayerService>(); builder.Services.AddSingleton<PlayerInventoryService>(); builder.Services.AddSingleton<ServerQueryService>(); builder.Services.AddSingleton<AdminAuthService>();
builder.Services.AddSingleton<IPasswordHasher<AdminEntity>, PasswordHasher<AdminEntity>>();

var app = builder.Build();
using var panelInstanceLock = new FileStream(paths.StateDatabase + ".panel-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
using var releasePanelLock = app.Lifetime.ApplicationStopped.Register(panelInstanceLock.Dispose);
await InitializeAsync(app.Services);
if (panelOptions.TrustedProxies.Length > 0) app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'";
    if (panelOptions.RequireHttps && !context.Request.IsHttps && !context.Request.Path.StartsWithSegments("/health"))
    { context.Response.StatusCode = 426; await context.Response.WriteAsJsonAsync(new { code = "HTTPS_REQUIRED", message = "Use the configured private HTTPS address." }); return; }
    if (panelOptions.RequireHttps && context.Request.IsHttps) context.Response.Headers.StrictTransportSecurity = "max-age=31536000";
    await next(context);
});
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (PanelException exception) { await PanelProblems.Result(exception, context).ExecuteAsync(context); }
    catch (BadHttpRequestException exception) { await PanelProblems.Result(PanelProblems.BadRequest(exception), context).ExecuteAsync(context); }
    catch (JsonException exception) { await PanelProblems.Result(PanelProblems.Validation(exception.Message), context).ExecuteAsync(context); }
    catch (AntiforgeryValidationException) { await PanelProblems.Result(new PanelException(400, "ANTIFORGERY_FAILED", "The antiforgery token is invalid."), context).ExecuteAsync(context); }
    catch (Exception exception)
    {
        app.Logger.LogError(exception, "Unhandled request failure");
        await PanelProblems.Result(new PanelException(500, "OPERATION_FAILED", "The request failed unexpectedly."), context).ExecuteAsync(context);
    }
});
app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
app.Use(async (context, next) =>
{
    var mutation = context.Request.Path.StartsWithSegments("/api/v1") && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);
    try
    {
        if (mutation && context.User.Identity?.IsAuthenticated == true)
        {
            var segments = context.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 4 && segments[2] == "servers" && Guid.TryParse(segments[3], out var id) &&
                !(segments.Length == 6 && segments[4] == "actions" && segments[5] is "stop" or "kill") &&
                !(segments.Length == 5 && segments[4] == "recover"))
            {
                await using var db = await context.RequestServices.GetRequiredService<IDbContextFactory<StateDbContext>>().CreateDbContextAsync(context.RequestAborted);
                var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, context.RequestAborted);
                if (server?.RecoveryRequired == true) throw PanelProblems.Conflict("RECOVERY_REQUIRED", server.RecoveryReason ?? "Repair the interrupted recovery before changing this server.");
            }
        }
        await next(context);
    }
    catch
    {
        context.Items["audit-failed"] = true;
        throw;
    }
    finally
    {
        if (mutation)
        {
            try
            {
                await context.RequestServices.GetRequiredService<OperationsMonitor>().AuditAsync(context.User.Identity?.Name ?? "anonymous", context.Items["audit-action"] as string ?? context.Request.Method,
                    context.Items["audit-target"] as string ?? ((context.Request.Path.Value ?? "") + (context.Request.Query.TryGetValue("path", out var filePath) ? $" path={filePath}" : "")), context.Items.ContainsKey("audit-failed") ? "failed" : context.Response.StatusCode.ToString(), context.TraceIdentifier,
                    context.Connection.RemoteIpAddress?.ToString(), CancellationToken.None);
            }
            catch (Exception exception) { app.Logger.LogError(exception, "Administrative audit persistence failed"); }
        }
    }
});
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next(context);
});
app.MapPanelApi("/api/v1");
app.MapOperationalEndpoints();
app.MapHub<PanelHub>("/hubs/panel").RequireAuthorization();

var webRoot = panelOptions.WebRoot ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
if (Directory.Exists(webRoot))
{
    var provider = new PhysicalFileProvider(Path.GetFullPath(webRoot));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
    app.MapFallback(async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hubs"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await Results.Problem(statusCode: 404, title: "Endpoint not found.", extensions: new Dictionary<string, object?> { ["code"] = "NOT_FOUND" }).ExecuteAsync(context);
            return;
        }
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(webRoot, "index.html"));
    });
}
else app.MapGet("/", () => Results.Ok(new { name = "MC Panel API", api = "/api/v1", setup = "/api/v1/auth/status" })).AllowAnonymous();

await app.RunAsync();
return 0;

static async Task InitializeAsync(IServiceProvider services)
{
    await using var scope = services.CreateAsyncScope();
    var stateFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<StateDbContext>>();
    var consoleFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ConsoleDbContext>>();
    List<Guid> serverIds;
    string? sessionStamp;
    await using (var state = await stateFactory.CreateDbContextAsync())
    {
        await SchemaMigration.CheckConsoleAsync(scope.ServiceProvider.GetRequiredService<PanelPaths>().ConsoleDatabase);
        await SchemaMigration.MigrateAsync(scope.ServiceProvider.GetRequiredService<PanelPaths>().StateDatabase);
        if (!await state.PanelSettings.AnyAsync()) state.PanelSettings.Add(new PanelSettingsEntity());
        await state.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await ServerImportService.RecoverInterruptedActivationsAsync(
            scope.ServiceProvider.GetRequiredService<PanelPaths>(), state,
            scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<ServerImportService>(),
            CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<BackupService>()
            .RecoverInterruptedRestoresAsync(state, CancellationToken.None);
        var unrecoveredActivations = await scope.ServiceProvider.GetRequiredService<SoftwareActivationService>()
            .RecoverInterruptedAsync(state, CancellationToken.None);
        var staleJobs = await state.Jobs.Where(x => x.State == JobState.Running || x.State == JobState.Queued).ToListAsync();
        foreach (var job in staleJobs) { job.State = JobState.Interrupted; job.Progress = 100; job.Message = "Interrupted"; job.Error = "The panel restarted before this operation completed."; }
        var interruptedUpdates = await state.Servers.Where(x => x.State == ServerState.Updating).ToListAsync();
        foreach (var server in interruptedUpdates.Where(server => !unrecoveredActivations.Contains(server.Id)))
        { server.State = ServerState.Stopped; server.ProcessId = null; }
        if (!services.GetRequiredService<PersistentRuntimeClient>().Enabled)
        {
            var staleServers = await state.Servers.Where(x => x.State == ServerState.Running || x.State == ServerState.Starting || x.State == ServerState.Stopping || x.State == ServerState.BackingUp).ToListAsync();
            foreach (var server in staleServers) { server.State = ServerState.Stopped; server.ProcessId = null; server.StartedAt = null; }
        }
        var interruptedInstalls = await state.Servers.Where(x => x.State == ServerState.Installing).ToListAsync();
        foreach (var server in interruptedInstalls) { server.State = ServerState.Error; server.ProcessId = null; }
        await state.Players.Where(x => x.Online).ExecuteUpdateAsync(x => x.SetProperty(p => p.Online, false));
        await state.SaveChangesAsync();
        serverIds = await state.Servers.Select(x => x.Id).ToListAsync();
        sessionStamp = await state.Admins.Select(x => x.SessionStamp).SingleOrDefaultAsync();
    }
    services.GetRequiredService<SessionAudience>().Initialize(sessionStamp);
    services.GetRequiredService<ModpackService>().CleanupExpiredImports();
    services.GetRequiredService<CustomJarService>().CleanupExpiredImports();
    await using (var console = await consoleFactory.CreateDbContextAsync())
    {
        await console.Database.EnsureCreatedAsync();
        await console.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
    using (var javaTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
    {
        try { await scope.ServiceProvider.GetRequiredService<JavaDiscoveryService>().ScanAsync(javaTimeout.Token); } catch { }
    }
    var consoleService = scope.ServiceProvider.GetRequiredService<ConsoleService>();
    foreach (var serverId in serverIds) await consoleService.PruneAsync(serverId, CancellationToken.None);
    scope.ServiceProvider.GetRequiredService<SoftwareActivationService>().CleanupOrphanedStaging();
}

public partial class Program { }
