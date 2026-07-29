using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public enum DownloadPolicy { Distribution, Modrinth, Mrpack }
public sealed record DownloadArtifact(
    Uri Url, string HashAlgorithm, string Hash, long? Size, string FileName,
    DownloadPolicy Policy = DownloadPolicy.Distribution);
public sealed record InstallPlan(
    ServerKind Kind, string Version, int RequiredJavaMajor, DownloadArtifact Artifact,
    string? Build = null, string? LoaderVersion = null, string? InstallerVersion = null, bool Experimental = false);

public sealed class ValidatedDownloadClient(IHttpClientFactory clients)
{
    private const long MaximumArtifactBytes = 1_073_741_824;
    private static readonly HashSet<string> DistributionHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "piston-meta.mojang.com", "piston-data.mojang.com", "launcher.mojang.com",
        "fill.papermc.io", "fill-data.papermc.io", "meta.fabricmc.net", "maven.fabricmc.net",
        "files.minecraftforge.net", "maven.minecraftforge.net", "maven.neoforged.net"
    };
    private static readonly HashSet<string> ModrinthHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.modrinth.com", "cdn.modrinth.com"
    };
    private static readonly HashSet<string> MrpackHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "cdn.modrinth.com", "github.com", "raw.githubusercontent.com", "gitlab.com",
        "objects.githubusercontent.com", "release-assets.githubusercontent.com"
    };

    public async Task<JsonDocument> JsonAsync(
        Uri uri, CancellationToken cancellationToken,
        DownloadPolicy policy = DownloadPolicy.Distribution)
    {
        using var response = await SendAsync(uri, HttpCompletionOption.ResponseContentRead, policy, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async Task<string> StringAsync(
        Uri uri, CancellationToken cancellationToken,
        DownloadPolicy policy = DownloadPolicy.Distribution)
    {
        using var response = await SendAsync(uri, HttpCompletionOption.ResponseContentRead, policy, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<JsonDocument> JsonPostAsync<T>(
        Uri uri, T body, CancellationToken cancellationToken,
        DownloadPolicy policy = DownloadPolicy.Distribution)
    {
        Validate(uri, policy);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(body)
        };
        using var response = await clients.CreateClient("upstream").SendAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new PanelException(
                502, "INSTALL_DOWNLOAD_REJECTED",
                "The upstream service unexpectedly redirected a metadata request.");
        if (!response.IsSuccessStatusCode)
            throw new PanelException(
                502, "UPSTREAM_UNAVAILABLE",
                "The upstream distribution service is unavailable.",
                $"Upstream returned {(int)response.StatusCode} {response.StatusCode}.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async Task DownloadAsync(DownloadArtifact artifact, string destination, CancellationToken cancellationToken)
    {
        var (algorithm, expectedHash) = ValidateChecksumMetadata(artifact);
        if (artifact.Size is < 0)
            throw new PanelException(502, "INSTALL_CHECKSUM_FAILED", "The upstream artifact size metadata is invalid.");
        if (artifact.Size is > MaximumArtifactBytes)
            throw new PanelException(502, "INSTALL_DOWNLOAD_REJECTED", "The upstream artifact is unexpectedly large.");

        var createdDestination = false;
        try
        {
            using var response = await SendAsync(artifact.Url, HttpCompletionOption.ResponseHeadersRead, artifact.Policy, cancellationToken);
            if (artifact.Size.HasValue && response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != artifact.Size)
                throw new PanelException(502, "INSTALL_CHECKSUM_FAILED", "The upstream artifact size did not match its metadata.");
            if (response.Content.Headers.ContentLength is > MaximumArtifactBytes)
                throw new PanelException(502, "INSTALL_DOWNLOAD_REJECTED", "The upstream artifact is unexpectedly large.");

            using var hash = IncrementalHash.CreateHash(algorithm);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            createdDestination = true;
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > MaximumArtifactBytes) throw new PanelException(502, "INSTALL_DOWNLOAD_REJECTED", "The upstream artifact is unexpectedly large.");
                hash.AppendData(buffer.AsSpan(0, read));
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await target.FlushAsync(cancellationToken);
            var actualHash = hash.GetHashAndReset();
            if (artifact.Size.HasValue && total != artifact.Size.Value || !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                throw new PanelException(502, "INSTALL_CHECKSUM_FAILED", "The downloaded artifact failed verification.");
        }
        catch
        {
            if (createdDestination)
            {
                try { File.Delete(destination); }
                catch { }
            }
            throw;
        }
    }

    private static (HashAlgorithmName Algorithm, byte[] ExpectedHash) ValidateChecksumMetadata(DownloadArtifact artifact)
    {
        var (algorithm, byteLength) = artifact.HashAlgorithm?.Trim().ToLowerInvariant() switch
        {
            "sha1" => (HashAlgorithmName.SHA1, 20),
            "sha256" => (HashAlgorithmName.SHA256, 32),
            "sha512" => (HashAlgorithmName.SHA512, 64),
            _ => throw new PanelException(502, "INSTALL_CHECKSUM_FAILED", "The upstream artifact checksum algorithm is unsupported.")
        };
        if (artifact.Hash is null || artifact.Hash.Length != byteLength * 2)
            throw new PanelException(502, "INSTALL_CHECKSUM_FAILED", "The upstream artifact checksum metadata is invalid.");
        try
        {
            var expectedHash = Convert.FromHexString(artifact.Hash);
            if (expectedHash.Length != byteLength)
                throw new PanelException(502, "INSTALL_CHECKSUM_FAILED", "The upstream artifact checksum metadata is invalid.");
            return (algorithm, expectedHash);
        }
        catch (FormatException)
        {
            throw new PanelException(502, "INSTALL_CHECKSUM_FAILED", "The upstream artifact checksum metadata is invalid.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        Uri initial, HttpCompletionOption completion, DownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        var uri = initial;
        for (var redirect = 0; redirect < 4; redirect++)
        {
            Validate(uri, policy);
            var response = await clients.CreateClient("upstream").GetAsync(uri, completion, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
            {
                var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                response.Dispose();
                uri = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode;
                response.Dispose();
                throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "The upstream distribution service is unavailable.", $"Upstream returned {(int)status} {status}.");
            }
            return response;
        }
        throw new PanelException(502, "INSTALL_DOWNLOAD_REJECTED", "The upstream download redirected too many times.");
    }

    public static void Validate(Uri uri, DownloadPolicy policy = DownloadPolicy.Distribution)
    {
        var hosts = policy switch
        {
            DownloadPolicy.Modrinth => ModrinthHosts,
            DownloadPolicy.Mrpack => MrpackHosts,
            _ => DistributionHosts
        };
        if (uri.Scheme != Uri.UriSchemeHttps || !hosts.Contains(uri.IdnHost) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            throw new PanelException(502, "INSTALL_DOWNLOAD_REJECTED", "The upstream download URL is not allowed.");
    }
}

public sealed class DistributionCatalogService(ValidatedDownloadClient http, ILogger<DistributionCatalogService> logger)
{
    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private (CatalogDto Value, DateTimeOffset Expires)? _cache;
    private CatalogDto? _stableCache;

    public async Task<CatalogDto> GetCatalogAsync(bool experimental, CancellationToken cancellationToken)
    {
        if (_cache is { } cached && cached.Expires > DateTimeOffset.UtcNow)
            return experimental ? cached.Value : _stableCache ?? FilterStable(cached.Value);
        await _catalogLock.WaitAsync(cancellationToken);
        try
        {
            if (_cache is { } second && second.Expires > DateTimeOffset.UtcNow) return experimental ? second.Value : _stableCache ?? FilterStable(second.Value);
            var mojangTask = GetMojangManifestAsync(cancellationToken);
            var paperTask = GetPaperVersionsAsync(cancellationToken);
            var fabricGamesTask = GetFabricVersionsAsync("game", cancellationToken);
            var loadersTask = GetFabricChoicesAsync("loader", cancellationToken);
            var installersTask = GetFabricChoicesAsync("installer", cancellationToken);
            var forgeBuildsTask = GetForgeBuildsAsync(cancellationToken);
            var neoForgeBuildsTask = GetNeoForgeBuildsAsync(cancellationToken);
            await Task.WhenAll(mojangTask, paperTask, fabricGamesTask, loadersTask, installersTask, forgeBuildsTask, neoForgeBuildsTask);
            var mojang = await mojangTask;
            // Mojang's Piston metadata first exposes a dedicated server artifact at 1.2.5.
            // Later manifest entries (including snapshots) are artifact-bearing; older client-only
            // entries remain resolvable metadata but are intentionally absent from the server UI.
            var artifactMojang = mojang.Where(x => IsServerCatalogCandidate(x.Id, x.Type, x.ReleaseTime)).ToList();
            var paper = await paperTask;
            var fabricSet = (await fabricGamesTask).Select(x => x.Version).ToHashSet(StringComparer.Ordinal);
            var fabric = artifactMojang.Where(x => fabricSet.Contains(x.Id)).Select(x => x.Id).ToList();
            var forgeBuilds = await forgeBuildsTask;
            var neoForgeBuilds = await neoForgeBuildsTask;
            var forge = artifactMojang.Where(x => forgeBuilds.ContainsKey(x.Id)).Select(x => x.Id).ToList();
            var neoForge = artifactMojang.Where(x => neoForgeBuilds.ContainsKey(x.Id)).Select(x => x.Id).ToList();
            var paperBuilds = new ConcurrentDictionary<string, IReadOnlyList<PaperBuildDto>>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(paper, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken }, async (version, token) =>
            {
                try { paperBuilds[version] = await GetPaperBuildsAsync(version, true, token); }
                catch (Exception exception) { logger.LogDebug(exception, "Paper builds unavailable for {Version}", version); }
            });
            var value = new CatalogDto(
                artifactMojang.Select(x => x.Id).ToList(), paper, fabric, forge, neoForge,
                paperBuilds, await loadersTask, await installersTask, forgeBuilds, neoForgeBuilds, DateTimeOffset.UtcNow);
            _cache = (value, DateTimeOffset.UtcNow.AddMinutes(15));
            var fabricGames = (await fabricGamesTask).ToDictionary(x => x.Version, x => x.Stable, StringComparer.Ordinal);
            var releaseVersions = artifactMojang.Where(x => x.Type == "release").Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
            _stableCache = FilterStable(value) with
            {
                Vanilla = value.Vanilla.Where(releaseVersions.Contains).ToList(),
                Paper = value.Paper.Where(version => paperBuilds.TryGetValue(version, out var builds) && builds.Any(build => !build.Experimental)).ToList(),
                Fabric = value.Fabric.Where(version => releaseVersions.Contains(version) && fabricGames.GetValueOrDefault(version)).ToList(),
                Forge = value.Forge.Where(version => releaseVersions.Contains(version) && forgeBuilds.GetValueOrDefault(version)?.Any(build => !build.Experimental) == true).ToList(),
                NeoForge = value.NeoForge.Where(version => releaseVersions.Contains(version) && neoForgeBuilds.GetValueOrDefault(version)?.Any(build => !build.Experimental) == true).ToList()
            };
            return experimental ? value : _stableCache!;
        }
        finally { _catalogLock.Release(); }
    }

    public Task<IReadOnlyList<PaperBuildDto>> PaperBuildsAsync(string version, bool experimental, CancellationToken cancellationToken) =>
        GetPaperBuildsAsync(version, experimental, cancellationToken);

    public async Task<InstallPlan> ResolveAsync(ServerKind kind, string version, string? build, string? loader, string? installer, bool includeExperimental, CancellationToken cancellationToken)
    {
        return kind switch
        {
            ServerKind.Vanilla => await ResolveVanillaAsync(version, includeExperimental, cancellationToken),
            ServerKind.Paper => await ResolvePaperAsync(version, build, includeExperimental, cancellationToken),
            ServerKind.Fabric => await ResolveFabricAsync(version, loader, installer, includeExperimental, cancellationToken),
            ServerKind.Forge => await ResolveForgeAsync(version, loader, includeExperimental, cancellationToken),
            ServerKind.NeoForge => await ResolveNeoForgeAsync(version, loader, includeExperimental, cancellationToken),
            _ => throw PanelProblems.Validation("Unsupported server distribution.")
        };
    }

    private async Task<InstallPlan> ResolveVanillaAsync(string version, bool includeExperimental, CancellationToken cancellationToken)
    {
        var metadata = await GetMojangVersionAsync(version, cancellationToken);
        var experimental = !metadata.Type.Equals("release", StringComparison.OrdinalIgnoreCase);
        if (experimental && !includeExperimental) throw PanelProblems.Validation("Snapshots and other experimental Vanilla versions require explicit confirmation.");
        return new InstallPlan(ServerKind.Vanilla, version, metadata.RequiredJava,
            new DownloadArtifact(metadata.Url, "sha1", metadata.Sha1, metadata.Size, "server.jar"), Experimental: experimental);
    }

    private async Task<InstallPlan> ResolvePaperAsync(string version, string? requestedBuild, bool includeExperimental, CancellationToken cancellationToken)
    {
        using var document = await http.JsonAsync(new Uri($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}/builds"), cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "Paper returned unexpected metadata.");
        JsonElement? selected = null;
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var id = ReadString(item, "id");
            var channel = ReadString(item, "channel");
            var stable = channel.Equals("STABLE", StringComparison.OrdinalIgnoreCase);
            if (!includeExperimental && !stable) continue;
            if (requestedBuild is null || id == requestedBuild) { selected = item.Clone(); break; }
        }
        if (selected is null) throw PanelProblems.Validation("The selected Paper build does not exist or is experimental.");
        var chosen = selected.Value;
        var download = chosen.GetProperty("downloads").GetProperty("server:default");
        var url = new Uri(download.GetProperty("url").GetString()!);
        var hash = download.GetProperty("checksums").GetProperty("sha256").GetString()!;
        var size = download.TryGetProperty("size", out var sizeElement) ? sizeElement.GetInt64() : (long?)null;
        var idValue = ReadString(chosen, "id");
        var channelValue = ReadString(chosen, "channel");
        var mojang = await GetMojangVersionAsync(version, cancellationToken);
        return new InstallPlan(ServerKind.Paper, version, Math.Max(mojang.RequiredJava, InferPaperJava(version)),
            new DownloadArtifact(url, "sha256", hash, size, download.TryGetProperty("name", out var n) ? n.GetString() ?? "server.jar" : "server.jar"),
            idValue, Experimental: !channelValue.Equals("STABLE", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<InstallPlan> ResolveFabricAsync(string version, string? requestedLoader, string? requestedInstaller, bool includeExperimental, CancellationToken cancellationToken)
    {
        var games = await GetFabricChoicesAsync("game", cancellationToken);
        var game = games.FirstOrDefault(x => x.Version == version);
        if (game is null) throw PanelProblems.Validation("Fabric does not publish metadata for the selected Minecraft version.");
        if (!game.Stable && !includeExperimental) throw PanelProblems.Validation("Experimental Fabric game versions require explicit confirmation.");
        var loaders = await GetFabricChoicesAsync("loader", cancellationToken);
        var installers = await GetFabricChoicesAsync("installer", cancellationToken);
        var loader = SelectChoice(loaders, requestedLoader, includeExperimental, "Fabric loader");
        var installer = SelectChoice(installers, requestedInstaller, includeExperimental, "Fabric installer");
        using var document = await http.JsonAsync(new Uri("https://meta.fabricmc.net/v2/versions/installer"), cancellationToken);
        var item = document.RootElement.EnumerateArray().FirstOrDefault(x => x.GetProperty("version").GetString() == installer.Version);
        var urlText = item.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(urlText) && item.TryGetProperty("maven", out var maven))
        {
            var parts = maven.GetString()!.Split(':');
            urlText = $"https://maven.fabricmc.net/{parts[0].Replace('.', '/')}/{parts[1]}/{parts[2]}/{parts[1]}-{parts[2]}.jar";
        }
        if (string.IsNullOrWhiteSpace(urlText)) throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "Fabric installer metadata is incomplete.");
        var uri = new Uri(urlText);
        var sha1 = (await http.StringAsync(new Uri(urlText + ".sha1"), cancellationToken)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (sha1.Length != 40) throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "Fabric installer checksum metadata is invalid.");
        var mojang = await GetMojangVersionAsync(version, cancellationToken);
        return new InstallPlan(ServerKind.Fabric, version, mojang.RequiredJava,
            new DownloadArtifact(uri, "sha1", sha1, null, $"fabric-installer-{installer.Version}.jar"),
            LoaderVersion: loader.Version, InstallerVersion: installer.Version, Experimental: !game.Stable || !loader.Stable || !installer.Stable);
    }

    private async Task<InstallPlan> ResolveForgeAsync(string version, string? requestedLoader, bool includeExperimental, CancellationToken cancellationToken)
    {
        var builds = await GetForgeBuildsAsync(cancellationToken);
        var build = SelectLoaderBuild(builds, version, requestedLoader, includeExperimental, "Forge");
        var uri = ForgeInstallerUri(version, build.Version);
        var sha1 = await ReadSha1Async(uri.ToString(), cancellationToken);
        var mojang = await GetMojangVersionAsync(version, cancellationToken);
        return new InstallPlan(ServerKind.Forge, version, mojang.RequiredJava,
            new DownloadArtifact(uri, "sha1", sha1, null, Path.GetFileName(uri.LocalPath)),
            LoaderVersion: build.Version, Experimental: build.Experimental);
    }

    private async Task<InstallPlan> ResolveNeoForgeAsync(string version, string? requestedLoader, bool includeExperimental, CancellationToken cancellationToken)
    {
        var builds = await GetNeoForgeBuildsAsync(cancellationToken);
        var build = SelectLoaderBuild(builds, version, requestedLoader, includeExperimental, "NeoForge");
        var uri = NeoForgeInstallerUri(build.Version);
        var sha1 = await ReadSha1Async(uri.ToString(), cancellationToken);
        var mojang = await GetMojangVersionAsync(version, cancellationToken);
        return new InstallPlan(ServerKind.NeoForge, version, mojang.RequiredJava,
            new DownloadArtifact(uri, "sha1", sha1, null, Path.GetFileName(uri.LocalPath)),
            LoaderVersion: build.Version, Experimental: build.Experimental);
    }

    private async Task<string> ReadSha1Async(string url, CancellationToken cancellationToken)
    {
        var sha1 = (await http.StringAsync(Sha1Uri(new Uri(url)), cancellationToken)).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (sha1.Length != 40 || !sha1.All(Uri.IsHexDigit))
            throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "Loader checksum metadata is invalid.");
        return sha1;
    }

    private async Task<List<MojangManifestItem>> GetMojangManifestAsync(CancellationToken cancellationToken)
    {
        using var document = await http.JsonAsync(new Uri("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"), cancellationToken);
        return document.RootElement.GetProperty("versions").EnumerateArray().Select(x => new MojangManifestItem(
            x.GetProperty("id").GetString()!, x.GetProperty("type").GetString()!, new Uri(x.GetProperty("url").GetString()!),
            x.GetProperty("releaseTime").GetDateTimeOffset())).ToList();
    }

    private async Task<MojangVersionMetadata> GetMojangVersionAsync(string version, CancellationToken cancellationToken)
    {
        var item = (await GetMojangManifestAsync(cancellationToken)).FirstOrDefault(x => x.Id == version)
            ?? throw PanelProblems.Validation("The selected Minecraft version is unavailable.");
        using var document = await http.JsonAsync(item.Url, cancellationToken);
        if (!document.RootElement.TryGetProperty("downloads", out var downloads) || !downloads.TryGetProperty("server", out var server))
            throw PanelProblems.Validation("That Minecraft version has no dedicated server artifact.");
        var requiredJava = document.RootElement.TryGetProperty("javaVersion", out var java) && java.TryGetProperty("majorVersion", out var major)
            ? major.GetInt32() : InferJava(version);
        return new MojangVersionMetadata(new Uri(server.GetProperty("url").GetString()!), server.GetProperty("sha1").GetString()!, server.GetProperty("size").GetInt64(), requiredJava, item.Type);
    }

    private async Task<List<string>> GetPaperVersionsAsync(CancellationToken cancellationToken)
    {
        using var document = await http.JsonAsync(new Uri("https://fill.papermc.io/v3/projects/paper"), cancellationToken);
        var versions = new List<string>();
        foreach (var group in document.RootElement.GetProperty("versions").EnumerateObject())
            versions.AddRange(group.Value.EnumerateArray().Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)));
        return versions;
    }

    private async Task<IReadOnlyList<PaperBuildDto>> GetPaperBuildsAsync(string version, bool experimental, CancellationToken cancellationToken)
    {
        using var document = await http.JsonAsync(new Uri($"https://fill.papermc.io/v3/projects/paper/versions/{Uri.EscapeDataString(version)}/builds"), cancellationToken);
        return document.RootElement.EnumerateArray().Select(x =>
        {
            var channel = ReadString(x, "channel");
            var downloadName = x.TryGetProperty("downloads", out var downloads) && downloads.TryGetProperty("server:default", out var server) && server.TryGetProperty("name", out var name) ? name.GetString() : null;
            return new PaperBuildDto(ReadString(x, "id"), channel, !channel.Equals("STABLE", StringComparison.OrdinalIgnoreCase), downloadName);
        }).Where(x => experimental || !x.Experimental).ToList();
    }

    private async Task<List<FabricChoiceDto>> GetFabricChoicesAsync(string type, CancellationToken cancellationToken)
    {
        using var document = await http.JsonAsync(new Uri($"https://meta.fabricmc.net/v2/versions/{type}"), cancellationToken);
        return document.RootElement.EnumerateArray().Select(x => new FabricChoiceDto(
            x.GetProperty("version").GetString()!, x.TryGetProperty("stable", out var stable) && stable.GetBoolean())).ToList();
    }

    private async Task<List<FabricChoiceDto>> GetFabricVersionsAsync(string type, CancellationToken cancellationToken) =>
        await GetFabricChoicesAsync(type, cancellationToken);

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>>> GetForgeBuildsAsync(CancellationToken cancellationToken)
    {
        var metadataTask = http.StringAsync(new Uri("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"), cancellationToken);
        var promotionsTask = http.JsonAsync(new Uri("https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json"), cancellationToken);
        await Task.WhenAll(metadataTask, promotionsTask);
        XDocument metadata;
        try { metadata = XDocument.Parse(await metadataTask); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "Forge returned invalid version metadata.", exception.Message); }

        using var promotions = await promotionsTask;
        return ParseForgeBuilds(metadata, promotions.RootElement);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>> ParseForgeBuilds(string metadataXml, string promotionsJson)
    {
        var metadata = XDocument.Parse(metadataXml);
        using var promotions = JsonDocument.Parse(promotionsJson);
        return ParseForgeBuilds(metadata, promotions.RootElement);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>> ParseForgeBuilds(XDocument metadata, JsonElement promotionsRoot)
    {
        var promoted = promotionsRoot.GetProperty("promos");
        var versions = metadata.Descendants("version").Select(x => x.Value.Trim()).Where(x => x.Length > 0).Reverse();
        var grouped = new Dictionary<string, List<LoaderBuildDto>>(StringComparer.Ordinal);
        foreach (var coordinate in versions)
        {
            var separator = coordinate.IndexOf('-');
            if (separator <= 0 || separator == coordinate.Length - 1) continue;
            var minecraft = coordinate[..separator];
            if (!IsForgeInstallerVersion(minecraft)) continue;
            var loader = coordinate[(separator + 1)..];
            var recommended = promoted.TryGetProperty($"{minecraft}-recommended", out var recommendedElement) ? recommendedElement.GetString() : null;
            var latest = promoted.TryGetProperty($"{minecraft}-latest", out var latestElement) ? latestElement.GetString() : null;
            var isRecommended = loader == recommended;
            var isLatest = loader == latest;
            var experimental = !isRecommended && !(recommended is null && isLatest);
            var channel = isRecommended ? "Recommended" : isLatest ? "Latest" : "Other";
            if (!grouped.TryGetValue(minecraft, out var builds)) grouped[minecraft] = builds = [];
            if (builds.All(x => x.Version != loader)) builds.Add(new LoaderBuildDto(loader, channel, experimental));
        }
        return grouped.ToDictionary(
            x => x.Key,
            x => (IReadOnlyList<LoaderBuildDto>)x.Value.OrderBy(y => y.Experimental).ThenBy(y => y.Channel == "Recommended" ? 0 : y.Channel == "Latest" ? 1 : 2).ToList(),
            StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>>> GetNeoForgeBuildsAsync(CancellationToken cancellationToken)
    {
        var xml = await http.StringAsync(new Uri("https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"), cancellationToken);
        XDocument metadata;
        try { metadata = XDocument.Parse(xml); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { throw new PanelException(502, "UPSTREAM_UNAVAILABLE", "NeoForge returned invalid version metadata.", exception.Message); }
        return ParseNeoForgeBuilds(metadata);
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>> ParseNeoForgeBuilds(string metadataXml) =>
        ParseNeoForgeBuilds(XDocument.Parse(metadataXml));

    private static IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>> ParseNeoForgeBuilds(XDocument metadata)
    {
        var grouped = new Dictionary<string, List<LoaderBuildDto>>(StringComparer.Ordinal);
        foreach (var loader in metadata.Descendants("version").Select(x => x.Value.Trim()).Where(x => x.Length > 0).Reverse())
        {
            var minecraft = NeoForgeMinecraftVersion(loader);
            if (minecraft is null) continue;
            var experimental = loader.Contains('-', StringComparison.Ordinal);
            var channel = !experimental ? "Stable" : loader.Contains("beta", StringComparison.OrdinalIgnoreCase) ? "Beta" : "Prerelease";
            if (!grouped.TryGetValue(minecraft, out var builds)) grouped[minecraft] = builds = [];
            if (builds.All(x => x.Version != loader)) builds.Add(new LoaderBuildDto(loader, channel, experimental));
        }
        return grouped.ToDictionary(x => x.Key, x => (IReadOnlyList<LoaderBuildDto>)x.Value.OrderBy(y => y.Experimental).ToList(), StringComparer.Ordinal);
    }

    internal static Uri ForgeInstallerUri(string minecraftVersion, string loaderVersion)
    {
        var coordinate = $"{minecraftVersion}-{loaderVersion}";
        return new Uri($"https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinate}/forge-{coordinate}-installer.jar");
    }

    internal static Uri NeoForgeInstallerUri(string loaderVersion) =>
        new($"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loaderVersion}/neoforge-{loaderVersion}-installer.jar");

    internal static Uri Sha1Uri(Uri artifact) => new(artifact.ToString() + ".sha1");

    private static LoaderBuildDto SelectLoaderBuild(
        IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>> builds,
        string minecraftVersion,
        string? requested,
        bool includeExperimental,
        string loaderName)
    {
        if (!builds.TryGetValue(minecraftVersion, out var available))
            throw PanelProblems.Validation($"{loaderName} does not publish an installer for the selected Minecraft version.");
        var selected = requested is null
            ? available.FirstOrDefault(x => !x.Experimental) ?? (includeExperimental ? available.FirstOrDefault() : null)
            : available.FirstOrDefault(x => x.Version == requested);
        if (selected is null || selected.Experimental && !includeExperimental)
            throw PanelProblems.Validation($"The selected {loaderName} version does not exist or is experimental.");
        return selected;
    }

    public static string? NeoForgeMinecraftVersion(string loaderVersion)
    {
        var numeric = loaderVersion.Split('-', 2)[0].Split('.');
        if (numeric.Length < 3 || !int.TryParse(numeric[0], out var major) || !int.TryParse(numeric[1], out var minor)) return null;
        if (major is 20 or 21) return minor == 0 ? $"1.{major}" : $"1.{major}.{minor}";
        if (major < 26 || !int.TryParse(numeric[2], out var patch)) return null;
        return patch == 0 ? $"{major}.{minor}" : $"{major}.{minor}.{patch}";
    }

    private static bool IsForgeInstallerVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) return false;
        if (major >= 26) return true;
        if (major != 1) return false;
        var patch = parts.Length > 2 && int.TryParse(parts[2], out var parsed) ? parsed : 0;
        return minor > 5 || minor == 5 && patch >= 2;
    }

    private static FabricChoiceDto SelectChoice(IReadOnlyList<FabricChoiceDto> choices, string? requested, bool experimental, string label)
    {
        var selected = requested is null ? choices.FirstOrDefault(x => x.Stable) : choices.FirstOrDefault(x => x.Version == requested);
        if (selected is null || !experimental && !selected.Stable) throw PanelProblems.Validation($"The selected {label} does not exist or is unstable.");
        return selected;
    }

    private static CatalogDto FilterStable(CatalogDto value) => value with
    {
        PaperBuilds = value.PaperBuilds.ToDictionary(x => x.Key, x => (IReadOnlyList<PaperBuildDto>)x.Value.Where(y => !y.Experimental).ToList()),
        FabricLoaders = value.FabricLoaders.Where(x => x.Stable).ToList(), FabricInstallers = value.FabricInstallers.Where(x => x.Stable).ToList(),
        ForgeBuilds = FilterStableBuilds(value.ForgeBuilds), NeoForgeBuilds = FilterStableBuilds(value.NeoForgeBuilds)
    };

    private static IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>> FilterStableBuilds(
        IReadOnlyDictionary<string, IReadOnlyList<LoaderBuildDto>> builds) =>
        builds.ToDictionary(x => x.Key, x => (IReadOnlyList<LoaderBuildDto>)x.Value.Where(y => !y.Experimental).ToList(), StringComparer.Ordinal);

    private static string ReadString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    }

    private static int InferJava(string version)
    {
        var clean = version.Split('-', '+')[0];
        var pieces = clean.Split('.');
        if (pieces.Length < 2 || !int.TryParse(pieces[1], out var minor)) return 21;
        var patch = pieces.Length > 2 && int.TryParse(pieces[2], out var p) ? p : 0;
        return minor > 20 || minor == 20 && patch >= 5 ? 21 : minor >= 18 ? 17 : minor == 17 ? 16 : 8;
    }

    public static int InferPaperJava(string version)
    {
        var clean = version.Split('-', '+')[0];
        var pieces = clean.Split('.');
        if (pieces.Length > 0 && int.TryParse(pieces[0], out var calendar) && calendar >= 26) return 25;
        if (pieces.Length < 2 || !int.TryParse(pieces[1], out var minor)) return 21;
        var patch = pieces.Length > 2 && int.TryParse(pieces[2], out var parsedPatch) ? parsedPatch : 0;
        if (minor is >= 20 and <= 21) return 21;
        if (minor is >= 17 and <= 19) return 17;
        if (minor == 16 && patch >= 5) return 16;
        if (minor is >= 12 and <= 16) return 11;
        return 8;
    }

    public static bool IsServerCatalogCandidate(string id, string type, DateTimeOffset releaseTime) =>
        !string.IsNullOrWhiteSpace(id) && (type.Equals("release", StringComparison.OrdinalIgnoreCase) || type.Equals("snapshot", StringComparison.OrdinalIgnoreCase)) &&
        releaseTime >= new DateTimeOffset(2012, 3, 29, 22, 0, 0, TimeSpan.Zero);

    private sealed record MojangManifestItem(string Id, string Type, Uri Url, DateTimeOffset ReleaseTime);
    private sealed record MojangVersionMetadata(Uri Url, string Sha1, long Size, int RequiredJava, string Type);
}
