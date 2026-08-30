using System.Security.Cryptography;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed class ModrinthModInstallerService(
    PanelPaths paths,
    ModrinthService modrinth,
    ValidatedDownloadClient downloads,
    OperationQueue operations,
    AsyncKeyedLock keyedLock,
    IDbContextFactory<StateDbContext> stateFactory,
    IServerProcessStatus processStatus,
    ConsoleService console,
    InstancePermissionService? permissions = null)
{
    private const long MaximumCombinedArtifactBytes = 1_073_741_824;

    public Task<JobDto> QueueAsync(
        Guid serverId, InstallModrinthModRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProjectId) || string.IsNullOrWhiteSpace(request.VersionId))
            throw PanelProblems.Validation("A Modrinth project and version are required.");
        return operations.EnqueueAsync("InstallMod", serverId,
            (_, jobId, token) => InstallAsync(
                serverId, request.ProjectId, request.VersionId,
                request.SelectedDependencyProjectIds, false, jobId, token),
            cancellationToken);
    }

    public Task<JobDto> QueuePluginAsync(
        Guid serverId, InstallModrinthPluginRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ProjectId) || string.IsNullOrWhiteSpace(request.VersionId))
            throw PanelProblems.Validation("A Modrinth project and version are required.");
        return operations.EnqueueAsync("InstallPlugin", serverId,
            (_, jobId, token) => InstallAsync(
                serverId, request.ProjectId, request.VersionId,
                request.SelectedDependencyProjectIds, true, jobId, token),
            cancellationToken);
    }

    private async Task InstallAsync(
        Guid serverId, string projectId, string versionId,
        IReadOnlyCollection<string>? selectedDependencyProjectIds,
        bool plugin, Guid jobId,
        CancellationToken cancellationToken)
    {
        var artifact = plugin ? "plugin" : "mod";
        var stage = Path.Combine(paths.Staging, $"{artifact}-{serverId:N}-{jobId:N}");
        Directory.CreateDirectory(stage);
        try
        {
            await operations.ProgressAsync(jobId, 10, "Resolving compatible Modrinth version", cancellationToken);
            var resolved = plugin
                ? await modrinth.ResolvePluginAsync(serverId, projectId, versionId, cancellationToken)
                : await modrinth.ResolveModAsync(serverId, projectId, versionId, cancellationToken);
            var (_, version, file) = resolved;
            var selectedDependencies = await modrinth.ResolveDependenciesAsync(
                serverId, version, selectedDependencyProjectIds, plugin, cancellationToken);
            var installed = await modrinth.InstalledArtifactsAsync(
                serverId, plugin, cancellationToken);
            var installedMain = installed
                .Where(x => x.ProjectId.Equals(version.ProjectId, StringComparison.Ordinal))
                .ToList();
            var mainAlreadyInstalled = installedMain
                .Any(x => x.VersionId.Equals(version.Id, StringComparison.Ordinal));
            if (installedMain.Count > 0 && !mainAlreadyInstalled)
                throw AlreadyInstalledConflict(artifact, version, installedMain);
            var dependencies = new List<(ModrinthVersion Version, ModrinthFile File)>();
            var alreadyInstalledDependencyCount = 0;
            foreach (var dependency in selectedDependencies)
            {
                var matches = installed
                    .Where(item => item.ProjectId.Equals(
                        dependency.Version.ProjectId, StringComparison.Ordinal))
                    .ToList();
                if (matches.Count == 0)
                {
                    dependencies.Add(dependency);
                    continue;
                }
                if (!matches.Any(item => item.VersionId.Equals(
                        dependency.Version.Id, StringComparison.Ordinal)))
                    throw AlreadyInstalledConflict("dependency", dependency.Version, matches);
                alreadyInstalledDependencyCount++;
            }
            var installables = new Dictionary<string, (ModrinthVersion Version, ModrinthFile File)>(
                StringComparer.OrdinalIgnoreCase);
            IEnumerable<(ModrinthVersion Version, ModrinthFile File)> candidates = mainAlreadyInstalled
                ? dependencies
                : dependencies.Append((Version: version, File: file));
            foreach (var item in candidates)
            {
                if (installables.TryGetValue(item.File.FileName, out var existing))
                {
                    if (!existing.File.Sha512.Equals(item.File.Sha512, StringComparison.OrdinalIgnoreCase))
                        throw PanelProblems.Validation(
                            $"Selected Modrinth files conflict at {item.File.FileName}.");
                    continue;
                }
                installables[item.File.FileName] = item;
            }
            long combinedSize = 0;
            try
            {
                foreach (var item in installables.Values)
                    combinedSize = checked(combinedSize + item.File.Size);
            }
            catch (OverflowException)
            {
                throw new PanelException(
                    502, "INSTALL_DOWNLOAD_REJECTED",
                    "The selected Modrinth artifacts are unexpectedly large.");
            }
            if (combinedSize is < 0 or > MaximumCombinedArtifactBytes)
                throw new PanelException(
                    502, "INSTALL_DOWNLOAD_REJECTED",
                    "The selected Modrinth artifacts are unexpectedly large.");

            await operations.ProgressAsync(
                jobId, 20,
                dependencies.Count == 0
                    ? $"Downloading and verifying {file.FileName}"
                    : $"Downloading and verifying {installables.Count} selected files",
                cancellationToken);
            await Parallel.ForEachAsync(
                installables.Values,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = 4,
                    CancellationToken = cancellationToken
                },
                async (item, token) =>
                {
                    await downloads.DownloadAsync(new(
                            item.File.Url, "sha512", item.File.Sha512, item.File.Size,
                            item.File.FileName, DownloadPolicy.Modrinth),
                        Path.Combine(stage, item.File.FileName), token);
                });

            using var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
            await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
            var server = await db.Servers.SingleOrDefaultAsync(x => x.Id == serverId, cancellationToken)
                         ?? throw PanelProblems.NotFound("Server");
            var running = processStatus.IsRunning(serverId);
            var stable = server.State switch
            {
                ServerState.Running => running,
                ServerState.Stopped or ServerState.Crashed => !running,
                _ => false
            };
            if (!stable)
                throw PanelProblems.Conflict("SERVER_BUSY", $"{(plugin ? "Plugins" : "Mods")} cannot be changed while the server is changing state.");
            if (plugin ? ModrinthService.PluginLoader(server.Kind) is null : ModrinthService.Loader(server.Kind) is null)
                throw PanelProblems.Validation($"This server does not support Modrinth {artifact}s.");
            var currentInstalled = await modrinth.InstalledArtifactsAsync(
                serverId, plugin, cancellationToken);
            var currentMain = currentInstalled
                .Where(x => x.ProjectId.Equals(version.ProjectId, StringComparison.Ordinal))
                .ToList();
            if (currentMain.Count > 0 &&
                !currentMain.Any(x => x.VersionId.Equals(version.Id, StringComparison.Ordinal)))
                throw AlreadyInstalledConflict(artifact, version, currentMain);
            if (currentMain.Any(x => x.VersionId.Equals(version.Id, StringComparison.Ordinal)))
                installables.Remove(file.FileName);
            foreach (var dependency in dependencies)
            {
                var matches = currentInstalled
                    .Where(item => item.ProjectId.Equals(
                        dependency.Version.ProjectId, StringComparison.Ordinal))
                    .ToList();
                if (matches.Count == 0)
                    continue;
                if (!matches.Any(item => item.VersionId.Equals(
                        dependency.Version.Id, StringComparison.Ordinal)))
                    throw AlreadyInstalledConflict("dependency", dependency.Version, matches);
                if (installables.Remove(dependency.File.FileName))
                    alreadyInstalledDependencyCount++;
            }
            var directory = Path.Combine(paths.Instance(serverId), plugin ? "plugins" : "mods");
            Directory.CreateDirectory(directory);
            var activations = new List<(string Source, string Destination)>();
            foreach (var item in installables.Values)
            {
                var destination = Path.Combine(directory, item.File.FileName);
                if (File.Exists(destination))
                {
                    if (!string.Equals(
                            await Sha512Async(destination, cancellationToken),
                            item.File.Sha512,
                            StringComparison.OrdinalIgnoreCase))
                        throw PanelProblems.Conflict(
                            "VALIDATION_FAILED",
                            $"A different {artifact} file with the same name is already installed: {item.File.FileName}.");
                    continue;
                }
                activations.Add((Path.Combine(stage, item.File.FileName), destination));
            }
            if (activations.Count == 0)
            {
                await operations.ProgressAsync(
                    jobId, 95, $"The verified {artifact} and selected dependencies are already installed",
                    cancellationToken);
                return;
            }

            await operations.ProgressAsync(
                jobId, 80,
                dependencies.Count == 0
                    ? $"Activating verified {artifact}"
                    : $"Activating verified {artifact} and selected dependencies",
                cancellationToken);
            var moved = new List<string>();
            try
            {
                foreach (var activation in activations)
                {
                    File.Move(activation.Source, activation.Destination);
                    moved.Add(activation.Destination);
                }
                server.RestartRequired |= server.State == ServerState.Running;
                server.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                if (permissions is not null) await permissions.NormalizeAsync(serverId, cancellationToken);
            }
            catch
            {
                foreach (var path in moved)
                {
                    try { File.Delete(path); }
                    catch { }
                }
                throw;
            }

            var dependencyMessage = selectedDependencies.Count == 0
                ? ""
                : $" with {dependencies.Count} selected required {(dependencies.Count == 1 ? "dependency" : "dependencies")}" +
                  (alreadyInstalledDependencyCount == 0
                      ? ""
                      : $" ({alreadyInstalledDependencyCount} already installed)");
            await console.AppendAsync(serverId, "system",
                $"Installed {version.Name} ({version.Number}) from Modrinth as a {artifact}{dependencyMessage}.",
                cancellationToken);
            var completedLabel = dependencies.Count == 0
                ? $"{char.ToUpperInvariant(artifact[0]) + artifact[1..]} installed"
                : $"{char.ToUpperInvariant(artifact[0]) + artifact[1..]} and selected dependencies installed";
            await operations.ProgressAsync(jobId, 95,
                server.RestartRequired
                    ? $"{completedLabel}; restart required"
                    : completedLabel,
                cancellationToken);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private static PanelException AlreadyInstalledConflict(
        string artifact,
        ModrinthVersion requested,
        IReadOnlyList<InstalledModrinthArtifact> installed)
    {
        var versions = string.Join(", ", installed.Select(x =>
            $"{x.VersionNumber} ({x.FileName})"));
        return PanelProblems.Conflict(
            "PROJECT_ALREADY_INSTALLED",
            $"{requested.Name} is already installed as {versions}. Remove the existing {artifact} before installing a different version.");
    }

    private static async Task<string> Sha512Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA512.HashDataAsync(stream, cancellationToken));
    }
}
