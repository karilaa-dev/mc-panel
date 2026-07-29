using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed record ModrinthFile(
    Uri Url, string FileName, long Size, string Sha1, string Sha512, bool Primary);
public sealed record ModrinthVersion(
    string Id, string ProjectId, string Name, string Number, string Type,
    DateTimeOffset PublishedAt, IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders, IReadOnlyList<ModrinthFile> Files,
    IReadOnlyList<ModrinthDependencyDto> Dependencies);
public sealed record InstalledModrinthArtifact(
    string ProjectId, string VersionId, string VersionNumber, string FileName);

public sealed class ModrinthService(
    ValidatedDownloadClient http,
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory)
{
    private static readonly HashSet<string> SupportedModLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge"
    };
    private static readonly HashSet<string> SupportedPluginLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "paper", "purpur", "spigot", "bukkit"
    };
    private static readonly HashSet<string> SupportedPackLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "minecraft"
    };

    public async Task<ModrinthSearchDto> SearchAsync(
        string projectType, string? query, int offset, int? requestedLimit, Guid? serverId,
        string? gameVersion, string? loader,
        CancellationToken cancellationToken)
    {
        projectType = projectType?.Trim().ToLowerInvariant() ?? "";
        if (projectType is not ("mod" or "modpack" or "plugin"))
            throw PanelProblems.Validation("Project type must be 'mod', 'modpack', or 'plugin'.");
        offset = Math.Max(0, offset);
        var limit = requestedLimit ?? 20;
        if (limit is < 1 or > 100) throw PanelProblems.Validation("The Modrinth page size must be between 1 and 100.");
        var facets = new List<string[]>
        {
            new[] { projectType == "plugin" ? "all_project_types:plugin" : $"project_type:{projectType}" }
        };
        if (projectType is "mod" or "plugin")
        {
            if (!serverId.HasValue) throw PanelProblems.Validation($"A server is required when searching for {projectType}s.");
            var server = await ServerAsync(serverId.Value, cancellationToken);
            var defaultLoader = projectType == "mod"
                ? Loader(server.Kind) ?? throw PanelProblems.Validation("Modrinth mod browsing is only available for Fabric, Forge, and NeoForge servers.")
                : PluginLoader(server.Kind) ?? throw PanelProblems.Validation("Modrinth plugin browsing is only available for Paper servers.");
            var selectedVersion = FilterValue(gameVersion, server.Version, "Minecraft version");
            var selectedLoader = FilterValue(loader, defaultLoader, "loader").ToLowerInvariant();
            var supported = projectType == "mod" ? SupportedModLoaders : SupportedPluginLoaders;
            if (!supported.Contains(selectedLoader))
                throw PanelProblems.Validation($"The selected Modrinth {projectType} loader is unsupported.");
            facets.Add(new[] { $"versions:{selectedVersion}" });
            facets.Add(new[] { $"categories:{selectedLoader}" });
            facets.Add(new[] { "server_side!=unsupported" });
        }
        var url = $"https://api.modrinth.com/v2/search?query={Uri.EscapeDataString(query?.Trim() ?? "")}" +
                  $"&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets))}&index=downloads&offset={offset}&limit={limit}";
        using var json = await http.JsonAsync(new Uri(url), cancellationToken, DownloadPolicy.Modrinth);
        var root = json.RootElement;
        var projects = new List<ModrinthProjectDto>();
        foreach (var hit in root.GetProperty("hits").EnumerateArray())
        {
            var categories = Strings(hit, "categories");
            if (projectType == "modpack" && categories.Count > 0 &&
                !categories.Any(x => SupportedPackLoaders.Contains(x))) continue;
            projects.Add(new(
                String(hit, "project_id"), String(hit, "slug"), String(hit, "title"),
                String(hit, "description"), projectType,
                String(hit, "author"), NullableString(hit, "icon_url"),
                hit.TryGetProperty("downloads", out var downloads) ? downloads.GetInt64() : 0,
                Strings(hit, "versions"), categories, NullableString(hit, "featured_gallery"),
                hit.TryGetProperty("follows", out var follows) ? follows.GetInt64() : 0,
                NullableDateTimeOffset(hit, "date_modified")));
        }
        return new(projects, root.GetProperty("offset").GetInt32(),
            root.GetProperty("limit").GetInt32(), root.GetProperty("total_hits").GetInt32());
    }

    public async Task<IReadOnlyList<ModrinthVersionDto>> VersionsAsync(
        string projectId, Guid? serverId, string? projectType, string? gameVersion,
        string? loader, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw PanelProblems.Validation("A Modrinth project is required.");
        string suffix;
        ServerEntity? server = null;
        if (serverId.HasValue)
        {
            server = await ServerAsync(serverId.Value, cancellationToken);
            projectType = string.IsNullOrWhiteSpace(projectType) ? "mod" : projectType.Trim().ToLowerInvariant();
            if (projectType is not ("mod" or "plugin"))
                throw PanelProblems.Validation("Server-filtered versions must be for mods or plugins.");
            var defaultLoader = projectType == "mod"
                ? Loader(server.Kind) ?? throw PanelProblems.Validation("This server does not support mods.")
                : PluginLoader(server.Kind) ?? throw PanelProblems.Validation("This server does not support plugins.");
            var selectedVersion = FilterValue(gameVersion, server.Version, "Minecraft version");
            var selectedLoader = FilterValue(loader, defaultLoader, "loader").ToLowerInvariant();
            var supported = projectType == "mod" ? SupportedModLoaders : SupportedPluginLoaders;
            if (!supported.Contains(selectedLoader))
                throw PanelProblems.Validation($"The selected Modrinth {projectType} loader is unsupported.");
            suffix = $"?game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { selectedVersion }))}" +
                     $"&loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { selectedLoader }))}&include_changelog=false";
        }
        else suffix = "?include_changelog=false";
        var uri = new Uri($"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId.Trim())}/version{suffix}");
        using var json = await http.JsonAsync(uri, cancellationToken, DownloadPolicy.Modrinth);
        var versions = json.RootElement.EnumerateArray().Select(ParseVersion)
            .Where(x => server is not null || x.Loaders.Count == 0 || x.Loaders.Any(y => SupportedPackLoaders.Contains(y)))
            .OrderByDescending(x => x.PublishedAt)
            .ToList();
        var enriched = await EnrichRequiredDependenciesAsync(versions, cancellationToken);
        if (server is not null)
        {
            var installed = await InstalledArtifactsAsync(
                server.Id, projectType == "plugin", cancellationToken);
            enriched = AttachInstalledDependencies(enriched, installed);
        }
        return enriched.Select(ToDto).ToList();
    }

    public async Task<ModrinthVersion> VersionAsync(string versionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(versionId)) throw PanelProblems.Validation("A Modrinth version is required.");
        using var json = await http.JsonAsync(
            new Uri($"https://api.modrinth.com/v2/version/{Uri.EscapeDataString(versionId.Trim())}"),
            cancellationToken, DownloadPolicy.Modrinth);
        return ParseVersion(json.RootElement);
    }

    public async Task<(ServerEntity Server, ModrinthVersion Version, ModrinthFile File)> ResolveModAsync(
        Guid serverId, string projectId, string versionId, CancellationToken cancellationToken)
    {
        var server = await ServerAsync(serverId, cancellationToken);
        var loader = Loader(server.Kind) ?? throw PanelProblems.Validation("This server does not support Modrinth mods.");
        var version = await VersionAsync(versionId, cancellationToken);
        if (!version.ProjectId.Equals(projectId, StringComparison.Ordinal) ||
            !version.GameVersions.Contains(server.Version, StringComparer.Ordinal) ||
            !version.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase))
            throw PanelProblems.Validation("The selected Modrinth version is not compatible with this server.");
        var file = version.Files.FirstOrDefault(x => x.Primary && x.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                   ?? version.Files.FirstOrDefault(x => x.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                   ?? throw PanelProblems.Validation("The selected Modrinth version does not contain an installable JAR.");
        return (server, version, file);
    }

    public async Task<(ServerEntity Server, ModrinthVersion Version, ModrinthFile File)> ResolvePluginAsync(
        Guid serverId, string projectId, string versionId, CancellationToken cancellationToken)
    {
        var server = await ServerAsync(serverId, cancellationToken);
        if (PluginLoader(server.Kind) is null)
            throw PanelProblems.Validation("This server does not support Modrinth plugins.");
        var version = await VersionAsync(versionId, cancellationToken);
        if (!version.ProjectId.Equals(projectId, StringComparison.Ordinal) ||
            !version.GameVersions.Contains(server.Version, StringComparer.Ordinal) ||
            !version.Loaders.Any(SupportedPluginLoaders.Contains))
            throw PanelProblems.Validation("The selected Modrinth version is not compatible with this Paper server.");
        var file = InstallableJar(version);
        return (server, version, file);
    }

    public async Task<IReadOnlyList<(ModrinthVersion Version, ModrinthFile File)>> ResolveDependenciesAsync(
        Guid serverId,
        ModrinthVersion parent,
        IReadOnlyCollection<string>? selectedProjectIds,
        bool plugin,
        CancellationToken cancellationToken)
    {
        var selected = (selectedProjectIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selected.Length == 0) return [];
        if (selected.Length > 100 || selected.Any(x => x.Length > 64 || x.Any(char.IsControl)))
            throw PanelProblems.Validation("The selected Modrinth dependencies are invalid.");

        var enriched = (await EnrichRequiredDependenciesAsync([parent], cancellationToken)).Single();
        var required = enriched.Dependencies
            .Where(x => x.Type.Equals("required", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(x.ProjectId))
            .GroupBy(x => x.ProjectId!, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var unknown = selected.Where(x => !required.ContainsKey(x)).ToArray();
        if (unknown.Length > 0)
            throw PanelProblems.Validation("A selected project is not a required dependency of this Modrinth version.");

        var resolved = new List<(ModrinthVersion Version, ModrinthFile File)>();
        foreach (var projectId in selected)
        {
            var dependency = required[projectId];
            var versionId = dependency.VersionId;
            if (string.IsNullOrWhiteSpace(versionId))
            {
                var compatible = await VersionsAsync(
                    projectId, serverId, plugin ? "plugin" : "mod", null, null, cancellationToken);
                versionId = compatible.FirstOrDefault()?.Id ??
                            throw PanelProblems.Validation(
                                $"{dependency.ProjectTitle ?? projectId} has no compatible version for this server.");
            }
            var item = plugin
                ? await ResolvePluginAsync(serverId, projectId, versionId, cancellationToken)
                : await ResolveModAsync(serverId, projectId, versionId, cancellationToken);
            resolved.Add((item.Version, item.File));
        }
        return resolved;
    }

    public async Task<IReadOnlyList<InstalledModrinthArtifact>> InstalledArtifactsAsync(
        Guid serverId, bool plugin, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(paths.Instance(serverId), plugin ? "plugins" : "mods");
        if (!Directory.Exists(directory)) return [];
        var files = Directory.EnumerateFiles(directory, "*.jar", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .Where(IsRegularFile)
            .ToList();
        if (files.Count == 0) return [];

        var hashes = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken
            },
            async (file, token) =>
            {
                var hash = await Sha512Async(file, token);
                hashes.TryAdd(hash, file.Name);
            });

        var result = new List<InstalledModrinthArtifact>();
        foreach (var chunk in hashes.Keys.Chunk(100))
        {
            using var json = await http.JsonPostAsync(
                new Uri("https://api.modrinth.com/v2/version_files"),
                new { hashes = chunk, algorithm = "sha512" },
                cancellationToken,
                DownloadPolicy.Modrinth);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new PanelException(
                    502, "UPSTREAM_UNAVAILABLE",
                    "Modrinth returned unexpected installed-file metadata.");
            foreach (var item in json.RootElement.EnumerateObject())
            {
                if (!hashes.TryGetValue(item.Name, out var fileName)) continue;
                var version = ParseVersion(item.Value);
                result.Add(new(
                    version.ProjectId, version.Id, version.Number, fileName));
            }
        }
        return result
            .DistinctBy(x => new { x.ProjectId, x.VersionId, x.FileName })
            .ToList();
    }

    private async Task<ServerEntity> ServerAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        return await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
               ?? throw PanelProblems.NotFound("Server");
    }

    internal static string? Loader(ServerKind kind) => kind switch
    {
        ServerKind.Fabric => "fabric",
        ServerKind.Forge => "forge",
        ServerKind.NeoForge => "neoforge",
        _ => null
    };

    internal static string? PluginLoader(ServerKind kind) => kind == ServerKind.Paper ? "paper" : null;

    private static ModrinthVersion ParseVersion(JsonElement value)
    {
        var files = new List<ModrinthFile>();
        foreach (var file in value.GetProperty("files").EnumerateArray())
        {
            var hashes = file.GetProperty("hashes");
            files.Add(new(
                new Uri(String(file, "url")), Path.GetFileName(String(file, "filename")),
                file.GetProperty("size").GetInt64(),
                String(hashes, "sha1"), String(hashes, "sha512"),
                file.TryGetProperty("primary", out var primary) && primary.GetBoolean()));
        }
        var dependencies = new List<ModrinthDependencyDto>();
        if (value.TryGetProperty("dependencies", out var dependencyArray))
        foreach (var dependency in dependencyArray.EnumerateArray())
            dependencies.Add(new(
                String(dependency, "dependency_type"),
                NullableString(dependency, "project_id"),
                NullableString(dependency, "version_id"),
                NullableString(dependency, "file_name"),
                null,
                null,
                []));
        return new(
            String(value, "id"), String(value, "project_id"), String(value, "name"),
            String(value, "version_number"), String(value, "version_type"),
            value.GetProperty("date_published").GetDateTimeOffset(),
            Strings(value, "game_versions"), Strings(value, "loaders"), files, dependencies);
    }

    private static ModrinthVersionDto ToDto(ModrinthVersion version)
    {
        var file = version.Files.FirstOrDefault(x => x.Primary) ?? version.Files.FirstOrDefault();
        return new(
            version.Id, version.ProjectId, version.Name, version.Number, version.Type,
            version.PublishedAt, version.GameVersions, version.Loaders,
            file?.FileName ?? "", file?.Size ?? 0, version.Dependencies);
    }

    private async Task<IReadOnlyList<ModrinthVersion>> EnrichRequiredDependenciesAsync(
        IReadOnlyList<ModrinthVersion> versions,
        CancellationToken cancellationToken)
    {
        var required = versions.SelectMany(x => x.Dependencies)
            .Where(x => x.Type.Equals("required", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (required.Count == 0) return versions;

        var projectIdByVersion = new Dictionary<string, string>(StringComparer.Ordinal);
        var unresolvedVersionIds = required
            .Where(x => string.IsNullOrWhiteSpace(x.ProjectId) && !string.IsNullOrWhiteSpace(x.VersionId))
            .Select(x => x.VersionId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        foreach (var ids in unresolvedVersionIds.Chunk(100))
        {
            using var versionJson = await http.JsonAsync(
                BatchUri("versions", ids), cancellationToken, DownloadPolicy.Modrinth);
            foreach (var version in versionJson.RootElement.EnumerateArray())
            {
                var id = String(version, "id");
                var dependencyProjectId = String(version, "project_id");
                if (id.Length > 0 && dependencyProjectId.Length > 0)
                    projectIdByVersion[id] = dependencyProjectId;
            }
        }

        var projectIds = required
            .Select(x => x.ProjectId ??
                         (x.VersionId is not null && projectIdByVersion.TryGetValue(x.VersionId, out var resolved)
                             ? resolved
                             : null))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var projectTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ids in projectIds.Chunk(100))
        {
            using var projectJson = await http.JsonAsync(
                BatchUri("projects", ids), cancellationToken, DownloadPolicy.Modrinth);
            foreach (var project in projectJson.RootElement.EnumerateArray())
            {
                var id = String(project, "id");
                var title = String(project, "title");
                if (id.Length > 0 && title.Length > 0) projectTitles[id] = title;
            }
        }

        return versions.Select(version => version with
        {
            Dependencies = version.Dependencies.Select(dependency =>
            {
                var dependencyProjectId = dependency.ProjectId ??
                    (dependency.VersionId is not null &&
                     projectIdByVersion.TryGetValue(dependency.VersionId, out var resolved)
                        ? resolved
                        : null);
                projectTitles.TryGetValue(dependencyProjectId ?? "", out var title);
                return dependency with
                {
                    ProjectId = dependencyProjectId,
                    ProjectTitle = title,
                    ProjectUrl = dependencyProjectId is null
                        ? null
                        : $"https://modrinth.com/project/{Uri.EscapeDataString(dependencyProjectId)}"
                };
            }).ToList()
        }).ToList();
    }

    private static List<ModrinthVersion> AttachInstalledDependencies(
        IReadOnlyList<ModrinthVersion> versions,
        IReadOnlyList<InstalledModrinthArtifact> installed)
    {
        var byProject = installed
            .GroupBy(x => x.ProjectId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<InstalledModrinthVersionDto>)group
                    .Select(x => new InstalledModrinthVersionDto(
                        x.VersionId, x.VersionNumber, x.FileName))
                    .ToList(),
                StringComparer.Ordinal);
        return versions.Select(version => version with
        {
            Dependencies = version.Dependencies.Select(dependency => dependency with
            {
                InstalledVersions = dependency.ProjectId is not null &&
                                    byProject.TryGetValue(dependency.ProjectId, out var matches)
                    ? matches
                    : []
            }).ToList()
        }).ToList();
    }

    private static bool IsRegularFile(FileInfo file)
    {
        try
        {
            return (file.Attributes & FileAttributes.ReparsePoint) == 0 &&
                   file.LinkTarget is null &&
                   file.Length is >= 0 and <= 1_073_741_824;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string> Sha512Async(FileInfo file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(
            await SHA512.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static Uri BatchUri(string resource, IReadOnlyCollection<string> ids) =>
        new($"https://api.modrinth.com/v2/{resource}?ids=" +
            Uri.EscapeDataString(JsonSerializer.Serialize(ids)));

    private static string String(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() ?? "" : "";

    private static string? NullableString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            ? item.GetString() : null;

    private static DateTimeOffset? NullableDateTimeOffset(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String &&
        item.TryGetDateTimeOffset(out var result) ? result : null;

    private static IReadOnlyList<string> Strings(JsonElement value, string property) =>
        value.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : [];

    private static string FilterValue(string? requested, string fallback, string label)
    {
        var value = string.IsNullOrWhiteSpace(requested) ? fallback : requested.Trim();
        if (value.Length > 64 || value.Any(char.IsControl))
            throw PanelProblems.Validation($"The selected {label} is invalid.");
        return value;
    }

    private static ModrinthFile InstallableJar(ModrinthVersion version) =>
        version.Files.FirstOrDefault(x => x.Primary && x.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        ?? version.Files.FirstOrDefault(x => x.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        ?? throw PanelProblems.Validation("The selected Modrinth version does not contain an installable JAR.");

}
