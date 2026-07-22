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
    OperationQueue operations,
    ConsoleService console,
    AsyncKeyedLock keyedLock,
    IOptions<PanelOptions> options,
    ILogger<ServerInstallerService> logger)
{
    public async Task<(ServerEntity Server, JobDto Job)> CreateAsync(CreateServerRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var runtime = await db.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.JavaRuntimeId, cancellationToken)
            ?? throw new PanelException(400, "JAVA_RUNTIME_NOT_FOUND", "The selected Java runtime was not found.");
        if (await db.Servers.AnyAsync(x => x.Port == request.Port, cancellationToken))
            throw PanelProblems.Conflict("PORT_IN_USE", "The selected port is already assigned to another server.");
        if (await db.Servers.AnyAsync(x => x.Name.ToLower() == request.Name.Trim().ToLower(), cancellationToken))
            throw PanelProblems.Conflict("VALIDATION_FAILED", "A server with that name already exists.");
        var totalMemory = HostMetricsService.ReadMemory().Total;
        if ((long)request.MemoryMb * 1024 * 1024 > totalMemory * options.Value.MemoryAllocationFraction)
            throw new PanelException(400, "MEMORY_LIMIT_EXCEEDED", "The selected memory exceeds the host allocation limit.");

        var entity = new ServerEntity
        {
            Id = Guid.NewGuid(), Name = request.Name.Trim(), Kind = request.Kind, Version = request.Version,
            DistributionBuild = request.Build, LoaderVersion = request.LoaderVersion,
            InstallerVersion = request.InstallerVersion, JavaRuntimeId = runtime.Id,
            MemoryMb = request.MemoryMb, InitialMemoryMb = request.MemoryMb, Port = request.Port, StartOnBoot = request.StartOnBoot,
            State = ServerState.Installing, EulaAcceptedAt = DateTimeOffset.UtcNow
        };
        db.Servers.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var job = await operations.EnqueueAsync("Install", entity.Id,
                (_, jobId, token) => InstallAsync(entity.Id, jobId, request.IncludeExperimental, token), cancellationToken);
            return (entity, job);
        }
        catch
        {
            // The server row reserves its name and port before the install is queued. If that
            // handoff fails, leave a visible, deletable Error entry instead of a stuck install.
            try
            {
                entity.State = ServerState.Error;
                entity.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception exception) { logger.LogError(exception, "Could not mark unqueued install {ServerId} as failed", entity.Id); }
            throw;
        }
    }

    public Task<JobDto> QueueUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        operations.EnqueueAsync("Update", id, (_, jobId, token) => UpdateAsync(id, jobId, token), cancellationToken);

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
            await console.AppendAsync(id, "system", $"Updated {server.Kind} for Minecraft {server.Version}.", cancellationToken);
        }
        catch
        {
            server.State = ServerState.Stopped; await db.SaveChangesAsync(CancellationToken.None); throw;
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private async Task<(LaunchMode Mode, string Target)> RunLoaderInstallerAsync(
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

    private static bool IsModLoader(ServerKind kind) => kind is ServerKind.Fabric or ServerKind.Forge or ServerKind.NeoForge;

    private static void Validate(CreateServerRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Version) || string.IsNullOrWhiteSpace(request.JavaRuntimeId))
            throw PanelProblems.Validation("Name, version, and Java runtime are required.");
        if (!request.EulaAccepted) throw PanelProblems.Validation("You must explicitly accept the Minecraft EULA.");
        if (!NameRegex().IsMatch(request.Name.Trim())) throw PanelProblems.Validation("Server names may contain letters, numbers, spaces, '-' and '_'.");
        if (request.MemoryMb < PanelOptions.MinimumServerMemoryMb || request.MemoryMb % PanelOptions.ServerMemoryStepMb != 0)
            throw PanelProblems.Validation($"Memory must be at least {PanelOptions.MinimumServerMemoryMb} MiB and use {PanelOptions.ServerMemoryStepMb} MiB increments.");
        if (request.Port is < 1024 or > 65535) throw PanelProblems.Validation("Port must be between 1024 and 65535.");
        if (request.Version.Length > 64 || request.Build?.Length > 64 || request.LoaderVersion?.Length > 64 || request.InstallerVersion?.Length > 64)
            throw PanelProblems.Validation("Distribution metadata values are too long.");
    }

    [GeneratedRegex("^[A-Za-z0-9 _-]{2,48}$")]
    private static partial Regex NameRegex();
}
