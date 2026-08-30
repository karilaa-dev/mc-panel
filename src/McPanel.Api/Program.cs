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
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; options.SlidingExpiration = true; options.ExpireTimeSpan = TimeSpan.FromHours(12);
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
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddAntiforgery(options => { options.HeaderName = "X-XSRF-TOKEN"; options.Cookie.Name = "mcpanel.xsrf"; options.Cookie.HttpOnly = true; options.Cookie.SameSite = SameSiteMode.Strict; });
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
builder.Services.AddHttpClient("minecraft-profile", client =>
{
    client.BaseAddress = new Uri("https://api.minecraftservices.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(panelOptions.PaperUserAgent);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.All });

builder.Services.AddSingleton<AsyncKeyedLock>(); builder.Services.AddSingleton<SafePathResolver>(); builder.Services.AddSingleton<SessionAudience>();
builder.Services.AddSingleton<CgroupMemoryService>();
builder.Services.AddSingleton<ValidatedDownloadClient>(); builder.Services.AddSingleton<DistributionCatalogService>();
builder.Services.AddSingleton<JavaDiscoveryService>(); builder.Services.AddSingleton<ConsoleService>();
builder.Services.AddSingleton<PersistentRuntimeClient>();
builder.Services.AddSingleton<GateReleaseService>(); builder.Services.AddSingleton<GateConfigurationService>();
builder.Services.AddSingleton<GateApiClient>(); builder.Services.AddSingleton<GateProxyService>();
builder.Services.AddSingleton<LegacyGateMigrationService>();
builder.Services.AddSingleton<RuntimeCompatibilityService>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<RuntimeCompatibilityService>());
builder.Services.AddHostedService<GateConfigurationReconciler>();
builder.Services.AddSingleton<OperationQueue>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<OperationQueue>());
builder.Services.AddSingleton<ProcessSupervisor>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<ProcessSupervisor>());
builder.Services.AddSingleton<IServerProcessStatus>(sp => sp.GetRequiredService<ProcessSupervisor>());
builder.Services.AddSingleton<HostMetricsService>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<HostMetricsService>());
builder.Services.AddSingleton<SchedulerService>(); builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
builder.Services.AddSingleton<ModrinthService>(); builder.Services.AddSingleton<ModpackService>(); builder.Services.AddSingleton<ModrinthModInstallerService>();
builder.Services.AddSingleton<ServerInstallerService>(); builder.Services.AddSingleton<PropertiesService>(); builder.Services.AddSingleton<ServerIconService>(); builder.Services.AddSingleton<FileManagerService>(); builder.Services.AddSingleton<ModMetadataService>();
builder.Services.AddSingleton<BackupService>(); builder.Services.AddSingleton<PlayerService>(); builder.Services.AddSingleton<PlayerInventoryService>(); builder.Services.AddSingleton<ServerQueryService>(); builder.Services.AddSingleton<AdminAuthService>();
builder.Services.AddSingleton<IPasswordHasher<AdminEntity>, PasswordHasher<AdminEntity>>();

var app = builder.Build();
await InitializeAsync(app.Services);
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
    if (context.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method))
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next(context);
});
app.MapPanelApi("/api/v1");
app.MapPanelApi("/api");
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
        await state.Database.EnsureCreatedAsync();
        await state.EnsureCompatibleSchemaAsync();
        await scope.ServiceProvider.GetRequiredService<LegacyGateMigrationService>().MigrateAsync(state, CancellationToken.None);
        await state.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        var staleJobs = await state.Jobs.Where(x => x.State == JobState.Running || x.State == JobState.Queued).ToListAsync();
        foreach (var job in staleJobs) { job.State = JobState.Failed; job.Progress = 100; job.Message = "Interrupted"; job.Error = "The panel restarted before this operation completed."; }
        if (!services.GetRequiredService<PersistentRuntimeClient>().Enabled)
        {
            var staleServers = await state.Servers.Where(x => x.State == ServerState.Running || x.State == ServerState.Starting || x.State == ServerState.Stopping || x.State == ServerState.BackingUp || x.State == ServerState.Updating).ToListAsync();
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
    await scope.ServiceProvider.GetRequiredService<ServerIconService>().BackfillAsync(CancellationToken.None);
    await using (var console = await consoleFactory.CreateDbContextAsync())
    {
        await console.Database.EnsureCreatedAsync();
        await console.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await console.EnsureCompatibleSchemaAsync();
    }
    using (var javaTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
    {
        try { await scope.ServiceProvider.GetRequiredService<JavaDiscoveryService>().ScanAsync(javaTimeout.Token); } catch { }
    }
    var consoleService = scope.ServiceProvider.GetRequiredService<ConsoleService>();
    foreach (var serverId in serverIds) await consoleService.PruneAsync(serverId, CancellationToken.None);
    var panelPaths = scope.ServiceProvider.GetRequiredService<PanelPaths>();
    try { foreach (var directory in Directory.EnumerateDirectories(panelPaths.Staging)) Directory.Delete(directory, true); } catch { }
}

public partial class Program { }
