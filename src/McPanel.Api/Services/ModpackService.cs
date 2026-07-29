using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed class ModpackService(
    PanelPaths paths,
    SafePathResolver resolver,
    IOptions<PanelOptions> options,
    ValidatedDownloadClient downloads,
    ModrinthService modrinth,
    IDbContextFactory<StateDbContext> stateFactory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private static readonly TimeSpan ImportLifetime = TimeSpan.FromHours(1);

    public async Task<ModpackInspectionDto> PrepareRemoteAsync(
        PrepareModrinthPackRequest request, CancellationToken cancellationToken)
    {
        var version = await modrinth.VersionAsync(request?.VersionId ?? "", cancellationToken);
        var file = version.Files.FirstOrDefault(x => x.Primary && x.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                   ?? version.Files.FirstOrDefault(x => x.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                   ?? throw PanelProblems.Validation("The selected Modrinth version does not contain a .mrpack file.");
        var origin = new PackOrigin("Modrinth", version.ProjectId, version.Id, file.FileName);
        return await CreateImportAsync(origin, async destination =>
        {
            await downloads.DownloadAsync(new(
                file.Url, "sha512", file.Sha512, file.Size, file.FileName, DownloadPolicy.Modrinth),
                destination, cancellationToken);
        }, cancellationToken);
    }

    public async Task<ModpackInspectionDto> PrepareUploadAsync(
        IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || !file.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
            throw PanelProblems.Validation("Upload a file with the .mrpack extension.");
        if (file.Length <= 0 || file.Length > options.Value.MaxUploadBytes)
            throw new PanelException(413, "FILE_TOO_LARGE", "The uploaded modpack exceeds the configured limit.");
        var safeName = Path.GetFileName(file.FileName);
        return await CreateImportAsync(new("Upload", null, null, safeName), async destination =>
        {
            await using var source = file.OpenReadStream();
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > options.Value.MaxUploadBytes)
                    throw new PanelException(413, "FILE_TOO_LARGE", "The uploaded modpack exceeds the configured limit.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }, cancellationToken);
    }

    public async Task<ClaimedModpack> ClaimAsync(string token, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw PanelProblems.Validation("The modpack import token is invalid.");
        var source = Path.Combine(paths.ModpackImports, token);
        if (!Directory.Exists(source)) throw new PanelException(410, "MODPACK_IMPORT_EXPIRED", "The modpack import expired or was already used.");
        var created = Directory.GetCreationTimeUtc(source);
        if (DateTime.UtcNow - created > ImportLifetime)
        {
            Directory.Delete(source, true);
            throw new PanelException(410, "MODPACK_IMPORT_EXPIRED", "The modpack import expired.");
        }
        var claimed = Path.Combine(paths.Staging, $"modpack-{token}-{Guid.NewGuid():N}");
        try { Directory.Move(source, claimed); }
        catch (DirectoryNotFoundException)
        { throw new PanelException(410, "MODPACK_IMPORT_EXPIRED", "The modpack import expired or was already used."); }
        try
        {
            var origin = JsonSerializer.Deserialize<PackOrigin>(
                await File.ReadAllTextAsync(Path.Combine(claimed, "origin.json"), cancellationToken), JsonOptions)
                ?? throw PanelProblems.Validation("The modpack import metadata is invalid.");
            var archive = Path.Combine(claimed, "source.mrpack");
            var parsed = await ParseAsync(archive, cancellationToken);
            return new(claimed, archive, origin, parsed);
        }
        catch
        {
            if (Directory.Exists(claimed)) Directory.Delete(claimed, true);
            throw;
        }
    }

    public async Task<ModpackInspectionDto> InspectAsync(string token, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw PanelProblems.Validation("The modpack import token is invalid.");
        var directory = Path.Combine(paths.ModpackImports, token);
        if (!Directory.Exists(directory) ||
            DateTime.UtcNow - Directory.GetCreationTimeUtc(directory) > ImportLifetime)
            throw new PanelException(410, "MODPACK_IMPORT_EXPIRED", "The modpack import expired or was already used.");
        var origin = JsonSerializer.Deserialize<PackOrigin>(
            await File.ReadAllTextAsync(Path.Combine(directory, "origin.json"), cancellationToken), JsonOptions)
            ?? throw PanelProblems.Validation("The modpack import metadata is invalid.");
        return Inspection(token, origin,
            await ParseAsync(Path.Combine(directory, "source.mrpack"), cancellationToken));
    }

    public async Task<InstalledPack> InstallFilesAsync(
        ClaimedModpack claim, string stage, IReadOnlyCollection<string>? selectedOptionalFiles,
        Func<int, string, Task> progress, CancellationToken cancellationToken)
    {
        var optional = claim.Parsed.Files.Where(x => x.Server == PackEnvironment.Optional)
            .Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        var selected = selectedOptionalFiles is null
            ? optional
            : selectedOptionalFiles.ToHashSet(StringComparer.Ordinal);
        if (!selected.IsSubsetOf(optional))
            throw PanelProblems.Validation("The optional modpack file selection is invalid.");
        var files = claim.Parsed.Files.Where(x =>
            x.Server == PackEnvironment.Required ||
            x.Server == PackEnvironment.Optional && selected.Contains(x.Path)).ToList();
        long totalSize = 0;
        foreach (var file in files) AddToInstallSize(ref totalSize, file.Size);
        using (var sizingArchive = ZipFile.OpenRead(claim.ArchivePath))
        foreach (var entry in sizingArchive.Entries.Where(x =>
                     x.FullName.StartsWith("overrides/", StringComparison.Ordinal) ||
                     x.FullName.StartsWith("server-overrides/", StringComparison.Ordinal)))
            AddToInstallSize(ref totalSize, entry.Length);

        var installed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var completed = 0;
        await Parallel.ForEachAsync(files, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (file, token) =>
        {
            var download = file.Downloads.Select(x =>
            {
                try { return new Uri(x); } catch { return null; }
            }).FirstOrDefault(x =>
            {
                if (x is null) return false;
                try { ValidatedDownloadClient.Validate(x, DownloadPolicy.Mrpack); return true; }
                catch (PanelException) { return false; }
            }) ?? throw new PanelException(502, "INSTALL_DOWNLOAD_REJECTED", $"No allowed download URL was provided for {file.Path}.");
            var target = resolver.Resolve(stage, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = Path.Combine(claim.Root, $"download-{Guid.NewGuid():N}");
            try
            {
                await downloads.DownloadAsync(new(
                    download, "sha512", file.Sha512, file.Size, Path.GetFileName(file.Path), DownloadPolicy.Mrpack),
                    temporary, token);
                File.Move(temporary, target, true);
                installed[file.Path] = 0;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            var count = Interlocked.Increment(ref completed);
            await progress(35 + (int)(35d * count / Math.Max(1, files.Count)), $"Downloading modpack files ({count}/{files.Count})");
        });

        using var archive = ZipFile.OpenRead(claim.ArchivePath);
        await ExtractLayerAsync(archive, "overrides/", stage, installed, cancellationToken);
        await ExtractLayerAsync(archive, "server-overrides/", stage, installed, cancellationToken);
        return new(installed.Keys.ToHashSet(StringComparer.Ordinal), selected);
    }

    public async Task CommitBaselineAsync(
        ServerEntity server, ClaimedModpack claim, InstalledPack installed,
        string stage, CancellationToken cancellationToken)
    {
        var entries = new List<BaselineFile>();
        foreach (var relative in installed.Paths.Order(StringComparer.Ordinal))
        {
            var file = resolver.Resolve(stage, relative.Replace('/', Path.DirectorySeparatorChar), false);
            if (!File.Exists(file)) continue;
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            entries.Add(new(relative, info.Length, await Sha512Async(file, cancellationToken)));
        }
        var stateDirectory = paths.ServerModpack(server.Id);
        if (Directory.Exists(stateDirectory))
            throw PanelProblems.Conflict("VALIDATION_FAILED", "Modpack state already exists for this server.");
        var temporary = stateDirectory + $".{Guid.NewGuid():N}.tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            File.Copy(claim.ArchivePath, Path.Combine(temporary, "source.mrpack"));
            var baseline = new PackBaseline(
                new(server.ModpackName!, server.ModpackVersion!, server.ModrinthProjectId,
                    server.ModrinthVersionId, server.ModpackSource ?? "Upload"),
                entries, claim.Parsed.Files.Where(x => x.Server == PackEnvironment.Optional &&
                    !installed.SelectedOptional.Contains(x.Path)).Select(x => x.Path).Order().ToList());
            await File.WriteAllTextAsync(Path.Combine(temporary, "baseline.json"),
                JsonSerializer.Serialize(baseline, JsonOptions), cancellationToken);
            Directory.Move(temporary, stateDirectory);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    public async Task<ModpackChangesDto> ChangesAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serverId, cancellationToken)
                     ?? throw PanelProblems.NotFound("Server");
        var summary = server.ModpackName is null || server.ModpackVersion is null ? null :
            new ModpackSummaryDto(server.ModpackName, server.ModpackVersion,
                server.ModrinthProjectId, server.ModrinthVersionId, server.ModpackSource ?? "Upload");
        var baselinePath = Path.Combine(paths.ServerModpack(serverId), "baseline.json");
        if (!File.Exists(baselinePath))
            return new(summary, DateTimeOffset.UtcNow, 0, 0, 0, [],
                summary is null ? "This server was not created from a modpack." : "The original modpack baseline is unavailable.");
        var baseline = JsonSerializer.Deserialize<PackBaseline>(
            await File.ReadAllTextAsync(baselinePath, cancellationToken), JsonOptions)
            ?? throw new PanelException(500, "OPERATION_FAILED", "The modpack baseline is invalid.");
        var root = paths.Instance(serverId);
        if (!Directory.Exists(root)) throw PanelProblems.NotFound("Server directory");
        var changes = new List<ModpackChangeDto>();
        var baselinePaths = baseline.Files.Select(x => x.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in baseline.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current;
            try { current = resolver.Resolve(root, expected.Path.Replace('/', Path.DirectorySeparatorChar)); }
            catch (PanelException exception) when (exception.Code == "PATH_OUTSIDE_SERVER")
            {
                changes.Add(new(expected.Path, ModpackChangeStatus.Removed, expected.Size, null));
                continue;
            }
            if (!File.Exists(current))
            {
                changes.Add(new(expected.Path, ModpackChangeStatus.Removed, expected.Size, null));
                continue;
            }
            var info = new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                changes.Add(new(expected.Path, ModpackChangeStatus.Removed, expected.Size, null));
                continue;
            }
            if (info.Length != expected.Size ||
                !string.Equals(await Sha512Async(current, cancellationToken), expected.Sha512, StringComparison.OrdinalIgnoreCase))
                changes.Add(new(expected.Path, ModpackChangeStatus.Modified, expected.Size, info.Length));
        }
        var mods = Path.Combine(root, "mods");
        if (Directory.Exists(mods) && (File.GetAttributes(mods) & FileAttributes.ReparsePoint) == 0)
        foreach (var file in Directory.EnumerateFiles(mods, "*.jar", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var relative = $"mods/{info.Name}";
            if (!baselinePaths.Contains(relative))
                changes.Add(new(relative, ModpackChangeStatus.Added, null, info.Length));
        }
        changes = changes.OrderBy(x => x.Status).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new(baseline.Modpack, DateTimeOffset.UtcNow,
            changes.Count(x => x.Status == ModpackChangeStatus.Added),
            changes.Count(x => x.Status == ModpackChangeStatus.Modified),
            changes.Count(x => x.Status == ModpackChangeStatus.Removed), changes);
    }

    public void Delete(Guid serverId)
    {
        var directory = paths.ServerModpack(serverId);
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    public void CleanupExpiredImports()
    {
        foreach (var directory in Directory.EnumerateDirectories(paths.ModpackImports))
        {
            try
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(directory) > ImportLifetime)
                    Directory.Delete(directory, true);
            }
            catch { }
        }
    }

    private async Task<ModpackInspectionDto> CreateImportAsync(
        PackOrigin origin, Func<string, Task> writeArchive, CancellationToken cancellationToken)
    {
        CleanupExpiredImports();
        var token = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(paths.ModpackImports, token);
        Directory.CreateDirectory(directory);
        var archive = Path.Combine(directory, "source.mrpack");
        try
        {
            await writeArchive(archive);
            var parsed = await ParseAsync(archive, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(directory, "origin.json"),
                JsonSerializer.Serialize(origin, JsonOptions), cancellationToken);
            return Inspection(token, origin, parsed);
        }
        catch
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
            throw;
        }
    }

    private ModpackInspectionDto Inspection(string token, PackOrigin origin, ParsedPack parsed) =>
        new(token, DateTimeOffset.UtcNow.Add(ImportLifetime), parsed.Name, parsed.Version,
            parsed.Kind, parsed.MinecraftVersion, parsed.LoaderVersion, origin.Source,
            origin.ProjectId, origin.VersionId,
            parsed.Files.Where(x => x.Server == PackEnvironment.Optional)
                .Select(x => new ModpackOptionalFileDto(x.Path, x.Size)).ToList());

    private async Task<ParsedPack> ParseAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count > options.Value.MaxArchiveEntries)
                throw new PanelException(400, "ZIP_LIMIT_EXCEEDED", "The modpack contains too many entries.");
            long total = 0;
            var entryNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new PanelException(400, "PATH_OUTSIDE_SERVER", "Modpacks containing symbolic links are rejected.");
                var normalized = NormalizeArchiveEntry(entry.FullName);
                if (!entryNames.Add(normalized))
                    throw PanelProblems.Validation("The modpack contains duplicate archive paths.");
                if (entry.Length < 0 || entry.Length > options.Value.MaxExtractedBytes - total ||
                    entry.CompressedLength > 0 && entry.Length > Math.Max(100L * entry.CompressedLength, 100L * 1024 * 1024))
                    throw new PanelException(400, "ZIP_LIMIT_EXCEEDED", "The modpack expands beyond the configured limit.");
                total += entry.Length;
            }
            var index = archive.GetEntry("modrinth.index.json")
                        ?? throw PanelProblems.Validation("The .mrpack is missing modrinth.index.json.");
            if (index.Length > 4 * 1024 * 1024) throw PanelProblems.Validation("The modpack index is too large.");
            await using var stream = index.Open();
            var manifest = await JsonSerializer.DeserializeAsync<PackManifest>(stream, JsonOptions, cancellationToken)
                           ?? throw PanelProblems.Validation("The modpack index is invalid.");
            if (manifest.FormatVersion != 1 || !string.Equals(manifest.Game, "minecraft", StringComparison.OrdinalIgnoreCase))
                throw PanelProblems.Validation("Only Minecraft .mrpack format version 1 is supported.");
            if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.VersionId))
                throw PanelProblems.Validation("The modpack name and version are required.");
            if (manifest.Dependencies is null || manifest.Files is null)
                throw PanelProblems.Validation("The modpack index is missing required metadata.");
            if (!manifest.Dependencies.TryGetValue("minecraft", out var minecraft) || string.IsNullOrWhiteSpace(minecraft))
                throw PanelProblems.Validation("The modpack does not declare a Minecraft version.");
            if (manifest.Name.Length > 256 || manifest.VersionId.Length > 128 || minecraft.Length > 64)
                throw PanelProblems.Validation("The modpack name, version, or Minecraft version is too long.");
            var loaders = manifest.Dependencies.Where(x => x.Key is "fabric-loader" or "forge" or "neoforge").ToList();
            if (manifest.Dependencies.Keys.Any(x => x is "quilt-loader") ||
                manifest.Dependencies.Keys.Any(x => x.EndsWith("-loader", StringComparison.OrdinalIgnoreCase) &&
                                                     x is not "fabric-loader"))
                throw PanelProblems.Validation("The modpack uses an unsupported loader.");
            if (loaders.Count > 1) throw PanelProblems.Validation("The modpack declares multiple loaders.");
            var kind = loaders.FirstOrDefault().Key switch
            {
                "fabric-loader" => ServerKind.Fabric,
                "forge" => ServerKind.Forge,
                "neoforge" => ServerKind.NeoForge,
                _ => ServerKind.Vanilla
            };
            var loaderVersion = loaders.FirstOrDefault().Value;
            if (loaderVersion?.Length > 64)
                throw PanelProblems.Validation("The modpack loader version is too long.");
            var files = new List<PackFile>();
            var targetPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in manifest.Files)
            {
                if (file is null || file.Hashes is null || file.Downloads is null)
                    throw PanelProblems.Validation("The modpack file metadata is incomplete.");
                var normalized = NormalizeTarget(file.Path);
                if (!targetPaths.Add(normalized)) throw PanelProblems.Validation("The modpack index contains duplicate file paths.");
                if (file.FileSize < 0 || string.IsNullOrWhiteSpace(file.Hashes.Sha1) ||
                    string.IsNullOrWhiteSpace(file.Hashes.Sha512) || file.Downloads.Count == 0)
                    throw PanelProblems.Validation($"The modpack file metadata for {normalized} is incomplete.");
                if (file.Hashes.Sha1.Length != 40 || file.Hashes.Sha512.Length != 128)
                    throw PanelProblems.Validation($"The modpack file hashes for {normalized} are invalid.");
                var environment = file.Env?.Server?.ToLowerInvariant() switch
                {
                    "unsupported" => PackEnvironment.Unsupported,
                    "optional" => PackEnvironment.Optional,
                    _ => PackEnvironment.Required
                };
                files.Add(new(normalized, file.FileSize, file.Hashes.Sha1, file.Hashes.Sha512,
                    file.Downloads, environment));
            }
            return new(manifest.Name.Trim(), manifest.VersionId.Trim(), minecraft.Trim(), kind,
                string.IsNullOrWhiteSpace(loaderVersion) ? null : loaderVersion.Trim(), files);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException or JsonException)
        {
            throw PanelProblems.Validation("The uploaded file is not a valid .mrpack archive.");
        }
    }

    private async Task ExtractLayerAsync(
        ZipArchive archive, string prefix, string stage,
        ConcurrentDictionary<string, byte> installed, CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries.Where(x => x.FullName.StartsWith(prefix, StringComparison.Ordinal) &&
                                                         x.FullName.Length > prefix.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            var relative = NormalizeTarget(entry.FullName[prefix.Length..]);
            var target = resolver.Resolve(stage, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temporary = target + $".mcpanel-{Guid.NewGuid():N}.tmp";
            try
            {
                await using var source = entry.Open();
                await using var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
                await source.CopyToAsync(destination, cancellationToken);
                File.Move(temporary, target, true);
                installed[relative] = 0;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static string NormalizeArchiveEntry(string path)
    {
        var value = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value)) return value;
        _ = NormalizeTarget(value.TrimEnd('/'));
        return value;
    }

    private static string NormalizeTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\0') || path.StartsWith('/') ||
            path.StartsWith('\\') || Path.IsPathRooted(path))
            throw new PanelException(400, "PATH_OUTSIDE_SERVER", "The modpack contains an unsafe path.");
        var parts = path.Replace('\\', '/').Split('/');
        if (parts.Any(x => x is "" or "." or ".." || x.Contains(':')))
            throw new PanelException(400, "PATH_OUTSIDE_SERVER", "The modpack contains an unsafe path.");
        return string.Join('/', parts);
    }

    private static async Task<string> Sha512Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA512.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private void AddToInstallSize(ref long total, long size)
    {
        if (size < 0 || size > options.Value.MaxExtractedBytes - total)
            throw new PanelException(400, "ZIP_LIMIT_EXCEEDED", "The modpack downloads exceed the configured extraction limit.");
        total += size;
    }

    public sealed record ClaimedModpack(string Root, string ArchivePath, PackOrigin Origin, ParsedPack Parsed) : IDisposable
    {
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    public sealed record InstalledPack(IReadOnlySet<string> Paths, IReadOnlySet<string> SelectedOptional);
    public sealed record PackOrigin(string Source, string? ProjectId, string? VersionId, string FileName);
    public sealed record ParsedPack(
        string Name, string Version, string MinecraftVersion, ServerKind Kind,
        string? LoaderVersion, IReadOnlyList<PackFile> Files);
    public sealed record PackFile(
        string Path, long Size, string Sha1, string Sha512,
        IReadOnlyList<string> Downloads, PackEnvironment Server);
    public enum PackEnvironment { Required, Optional, Unsupported }

    private sealed record PackBaseline(
        ModpackSummaryDto Modpack, IReadOnlyList<BaselineFile> Files,
        IReadOnlyList<string> ExcludedOptionalFiles);
    private sealed record BaselineFile(string Path, long Size, string Sha512);
    private sealed class PackManifest
    {
        public int FormatVersion { get; set; }
        public string Game { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string Name { get; set; } = "";
        public List<PackManifestFile> Files { get; set; } = [];
        public Dictionary<string, string> Dependencies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    private sealed class PackManifestFile
    {
        public string Path { get; set; } = "";
        public PackHashes Hashes { get; set; } = new();
        public PackEnvironmentBlock? Env { get; set; }
        public List<string> Downloads { get; set; } = [];
        public long FileSize { get; set; }
    }
    private sealed class PackHashes
    {
        public string Sha1 { get; set; } = "";
        public string Sha512 { get; set; } = "";
    }
    private sealed class PackEnvironmentBlock
    {
        public string? Server { get; set; }
    }
}
