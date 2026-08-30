using System.IO.Compression;
using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed class CustomJarService(
    PanelPaths paths,
    SafePathResolver resolver,
    IOptions<PanelOptions> options)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public async Task<CustomJarImportDto> PrepareAsync(IFormFile file, CancellationToken cancellationToken)
    {
        CleanupExpiredImports();
        if (file is null || file.Length <= 0) throw PanelProblems.Validation("Choose a non-empty JAR file.");
        if (file.Length > options.Value.MaxUploadBytes)
            throw new PanelException(413, "UPLOAD_TOO_LARGE", "The JAR exceeds the configured upload limit.");
        var fileName = Path.GetFileName(file.FileName);
        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw PanelProblems.Validation("Custom server software must use a .jar file name.");

        var token = Guid.NewGuid().ToString("N");
        var root = Path.Combine(paths.CustomJarImports, token);
        Directory.CreateDirectory(root);
        var jarPath = Path.Combine(root, "server.jar");
        try
        {
            await using (var input = file.OpenReadStream())
            await using (var output = new FileStream(jarPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[64 * 1024];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    written += read;
                    if (written > options.Value.MaxUploadBytes)
                        throw new PanelException(413, "UPLOAD_TOO_LARGE", "The JAR exceeds the configured upload limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }
            ValidateExecutableJar(jarPath);
            var createdAt = DateTimeOffset.UtcNow;
            var metadata = new ImportMetadata(fileName, new FileInfo(jarPath).Length, createdAt);
            await File.WriteAllTextAsync(Path.Combine(root, "metadata.json"), JsonSerializer.Serialize(metadata), cancellationToken);
            return new CustomJarImportDto(token, createdAt + Lifetime, fileName, metadata.Size);
        }
        catch
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            throw;
        }
    }

    public async Task<CustomJarImportDto> InspectAsync(string token, CancellationToken cancellationToken)
    {
        var (root, metadata) = await ReadImportAsync(token, cancellationToken);
        ValidateExecutableJar(Path.Combine(root, "server.jar"));
        return new CustomJarImportDto(token, metadata.CreatedAt + Lifetime, metadata.FileName, metadata.Size);
    }

    public async Task<ClaimedCustomJar> ClaimAsync(string token, CancellationToken cancellationToken)
    {
        var (root, metadata) = await ReadImportAsync(token, cancellationToken);
        var claimedRoot = Path.Combine(paths.Staging, $"custom-jar-{token}-{Guid.NewGuid():N}");
        try { Directory.Move(root, claimedRoot); }
        catch (DirectoryNotFoundException) { throw new PanelException(409, "IMPORT_ALREADY_USED", "This uploaded JAR was already used."); }
        var jar = Path.Combine(claimedRoot, "server.jar");
        ValidateExecutableJar(jar);
        return new ClaimedCustomJar(claimedRoot, jar, metadata.FileName, metadata.Size);
    }

    public IReadOnlyList<CustomJarCandidateDto> Candidates(string instanceRoot)
    {
        if (!Directory.Exists(instanceRoot)) return [];
        var result = new List<CustomJarCandidateDto>();
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 32
        };
        foreach (var file in Directory.EnumerateFiles(instanceRoot, "*.jar", enumeration))
        {
            try
            {
                resolver.Resolve(instanceRoot, resolver.Relative(instanceRoot, file), false);
                ValidateExecutableJar(file);
                result.Add(new CustomJarCandidateDto(resolver.Relative(instanceRoot, file), new FileInfo(file).Length));
            }
            catch (PanelException) { }
            catch (InvalidDataException) { }
            catch (IOException) { }
        }
        return result.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public string ResolveExisting(string instanceRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || !relativePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw PanelProblems.Validation("Choose an existing JAR from the server directory.");
        var fullPath = resolver.Resolve(instanceRoot, relativePath, false);
        if (!File.Exists(fullPath)) throw PanelProblems.NotFound("JAR file");
        ValidateExecutableJar(fullPath);
        return resolver.Relative(instanceRoot, fullPath);
    }

    public void CleanupExpiredImports()
    {
        if (!Directory.Exists(paths.CustomJarImports)) return;
        foreach (var root in Directory.EnumerateDirectories(paths.CustomJarImports))
        {
            try
            {
                var metadataPath = Path.Combine(root, "metadata.json");
                var expired = !File.Exists(metadataPath) ||
                    JsonSerializer.Deserialize<ImportMetadata>(File.ReadAllText(metadataPath)) is not { } metadata ||
                    metadata.CreatedAt + Lifetime <= DateTimeOffset.UtcNow;
                if (expired) Directory.Delete(root, true);
            }
            catch { }
        }
    }

    internal static void ValidateExecutableJar(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var manifest = archive.Entries.FirstOrDefault(x =>
                x.FullName.Equals("META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));
            if (manifest is null || manifest.Length > 1024 * 1024)
                throw PanelProblems.Validation("The JAR does not contain a valid executable manifest.");
            using var reader = new StreamReader(manifest.Open());
            var text = reader.ReadToEnd();
            var hasMainClass = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n').Any(line => line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(line["Main-Class:".Length..]));
            if (!hasMainClass) throw PanelProblems.Validation("The JAR manifest does not declare Main-Class.");
        }
        catch (PanelException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        { throw PanelProblems.Validation("The uploaded file is not a readable executable JAR."); }
    }

    private async Task<(string Root, ImportMetadata Metadata)> ReadImportAsync(string token, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(token, "N", out _)) throw PanelProblems.Validation("The custom JAR import token is invalid.");
        var root = Path.Combine(paths.CustomJarImports, token);
        var metadataPath = Path.Combine(root, "metadata.json");
        if (!File.Exists(metadataPath)) throw new PanelException(404, "IMPORT_NOT_FOUND", "The uploaded JAR was not found or has expired.");
        ImportMetadata metadata;
        try { metadata = JsonSerializer.Deserialize<ImportMetadata>(await File.ReadAllTextAsync(metadataPath, cancellationToken))!; }
        catch (Exception exception) when (exception is JsonException or IOException)
        { throw new PanelException(400, "IMPORT_INVALID", "The uploaded JAR metadata is invalid."); }
        if (metadata is null || metadata.CreatedAt + Lifetime <= DateTimeOffset.UtcNow)
        {
            try { Directory.Delete(root, true); } catch { }
            throw new PanelException(410, "IMPORT_EXPIRED", "The uploaded JAR has expired. Upload it again.");
        }
        return (root, metadata);
    }

    private sealed record ImportMetadata(string FileName, long Size, DateTimeOffset CreatedAt);

    public sealed record ClaimedCustomJar(string Root, string JarPath, string FileName, long Size) : IDisposable
    {
        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch { }
        }
    }
}
