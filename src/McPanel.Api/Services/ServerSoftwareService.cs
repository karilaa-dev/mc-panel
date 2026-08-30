using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed class ServerSoftwareService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    DistributionCatalogService catalog,
    ValidatedDownloadClient downloads,
    ServerInstallerService installer,
    CustomJarService customJars,
    BackupService backups,
    OperationQueue operations,
    ConsoleService console,
    AsyncKeyedLock keyedLock,
    IServerProcessStatus processStatus,
    InstancePermissionService permissions,
    SoftwareActivationService activations,
    ModpackService modpacks,
    ILogger<ServerSoftwareService> logger)
{
    public async Task<ServerSoftwareDto> GetAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serverId, cancellationToken)
            ?? throw PanelProblems.NotFound("Server");
        EnsureRegular(server);
        return new ServerSoftwareDto(
            server.Kind, server.Version, server.DistributionBuild, server.LoaderVersion, server.InstallerVersion,
            server.LaunchMode, server.LaunchTarget, server.JavaRuntimeId, server.RequiredJavaMajor,
            server.IsExperimental, customJars.Candidates(paths.Instance(serverId), server.LaunchTarget));
    }

    public async Task<JobDto> QueueChangeAsync(Guid serverId, ChangeServerSoftwareRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var requestId = NormalizeRequestId(request.ClientRequestId);
        using var requestLock = requestId is null
            ? null : await keyedLock.AcquireAsync(Guid.ParseExact(requestId, "N"), cancellationToken);
        await using (var db = await stateFactory.CreateDbContextAsync(cancellationToken))
        {
            if (requestId is not null && await db.Jobs.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ClientRequestId == requestId, cancellationToken) is { } existing)
                return await operations.GetAsync(existing.Id, cancellationToken)
                    ?? throw new PanelException(500, "OPERATION_FAILED", "The existing server core change job could not be loaded.");
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serverId, cancellationToken)
                ?? throw PanelProblems.NotFound("Server");
            EnsureRegular(server);
            EnsureStopped(server);
            if (request.Kind == ServerKind.CustomJar && request.CustomJarImportToken is { Length: > 0 } token)
                await customJars.InspectAsync(token, cancellationToken);
        }
        return await operations.EnqueueAsync("ChangeSoftware", serverId,
            (_, jobId, token) => ChangeAsync(serverId, jobId, request, token), cancellationToken, requestId);
    }

    private async Task ChangeAsync(Guid serverId, Guid jobId, ChangeServerSoftwareRequest request, CancellationToken cancellationToken)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.FindAsync([serverId], cancellationToken) ?? throw PanelProblems.NotFound("Server");
        EnsureRegular(server);
        EnsureStopped(server);
        var original = SoftwareActivationService.SoftwareMetadataSnapshot.Capture(server);
        var runtime = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.JavaRuntimeId, cancellationToken)
            ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");

        if (request.CreateBackup)
        {
            await operations.ProgressAsync(jobId, 5, "Creating the pre-change backup", cancellationToken);
            await backups.CreateLockedAsync(serverId, jobId, "Before server core change", cancellationToken);
            await db.Entry(server).ReloadAsync(cancellationToken);
            EnsureStopped(server);
        }

        server.State = ServerState.Updating;
        await db.SaveChangesAsync(cancellationToken);
        var stage = Path.Combine(paths.Staging, $"software-{serverId:N}-{jobId:N}");
        var rollback = Path.Combine(paths.Staging, $"software-rollback-{serverId:N}-{jobId:N}");
        CustomJarService.ClaimedCustomJar? claim = null;
        SoftwareActivationService.ActivationTransaction? activation = null;
        var committed = false;
        try
        {
            Directory.CreateDirectory(stage);
            var launchMode = LaunchMode.Jar;
            string launchTarget;
            string? build = null;
            string? loader = null;
            string? installerVersion = null;
            bool experimental = false;
            int requiredJava;

            if (request.Kind == ServerKind.CustomJar)
            {
                requiredJava = MinecraftJavaVersion.Required(request.Version);
                EnsureJava(runtime.Major, requiredJava, request.Kind, request.Version);
                if (request.CustomJarImportToken is { Length: > 0 } token)
                {
                    await operations.ProgressAsync(jobId, 25, "Validating the uploaded custom JAR", cancellationToken);
                    claim = await customJars.ClaimAsync(token, cancellationToken);
                    File.Copy(claim.JarPath, Path.Combine(stage, "custom-server.jar"), false);
                    launchTarget = "custom-server.jar";
                }
                else
                {
                    launchTarget = customJars.ResolveExisting(paths.Instance(serverId), request.ExistingJarPath!);
                }
            }
            else
            {
                await operations.ProgressAsync(jobId, 15, "Resolving official server core metadata", cancellationToken);
                var plan = await catalog.ResolveAsync(request.Kind, request.Version, request.Build,
                    request.LoaderVersion, request.InstallerVersion, request.IncludeExperimental, cancellationToken);
                requiredJava = plan.RequiredJavaMajor;
                EnsureJava(runtime.Major, requiredJava, request.Kind, request.Version);
                var artifact = Path.Combine(stage, plan.Artifact.FileName);
                await operations.ProgressAsync(jobId, 30, "Downloading and verifying server core", cancellationToken);
                await downloads.DownloadAsync(plan.Artifact, artifact, cancellationToken);
                if (ServerInstallerService.IsModLoader(request.Kind))
                {
                    var target = new ServerEntity
                    {
                        Id = server.Id, Name = server.Name, Kind = request.Kind, Version = request.Version,
                        JavaRuntimeId = runtime.Id
                    };
                    await operations.ProgressAsync(jobId, 50, $"Running the verified {request.Kind} installer", cancellationToken);
                    var launch = await installer.RunLoaderInstallerAsync(target, runtime.Path, plan, artifact, stage, cancellationToken);
                    File.Delete(artifact);
                    Directory.CreateDirectory(Path.Combine(stage, "mods"));
                    launchMode = launch.Mode;
                    launchTarget = launch.Target;
                    loader = plan.LoaderVersion;
                    installerVersion = plan.InstallerVersion;
                }
                else
                {
                    File.Move(artifact, Path.Combine(stage, "server.jar"));
                    launchTarget = "server.jar";
                    build = plan.Build;
                }
                experimental = plan.Experimental;
            }

            await operations.ProgressAsync(jobId, 70, "Activating the new launch files", cancellationToken);
            activation = activations.Begin(serverId, stage, rollback, original);
            activation.Activate();
            await permissions.NormalizeMutationsAsync(serverId, activation.ActivatedPaths, cancellationToken);
            server.Kind = request.Kind;
            server.Version = request.Version.Trim();
            server.DistributionBuild = build;
            server.LoaderVersion = loader;
            server.InstallerVersion = installerVersion;
            server.LaunchMode = launchMode;
            server.LaunchTarget = launchTarget.Replace(Path.DirectorySeparatorChar, '/');
            server.JavaRuntimeId = runtime.Id;
            server.RequiredJavaMajor = requiredJava;
            server.IsExperimental = experimental;
            server.ModpackName = null;
            server.ModpackVersion = null;
            server.ModrinthProjectId = null;
            server.ModrinthVersionId = null;
            server.ModpackSource = null;
            server.RestartRequired = false;
            server.State = ServerState.Stopped;
            server.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            activation.MarkCommitted();
            committed = true;
            try { activation.Commit(); }
            catch (Exception cleanupException)
            {
                logger.LogWarning(cleanupException,
                    "Server core activation committed for {ServerId}, but rollback cleanup will be retried at startup", serverId);
            }
            try { modpacks.Delete(serverId); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not remove stale modpack baseline for {ServerId}", serverId); }
            try
            {
                await console.AppendAsync(serverId, "system",
                    $"Changed server core to {(request.Kind == ServerKind.CustomJar ? "Custom JAR" : request.Kind)} for Minecraft {server.Version}. Existing worlds and content were preserved.",
                    cancellationToken);
            }
            catch (Exception exception) { logger.LogWarning(exception, "Could not append server core change log for {ServerId}", serverId); }
            await operations.ProgressAsync(jobId, 95, "Server core change complete", cancellationToken);
        }
        catch (Exception exception)
        {
            Exception? rollbackFailure = null;
            Exception? claimRestoreFailure = null;
            if (!committed)
            {
                try
                {
                    activation?.PrepareRollback();
                    activation?.Rollback();
                }
                catch (Exception rollbackException)
                {
                    rollbackFailure = rollbackException;
                    logger.LogError(rollbackException,
                        "Server core activation rollback failed for {ServerId}; preserving rollback files", serverId);
                }
                original.Restore(server);
                try { await db.SaveChangesAsync(CancellationToken.None); } catch { }
                if (claim is not null)
                {
                    try
                    {
                        claim.Restore();
                        claim = null;
                    }
                    catch (Exception restoreException)
                    {
                        claimRestoreFailure = restoreException;
                        logger.LogError(restoreException,
                            "Could not restore the custom JAR upload after a failed core change for {ServerId}", serverId);
                    }
                }
            }
            if (rollbackFailure is not null || claimRestoreFailure is not null)
                throw new AggregateException("The server core change failed and its recovery could not be completed.",
                    new[] { exception, rollbackFailure, claimRestoreFailure }.OfType<Exception>());
            throw;
        }
        finally
        {
            claim?.Dispose();
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { }
            try
            {
                if ((activation is null || activation.IsFinished) && Directory.Exists(rollback))
                    Directory.Delete(rollback, true);
            }
            catch { }
        }
    }

    private void EnsureStopped(ServerEntity server)
    {
        if (server.State != ServerState.Stopped || processStatus.IsRunning(server.Id))
            throw PanelProblems.Conflict("SERVER_NOT_STOPPED", "Stop the server before changing its server core.");
    }

    private static void EnsureRegular(ServerEntity server)
    {
        if (server.Kind == ServerKind.Gate)
            throw PanelProblems.Conflict("GATE_SOFTWARE_UNSUPPORTED", "Gate proxy configuration is managed from the Gate page.");
    }

    private static void EnsureJava(int actual, int required, ServerKind kind, string version)
    {
        if (actual < required || kind == ServerKind.Forge && required == 8 && actual != 8)
            throw new PanelException(400, "JAVA_VERSION_INCOMPATIBLE", $"Minecraft {version} requires Java {required}{(kind == ServerKind.Forge && required == 8 ? " exactly" : " or newer")}.");
    }

    private static void Validate(ChangeServerSoftwareRequest request)
    {
        if (request is null) throw PanelProblems.Validation("A server core change request is required.");
        if (request.Kind == ServerKind.Gate) throw PanelProblems.Validation("Gate is not a regular server core.");
        if (string.IsNullOrWhiteSpace(request.Version) || request.Version.Length > 64 || string.IsNullOrWhiteSpace(request.JavaRuntimeId))
            throw PanelProblems.Validation("Minecraft version and Java runtime are required.");
        var upload = !string.IsNullOrWhiteSpace(request.CustomJarImportToken);
        var existing = !string.IsNullOrWhiteSpace(request.ExistingJarPath);
        if (request.Kind == ServerKind.CustomJar && upload == existing)
            throw PanelProblems.Validation("Choose exactly one custom JAR source: a new upload or an existing JAR.");
        if (request.Kind != ServerKind.CustomJar && (upload || existing))
            throw PanelProblems.Validation("Custom JAR sources are only valid for a Custom JAR server core.");
        if (request.Build?.Length > 64 || request.LoaderVersion?.Length > 64 || request.InstallerVersion?.Length > 64)
            throw PanelProblems.Validation("Server core metadata values are too long.");
    }

    private static string? NormalizeRequestId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Guid.TryParse(value, out var id)) throw PanelProblems.Validation("The client request identifier must be a UUID.");
        return id.ToString("N");
    }

}
