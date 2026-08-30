using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed partial class ServerInstallerService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    DistributionCatalogService catalog,
    ValidatedDownloadClient downloads,
    ModpackService modpacks,
    OperationQueue operations,
    ConsoleService console,
    AsyncKeyedLock keyedLock,
    IOptions<PanelOptions> options,
    GateProxyService gate,
    ILogger<ServerInstallerService> logger,
    CustomJarService? customJars = null,
    InstancePermissionService? permissions = null)
{
    public async Task<(ServerEntity Server, JobDto Job)> CreateAsync(CreateServerRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var clientRequestId = NormalizeClientRequestId(request.ClientRequestId);
        using var creationLock = clientRequestId is null
            ? null : await keyedLock.AcquireAsync(Guid.ParseExact(clientRequestId, "N"), cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        if (await ExistingCreateAsync(db, clientRequestId, cancellationToken) is { } existing) return existing;
        if (request.Kind == ServerKind.CustomJar)
            await (customJars ?? throw new InvalidOperationException("Custom JAR service is unavailable."))
                .InspectAsync(request.CustomJarImportToken!, cancellationToken);
        var runtime = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.JavaRuntimeId, cancellationToken)
            ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
        if (await db.Servers.AnyAsync(x => x.Port == request.Port, cancellationToken))
            throw PanelProblems.Conflict("PORT_IN_USE", "The selected port is already assigned to another server.");
        if (await db.Servers.AnyAsync(x => x.Name.ToLower() == request.Name.Trim().ToLower(), cancellationToken))
            throw PanelProblems.Conflict("VALIDATION_FAILED", "A server with that name already exists.");
        var totalLimitMb = MemorySizing.TotalForExistingHeapMb(request.MemoryMb);
        var totalMemory = HostMetricsService.ReadMemory().Total;
        if ((long)totalLimitMb * 1024 * 1024 > totalMemory * options.Value.MemoryAllocationFraction)
            throw new PanelException(400, "MEMORY_LIMIT_EXCEEDED", "The selected memory exceeds the host allocation limit.");

        CustomJarService.ClaimedCustomJar? customJarClaim = null;
        if (request.Kind == ServerKind.CustomJar)
            customJarClaim = await customJars!.ClaimAsync(request.CustomJarImportToken!, cancellationToken);
        var entity = new ServerEntity
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), Kind = request.Kind, Version = request.Version,
            DistributionBuild = request.Build, LoaderVersion = request.LoaderVersion,
            InstallerVersion = request.InstallerVersion, JavaRuntimeId = runtime.Id,
            MemoryLimitMb = totalLimitMb,
            MemoryMb = request.MemoryMb,
            InitialMemoryMb = request.MemoryMb,
            Port = request.Port, StartOnBoot = request.StartOnBoot,
            State = ServerState.Installing, EulaAcceptedAt = DateTimeOffset.UtcNow
        };
        var pending = OperationQueue.CreatePending("Install", entity.Id, clientRequestId);
        try
        {
            db.Servers.Add(entity);
            db.Jobs.Add(pending);
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            var claim = customJarClaim;
            var job = await operations.HandoffCommittedAsync(pending,
                request.Kind == ServerKind.CustomJar
                    ? (_, jobId, token) => InstallCustomJarAsync(entity.Id, jobId, claim!, token)
                    : (_, jobId, token) => InstallAsync(entity.Id, jobId, request.IncludeExperimental, token), cancellationToken);
            if (job.State == JobState.Failed)
            {
                customJarClaim?.Dispose();
                await MarkHandoffFailureAsync(entity.Id);
            }
            return (entity, job);
        }
        catch
        {
            customJarClaim?.Dispose();
            throw;
        }
    }

    public async Task<(ServerEntity Server, JobDto Job)> CreateModpackAsync(
        CreateModpackServerRequest request, CancellationToken cancellationToken)
    {
        if (request is null) throw PanelProblems.Validation("A modpack server request is required.");
        var clientRequestId = NormalizeClientRequestId(request.ClientRequestId);
        using var creationLock = clientRequestId is null
            ? null : await keyedLock.AcquireAsync(Guid.ParseExact(clientRequestId, "N"), cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        if (await ExistingCreateAsync(db, clientRequestId, cancellationToken) is { } existing) return existing;
        var inspection = await modpacks.InspectAsync(request.ImportToken, cancellationToken);
        ValidateCommon(request.Name, inspection.MinecraftVersion, request.JavaRuntimeId,
            request.MemoryMb, request.Port, request.EulaAccepted);
        var runtime = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.JavaRuntimeId, cancellationToken)
            ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
        if (await db.Servers.AnyAsync(x => x.Port == request.Port, cancellationToken))
            throw PanelProblems.Conflict("PORT_IN_USE", "The selected port is already assigned to another server.");
        if (await db.Servers.AnyAsync(x => x.Name.ToLower() == request.Name.Trim().ToLower(), cancellationToken))
            throw PanelProblems.Conflict("VALIDATION_FAILED", "A server with that name already exists.");
        var totalLimitMb = MemorySizing.TotalForExistingHeapMb(request.MemoryMb);
        var totalMemory = HostMetricsService.ReadMemory().Total;
        if ((long)totalLimitMb * 1024 * 1024 > totalMemory * options.Value.MemoryAllocationFraction)
            throw new PanelException(400, "MEMORY_LIMIT_EXCEEDED", "The selected memory exceeds the host allocation limit.");

        var claim = await modpacks.ClaimAsync(request.ImportToken, cancellationToken);
        var entity = new ServerEntity
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), Kind = inspection.Kind,
            Version = inspection.MinecraftVersion, LoaderVersion = inspection.LoaderVersion,
            JavaRuntimeId = runtime.Id, MemoryLimitMb = totalLimitMb, MemoryMb = request.MemoryMb,
            InitialMemoryMb = request.MemoryMb, Port = request.Port, StartOnBoot = request.StartOnBoot,
            State = ServerState.Installing, EulaAcceptedAt = DateTimeOffset.UtcNow,
            ModpackName = inspection.Name, ModpackVersion = inspection.Version,
            ModrinthProjectId = inspection.ProjectId, ModrinthVersionId = inspection.ModrinthVersionId,
            ModpackSource = inspection.Source
        };
        var pending = OperationQueue.CreatePending("InstallModpack", entity.Id, clientRequestId);
        db.Servers.Add(entity);
        db.Jobs.Add(pending);
        try
        {
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            var job = await operations.HandoffCommittedAsync(pending,
                (_, jobId, token) => InstallModpackAsync(entity.Id, jobId, claim, request.SelectedOptionalFiles, token), cancellationToken);
            if (job.State == JobState.Failed)
            {
                claim.Dispose();
                await MarkHandoffFailureAsync(entity.Id);
            }
            return (entity, job);
        }
        catch
        {
            claim.Dispose();
            throw;
        }
    }

    public async Task<JobDto> QueueUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var kind = await db.Servers.Where(x => x.Id == id).Select(x => (ServerKind?)x.Kind).SingleOrDefaultAsync(cancellationToken)
            ?? throw PanelProblems.NotFound("Server");
        return kind == ServerKind.Gate
            ? await gate.QueueUpdateAsync(id, false, cancellationToken)
            : kind == ServerKind.CustomJar
                ? throw PanelProblems.Conflict("CUSTOM_JAR_UPDATE_UNSUPPORTED", "Change Custom JAR software from the Software page.")
                : await operations.EnqueueAsync("Update", id, (_, jobId, token) => UpdateAsync(id, jobId, token), cancellationToken);
    }

    public async Task<(ServerEntity Server, JobDto Job)> CreateGateAsync(
        CreateGateServerRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || !NameRegex().IsMatch(request.Name.Trim()))
            throw PanelProblems.Validation("Gate server names must contain 2 to 48 letters, numbers, spaces, '-' or '_'.");
        if (request.Port is < 1024 or > 65535) throw PanelProblems.Validation("Gate listener ports must be between 1024 and 65535.");
        var clientRequestId = NormalizeClientRequestId(request.ClientRequestId);
        using var creationLock = clientRequestId is null
            ? null : await keyedLock.AcquireAsync(Guid.ParseExact(clientRequestId, "N"), cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        if (await ExistingCreateAsync(db, clientRequestId, cancellationToken) is { } existing) return existing;
        if (await db.Servers.AnyAsync(x => x.Port == request.Port, cancellationToken))
            throw PanelProblems.Conflict("PORT_IN_USE", "The selected real listener port is already assigned to another server.");
        try { ProcessSupervisor.EnsurePortAvailable(request.Port); }
        catch (PanelException exception) when (exception.Code == "PORT_IN_USE")
        { throw new PanelException(409, "GATE_PORT_IN_USE", $"Real listener port {request.Port} is already in use on this host."); }
        var name = request.Name.Trim();
        if (await db.Servers.AnyAsync(x => x.Name.ToLower() == name.ToLower(), cancellationToken))
            throw PanelProblems.Conflict("VALIDATION_FAILED", "A server with that name already exists.");
        var entity = new ServerEntity
        {
            Id = Guid.NewGuid(), Name = name, Kind = ServerKind.Gate, Version = "Latest",
            JavaRuntimeId = string.Empty, Port = request.Port, MemoryMb = 256, InitialMemoryMb = 256,
            MemoryLimitMb = 256, StartOnBoot = request.StartOnBoot, CrashRecovery = true,
            State = ServerState.Installing, EulaAcceptedAt = DateTimeOffset.UtcNow, LaunchTarget = "gate"
        };
        var pending = OperationQueue.CreatePending("GateInstall", entity.Id, clientRequestId);
        db.Servers.Add(entity);
        db.GateSettings.Add(new GateSettingsEntity { ServerId = entity.Id });
        db.Jobs.Add(pending);
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        var job = await operations.HandoffCommittedAsync(pending,
            (_, jobId, token) => InstallGateAsync(entity.Id, jobId, token), cancellationToken);
        if (job.State == JobState.Failed) await MarkHandoffFailureAsync(entity.Id);
        return (entity, job);
    }

    private async Task MarkHandoffFailureAsync(Guid serverId)
    {
        try
        {
            await using var db = await stateFactory.CreateDbContextAsync(CancellationToken.None);
            var server = await db.Servers.FindAsync([serverId], CancellationToken.None);
            if (server is null) return;
            server.State = ServerState.Error;
            server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception) { logger.LogError(exception, "Could not mark unqueued install {ServerId} as failed", serverId); }
    }

    private async Task InstallGateAsync(Guid id, Guid jobId, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(id, cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.Instance(id));
            await operations.ProgressAsync(jobId, 15, "Resolving the latest stable Gate release", cancellationToken);
            var manifest = await gate.InstallLatestAsync(id, cancellationToken);
            await operations.ProgressAsync(jobId, 85, "Activating the verified Gate binary", cancellationToken);
            await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
            var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
            server.Version = manifest.Version;
            server.LaunchTarget = Path.GetRelativePath(paths.Instance(id), manifest.Executable);
            server.State = ServerState.Stopped;
            server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await console.AppendAsync(id, "system", $"Installed Minekube Gate {manifest.Version} from a verified official binary.", cancellationToken);
            await operations.ProgressAsync(jobId, 95, "Gate installation complete", cancellationToken);
        }
        catch
        {
            try
            {
                await using var db = await stateFactory.CreateDbContextAsync(CancellationToken.None);
                var server = await db.Servers.FindAsync([id], CancellationToken.None);
                if (server is not null) { server.State = ServerState.Error; server.UpdatedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); }
            }
            catch (Exception exception) { logger.LogWarning(exception, "Could not record Gate installation failure"); }
            throw;
        }
    }

    private async Task<(ServerEntity Server, JobDto Job)?> ExistingCreateAsync(
        StateDbContext db, string? clientRequestId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientRequestId)) return null;
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.ClientRequestId == clientRequestId, cancellationToken);
        if (job?.ServerId is not { } serverId) return null;
        var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == serverId, cancellationToken);
        var dto = await operations.GetAsync(job.Id, cancellationToken);
        return server is not null && dto is not null ? (server, dto) : null;
    }

    private static string? NormalizeClientRequestId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Guid.TryParse(value, out _)) throw PanelProblems.Validation("The create request identifier must be a UUID.");
        return Guid.Parse(value).ToString("N");
    }

    private async Task InstallAsync(Guid id, Guid jobId, bool includeExperimental, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(id, cancellationToken);
        var stage = Path.Combine(paths.Staging, $"install-{id:N}-{jobId:N}");
        try
        {
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
            Directory.CreateDirectory(stage);
            await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
            var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
            var runtime = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == server.JavaRuntimeId, cancellationToken)
                ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
            await operations.ProgressAsync(jobId, 10, "Resolving official distribution metadata", cancellationToken);
            var plan = await catalog.ResolveAsync(server.Kind, server.Version, server.DistributionBuild, server.LoaderVersion, server.InstallerVersion, includeExperimental, cancellationToken);
            if (runtime.Major < plan.RequiredJavaMajor)
                throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Minecraft {server.Version} requires Java {plan.RequiredJavaMajor} or newer.");
            if (server.Kind == ServerKind.Forge && plan.RequiredJavaMajor == 8 && runtime.Major != 8)
                throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Legacy Forge for Minecraft {server.Version} requires Java 8.");
            await operations.ProgressAsync(jobId, 25, "Downloading and verifying server files", cancellationToken);
            var artifactPath = Path.Combine(stage, plan.Artifact.FileName);
            await downloads.DownloadAsync(plan.Artifact, artifactPath, cancellationToken);
            if (IsModLoader(server.Kind))
            {
                await operations.ProgressAsync(jobId, 55, $"Running the verified {server.Kind} installer", cancellationToken);
                var launch = await RunLoaderInstallerAsync(server, runtime.Path, plan, artifactPath, stage, cancellationToken);
                server.LaunchMode = launch.Mode; server.LaunchTarget = launch.Target;
                server.LoaderVersion = plan.LoaderVersion; server.InstallerVersion = plan.InstallerVersion;
                File.Delete(artifactPath);
                Directory.CreateDirectory(Path.Combine(stage, "mods"));
            }
            else
            {
                File.Move(artifactPath, Path.Combine(stage, "server.jar"));
                server.LaunchMode = LaunchMode.Jar; server.LaunchTarget = "server.jar";
                server.DistributionBuild = plan.Build;
            }
            server.RequiredJavaMajor = plan.RequiredJavaMajor;
            server.IsExperimental = plan.Experimental;
            await operations.ProgressAsync(jobId, 80, "Writing initial configuration", cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(stage, "eula.txt"), $"# Accepted through MC Panel at {server.EulaAcceptedAt:O}{Environment.NewLine}eula=true{Environment.NewLine}", new UTF8Encoding(false), cancellationToken);
            var properties = PropertiesDocument.Empty();
            properties.Set("server-port", server.Port.ToString()); properties.Set("motd", server.Name);
            properties.Set("max-players", "20"); properties.Set("online-mode", "true");
            await File.WriteAllTextAsync(Path.Combine(stage, "server.properties"), properties.ToString(), new UTF8Encoding(false), cancellationToken);
            var destination = paths.Instance(id);
            if (Directory.Exists(destination)) throw PanelProblems.Conflict("SERVER_BUSY", "The managed server directory already exists.");
            Directory.Move(stage, destination);
            if (permissions is not null) await permissions.NormalizeAsync(id, cancellationToken);
            server.State = ServerState.Stopped; server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            await console.AppendAsync(id, "system", $"Installed {server.Kind} {server.Version} from verified official metadata.", cancellationToken);
            await operations.ProgressAsync(jobId, 95, "Installation complete", cancellationToken);
        }
        catch
        {
            try
            {
                await using var failureDb = await stateFactory.CreateDbContextAsync(CancellationToken.None);
                var server = await failureDb.Servers.FindAsync([id], CancellationToken.None);
                if (server is not null) { server.State = ServerState.Error; server.UpdatedAt = DateTimeOffset.UtcNow; await failureDb.SaveChangesAsync(); }
                await console.AppendAsync(id, "system", "Installation failed. No partial server directory was activated.");
            }
            catch (Exception exception) { logger.LogWarning(exception, "Could not record installation failure"); }
            throw;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private async Task InstallCustomJarAsync(
        Guid id, Guid jobId, CustomJarService.ClaimedCustomJar claim, CancellationToken cancellationToken)
    {
        using (claim)
        using (await keyedLock.AcquireAsync(id, cancellationToken))
        {
            var stage = Path.Combine(paths.Staging, $"install-{id:N}-{jobId:N}");
            try
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);
                await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
                var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
                var runtime = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(
                    x => x.Id == server.JavaRuntimeId, cancellationToken)
                    ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
                var requiredJava = MinecraftJavaVersion.Required(server.Version);
                if (runtime.Major < requiredJava)
                    throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Minecraft {server.Version} requires Java {requiredJava} or newer.");
                await operations.ProgressAsync(jobId, 30, "Validating the uploaded executable JAR", cancellationToken);
                CustomJarService.ValidateExecutableJar(claim.JarPath);
                File.Copy(claim.JarPath, Path.Combine(stage, "custom-server.jar"), false);
                await operations.ProgressAsync(jobId, 70, "Writing initial configuration", cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(stage, "eula.txt"),
                    $"# Accepted through MC Panel at {server.EulaAcceptedAt:O}{Environment.NewLine}eula=true{Environment.NewLine}",
                    new UTF8Encoding(false), cancellationToken);
                var properties = PropertiesDocument.Empty();
                properties.Set("server-port", server.Port.ToString()); properties.Set("motd", server.Name);
                properties.Set("max-players", "20"); properties.Set("online-mode", "true");
                await File.WriteAllTextAsync(Path.Combine(stage, "server.properties"), properties.ToString(), new UTF8Encoding(false), cancellationToken);
                var destination = paths.Instance(id);
                if (Directory.Exists(destination)) throw PanelProblems.Conflict("SERVER_BUSY", "The managed server directory already exists.");
                Directory.Move(stage, destination);
                server.LaunchMode = LaunchMode.Jar;
                server.LaunchTarget = "custom-server.jar";
                server.RequiredJavaMajor = requiredJava;
                server.IsExperimental = false;
                server.State = ServerState.Stopped;
                server.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                if (permissions is not null) await permissions.NormalizeAsync(id, cancellationToken);
                await console.AppendAsync(id, "system", $"Installed custom JAR {claim.FileName} for Minecraft {server.Version}.", cancellationToken);
                await operations.ProgressAsync(jobId, 95, "Custom JAR installation complete", cancellationToken);
            }
            catch
            {
                try
                {
                    await using var failureDb = await stateFactory.CreateDbContextAsync(CancellationToken.None);
                    var server = await failureDb.Servers.FindAsync([id], CancellationToken.None);
                    if (server is not null) { server.State = ServerState.Error; server.UpdatedAt = DateTimeOffset.UtcNow; await failureDb.SaveChangesAsync(); }
                }
                catch (Exception exception) { logger.LogWarning(exception, "Could not record custom JAR installation failure"); }
                throw;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
    }

    private async Task InstallModpackAsync(
        Guid id, Guid jobId, ModpackService.ClaimedModpack claim,
        IReadOnlyCollection<string>? selectedOptionalFiles, CancellationToken cancellationToken)
    {
        using (claim)
        using (await keyedLock.AcquireAsync(id, cancellationToken))
        {
            var stage = Path.Combine(paths.Staging, $"install-{id:N}-{jobId:N}");
            try
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                Directory.CreateDirectory(stage);
                await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
                var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
                var runtime = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(
                    x => x.Id == server.JavaRuntimeId, cancellationToken)
                    ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
                await operations.ProgressAsync(jobId, 5, "Resolving modpack server distribution", cancellationToken);
                var plan = await catalog.ResolveAsync(server.Kind, server.Version, server.DistributionBuild,
                    server.LoaderVersion, server.InstallerVersion, true, cancellationToken);
                if (runtime.Major < plan.RequiredJavaMajor)
                    throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Minecraft {server.Version} requires Java {plan.RequiredJavaMajor} or newer.");
                if (server.Kind == ServerKind.Forge && plan.RequiredJavaMajor == 8 && runtime.Major != 8)
                    throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Legacy Forge for Minecraft {server.Version} requires Java 8.");
                await operations.ProgressAsync(jobId, 15, "Downloading and verifying server files", cancellationToken);
                var artifactPath = Path.Combine(stage, plan.Artifact.FileName);
                await downloads.DownloadAsync(plan.Artifact, artifactPath, cancellationToken);
                if (IsModLoader(server.Kind))
                {
                    await operations.ProgressAsync(jobId, 25, $"Running the verified {server.Kind} installer", cancellationToken);
                    var launch = await RunLoaderInstallerAsync(server, runtime.Path, plan, artifactPath, stage, cancellationToken);
                    server.LaunchMode = launch.Mode; server.LaunchTarget = launch.Target;
                    server.LoaderVersion = plan.LoaderVersion; server.InstallerVersion = plan.InstallerVersion;
                    File.Delete(artifactPath);
                    Directory.CreateDirectory(Path.Combine(stage, "mods"));
                }
                else
                {
                    File.Move(artifactPath, Path.Combine(stage, "server.jar"));
                    server.LaunchMode = LaunchMode.Jar; server.LaunchTarget = "server.jar";
                    server.DistributionBuild = plan.Build;
                }
                server.RequiredJavaMajor = plan.RequiredJavaMajor;
                server.IsExperimental = plan.Experimental;
                var installed = await modpacks.InstallFilesAsync(claim, stage, selectedOptionalFiles,
                    (progress, message) => operations.ProgressAsync(jobId, progress, message, cancellationToken),
                    cancellationToken);
                await operations.ProgressAsync(jobId, 75, "Applying server configuration", cancellationToken);
                var propertiesPath = Path.Combine(stage, "server.properties");
                var properties = File.Exists(propertiesPath)
                    ? PropertiesDocument.Parse(await File.ReadAllTextAsync(propertiesPath, cancellationToken))
                    : PropertiesDocument.Empty();
                properties.Set("server-port", server.Port.ToString());
                if (properties.Get("motd") is null) properties.Set("motd", server.Name);
                if (properties.Get("max-players") is null) properties.Set("max-players", "20");
                if (properties.Get("online-mode") is null) properties.Set("online-mode", "true");
                await File.WriteAllTextAsync(propertiesPath, properties.ToString(), new UTF8Encoding(false), cancellationToken);
                await File.WriteAllTextAsync(Path.Combine(stage, "eula.txt"),
                    $"# Accepted through MC Panel at {server.EulaAcceptedAt:O}{Environment.NewLine}eula=true{Environment.NewLine}",
                    new UTF8Encoding(false), cancellationToken);
                await modpacks.CommitBaselineAsync(server, claim, installed, stage, cancellationToken);
                var destination = paths.Instance(id);
                if (Directory.Exists(destination)) throw PanelProblems.Conflict("SERVER_BUSY", "The managed server directory already exists.");
                Directory.Move(stage, destination);
                if (permissions is not null) await permissions.NormalizeAsync(id, cancellationToken);
                server.State = ServerState.Stopped;
                server.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await console.AppendAsync(id, "system",
                    $"Installed modpack {server.ModpackName} {server.ModpackVersion} for {server.Kind} {server.Version}.",
                    cancellationToken);
                await operations.ProgressAsync(jobId, 95, "Modpack installation complete", cancellationToken);
            }
            catch
            {
                modpacks.Delete(id);
                try
                {
                    await using var failureDb = await stateFactory.CreateDbContextAsync(CancellationToken.None);
                    var server = await failureDb.Servers.FindAsync([id], CancellationToken.None);
                    if (server is not null)
                    {
                        server.State = ServerState.Error; server.UpdatedAt = DateTimeOffset.UtcNow;
                        await failureDb.SaveChangesAsync();
                    }
                    await console.AppendAsync(id, "system", "Modpack installation failed. No partial server directory was activated.");
                }
                catch (Exception exception) { logger.LogWarning(exception, "Could not record modpack installation failure"); }
                throw;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
    }

    private async Task UpdateAsync(Guid id, Guid jobId, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(id, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([id], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        if (server.State != ServerState.Stopped) throw PanelProblems.Conflict("SERVER_NOT_STOPPED", "Stop the server before updating it.");
        var runtime = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == server.JavaRuntimeId, cancellationToken)
            ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
        server.State = ServerState.Updating; await db.SaveChangesAsync(cancellationToken);
        var stage = Path.Combine(paths.Staging, $"update-{id:N}-{jobId:N}");
        try
        {
            Directory.CreateDirectory(stage);
            await operations.ProgressAsync(jobId, 15, "Resolving latest pinned-version build", cancellationToken);
            var plan = await catalog.ResolveAsync(server.Kind, server.Version, null, null, null, server.IsExperimental, cancellationToken);
            if (runtime.Major < plan.RequiredJavaMajor)
                throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Minecraft {server.Version} requires Java {plan.RequiredJavaMajor} or newer.");
            if (server.Kind == ServerKind.Forge && plan.RequiredJavaMajor == 8 && runtime.Major != 8)
                throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Legacy Forge for Minecraft {server.Version} requires Java 8.");
            var artifact = Path.Combine(stage, plan.Artifact.FileName);
            await downloads.DownloadAsync(plan.Artifact, artifact, cancellationToken);
            await operations.ProgressAsync(jobId, 60, "Activating verified update", cancellationToken);
            if (IsModLoader(server.Kind))
            {
                var launch = await RunLoaderInstallerAsync(server, runtime.Path, plan, artifact, stage, cancellationToken);
                File.Delete(artifact);
                CopyDirectory(stage, paths.Instance(id));
                server.LaunchMode = launch.Mode; server.LaunchTarget = launch.Target;
                server.LoaderVersion = plan.LoaderVersion; server.InstallerVersion = plan.InstallerVersion;
            }
            else
            {
                File.Move(artifact, Path.Combine(paths.Instance(id), "server.jar"), true);
                server.DistributionBuild = plan.Build;
            }
            server.RequiredJavaMajor = plan.RequiredJavaMajor;
            server.IsExperimental = plan.Experimental;
            server.State = ServerState.Stopped; server.RestartRequired = false; server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            if (permissions is not null) await permissions.NormalizeAsync(id, cancellationToken);
            await console.AppendAsync(id, "system", $"Updated {server.Kind} for Minecraft {server.Version}.", cancellationToken);
        }
        catch
        {
            server.State = ServerState.Stopped; await db.SaveChangesAsync(CancellationToken.None); throw;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    internal async Task<(LaunchMode Mode, string Target)> RunLoaderInstallerAsync(
        ServerEntity server,
        string javaPath,
        InstallPlan plan,
        string installerPath,
        string stage,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = javaPath, WorkingDirectory = stage, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        var arguments = server.Kind == ServerKind.Fabric
            ? new[] { "-jar", installerPath, "server", "-mcversion", server.Version, "-loader", plan.LoaderVersion!, "-downloadMinecraft" }
            : new[] { "-jar", installerPath, "--installServer" };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        ClearJavaInjection(start);
        using var process = Process.Start(start) ?? throw new PanelException(500, "OPERATION_FAILED", $"The {server.Kind} installer could not start.");
        var stdout = PumpAsync(process.StandardOutput, server.Id, "system", cancellationToken);
        var stderr = PumpAsync(process.StandardError, server.Id, "stderr", cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            try { await Task.WhenAll(stdout, stderr); } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new PanelException(504, "OPERATION_FAILED", $"The {server.Kind} installer timed out.");
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new PanelException(502, "OPERATION_FAILED", $"The {server.Kind} installer failed.");
        return FindLaunchTarget(server.Kind, server.Version, plan.LoaderVersion!, stage);
    }

    internal static (LaunchMode Mode, string Target) FindLaunchTarget(ServerKind kind, string minecraftVersion, string loaderVersion, string root)
    {
        if (kind == ServerKind.Fabric)
        {
            const string fabricTarget = "fabric-server-launch.jar";
            if (File.Exists(Path.Combine(root, fabricTarget))) return (LaunchMode.Jar, fabricTarget);
        }
        else
        {
            var coordinate = kind == ServerKind.Forge ? $"{minecraftVersion}-{loaderVersion}" : loaderVersion;
            var argumentTarget = kind == ServerKind.Forge
                ? Path.Combine("libraries", "net", "minecraftforge", "forge", coordinate, "unix_args.txt")
                : Path.Combine("libraries", "net", "neoforged", "neoforge", coordinate, "unix_args.txt");
            if (File.Exists(Path.Combine(root, argumentTarget))) return (LaunchMode.ArgumentFile, argumentTarget);
            if (kind == ServerKind.Forge)
            {
                foreach (var candidate in new[]
                {
                    $"forge-{coordinate}.jar",
                    $"forge-{coordinate}-universal.jar",
                    $"minecraftforge-universal-{coordinate}.jar"
                })
                    if (File.Exists(Path.Combine(root, candidate))) return (LaunchMode.Jar, candidate);
            }
        }
        throw new PanelException(502, "OPERATION_FAILED", $"The {kind} installer did not produce a supported server launcher.");
    }

    private async Task PumpAsync(StreamReader reader, Guid id, string stream, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            try { await console.AppendAsync(id, stream, line, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogError(exception, "Loader installer console persistence failed for {ServerId}; continuing to drain {Stream}", id, stream); }
        }
    }

    private static void ClearJavaInjection(ProcessStartInfo start)
    {
        start.Environment.Remove("JAVA_TOOL_OPTIONS"); start.Environment.Remove("_JAVA_OPTIONS"); start.Environment.Remove("JDK_JAVA_OPTIONS");
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("MCPANEL_", StringComparison.OrdinalIgnoreCase) || x.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
    }

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) return;
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    internal static bool IsModLoader(ServerKind kind) => kind is ServerKind.Fabric or ServerKind.Forge or ServerKind.NeoForge;

    private static void Validate(CreateServerRequest request)
    {
        if (request is null) throw PanelProblems.Validation("A server request is required.");
        if (request.Kind == ServerKind.Gate)
            throw PanelProblems.Validation("Create Gate through the Gate Proxy server type workflow.");
        if (request.Kind == ServerKind.CustomJar && string.IsNullOrWhiteSpace(request.CustomJarImportToken))
            throw PanelProblems.Validation("Upload an executable custom JAR before creating the server.");
        if (request.Kind != ServerKind.CustomJar && !string.IsNullOrWhiteSpace(request.CustomJarImportToken))
            throw PanelProblems.Validation("A custom JAR import token is only valid for Custom JAR servers.");
        ValidateCommon(request.Name, request.Version, request.JavaRuntimeId, request.MemoryMb,
            request.Port, request.EulaAccepted);
        if (request.Version.Length > 64 || request.Build?.Length > 64 || request.LoaderVersion?.Length > 64 || request.InstallerVersion?.Length > 64)
            throw PanelProblems.Validation("Distribution metadata values are too long.");
    }

    private static void ValidateCommon(
        string name, string version, string javaRuntimeId, int memoryMb, int port, bool eulaAccepted)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(javaRuntimeId))
            throw PanelProblems.Validation("Name, version, and Java runtime are required.");
        if (!eulaAccepted) throw PanelProblems.Validation("You must explicitly accept the Minecraft EULA.");
        if (!NameRegex().IsMatch(name.Trim())) throw PanelProblems.Validation("Server names may contain letters, numbers, spaces, '-' and '_'.");
        if (memoryMb is < PanelOptions.MinimumServerMemoryMb or > 1_048_576 || memoryMb % PanelOptions.ServerMemoryStepMb != 0)
            throw PanelProblems.Validation($"RAM must be at least {PanelOptions.MinimumServerMemoryMb} MiB and use {PanelOptions.ServerMemoryStepMb} MiB increments.");
        if (port is < 1024 or > 65535) throw PanelProblems.Validation("Port must be between 1024 and 65535.");
    }

    [GeneratedRegex("^[A-Za-z0-9 _-]{2,48}$")]
    private static partial Regex NameRegex();
}
