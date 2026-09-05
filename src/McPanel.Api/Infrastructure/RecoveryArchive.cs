using System.IO.Compression;
using System.Text.Json;
using McPanel.Api.Data;

namespace McPanel.Api.Infrastructure;

public sealed record RecoveryFile(string Path, long Size, string Sha256, int? UnixMode = null);
public sealed record RecoveryManifest(int Format, string Kind, int Schema, string PanelVersion, DateTimeOffset CapturedAt, RecoveryFile[] Files);

/// <summary>Versioned, checksummed recovery packages. Extraction never writes outside a new staging directory.</summary>
public static class RecoveryArchive
{
    public static string Version => typeof(RecoveryArchive).Assembly.GetCustomAttributes(false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown";

    public static async Task PackAsync(string stage, string destination, string kind, DateTimeOffset capturedAt, CancellationToken token)
    {
        var files = new List<RecoveryFile>();
        foreach (var file in ArchiveIO.Files(stage).Order(StringComparer.Ordinal))
            files.Add(new(Path.GetRelativePath(stage, file).Replace('\\', '/'), new FileInfo(file).Length, await ArchiveIO.Sha256Async(file, token), OperatingSystem.IsWindows() ? null : (int)File.GetUnixFileMode(file) & 511));
        var manifest = new RecoveryManifest(1, kind, SchemaMigration.CurrentVersion, Version, capturedAt, files.ToArray());
        await File.WriteAllTextAsync(Path.Combine(stage, "manifest.json"), JsonSerializer.Serialize(manifest), token);
        await ArchiveIO.CompressAsync(stage, destination, token);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static async Task<RecoveryManifest> ExtractAsync(string source, string stage, long maxBytes, int maxEntries, long reserve, CancellationToken token)
    {
        if (Directory.Exists(stage)) throw new IOException("Recovery staging must not already exist.");
        using var archive = ZipFile.OpenRead(source);
        if (archive.Entries.Count > maxEntries) throw new InvalidDataException("Recovery package has too many entries.");
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Recovery manifest is missing.");
        if (entry.Length > 64 * 1024 * 1024) throw new InvalidDataException("Recovery manifest is too large.");
        using var manifestStream = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<RecoveryManifest>(manifestStream, cancellationToken: token) ?? throw new InvalidDataException("Recovery manifest is invalid.");
        if (manifest.Format != 1 || manifest.Schema > SchemaMigration.CurrentVersion || manifest.Kind is not ("panel" or "server")) throw new InvalidDataException("Recovery format requires a compatible MC Panel release.");
        var expected = manifest.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
        if (expected.ContainsKey("manifest.json")) throw new InvalidDataException("Recovery manifest lists itself.");
        long size = 0;
        foreach (var file in expected.Values)
        {
            if (file.Size < 0) throw new InvalidDataException("Invalid recovery entry size.");
            size = checked(size + file.Size);
        }
        if (size > maxBytes) throw new InvalidDataException("Recovery package exceeds the configured expanded size limit.");
        ArchiveIO.RequireSpace(Path.GetDirectoryName(stage)!, size, reserve);
        Directory.CreateDirectory(stage);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            foreach (var item in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (item.FullName == "manifest.json") continue;
                if (item.FullName.EndsWith('/'))
                {
                    var name = item.FullName.TrimEnd('/');
                    if (name.Length == 0 || name.Contains('\\') || name.StartsWith('/') || name.Split('/').Any(x => x is "" or "." or "..")) throw new InvalidDataException("Unsafe recovery directory.");
                    Directory.CreateDirectory(Path.Combine(stage, name)); continue;
                }
                if (!expected.Remove(item.FullName, out var file) || item.Length != file.Size ||
                    item.FullName.Contains('\\') || item.FullName.StartsWith('/') || item.FullName.Split('/').Any(x => x is "" or "." or "..") ||
                    ((item.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new InvalidDataException("Recovery package contains an unexpected or unsafe entry.");
                var target = Path.GetFullPath(Path.Combine(stage, item.FullName));
                if (!target.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Unsafe recovery path.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using (var input = item.Open())
                await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    var buffer = new byte[128 * 1024]; long written = 0; int read;
                    while ((read = await input.ReadAsync(buffer, token)) != 0)
                    {
                        written = checked(written + read);
                        if (written > file.Size) throw new InvalidDataException("Recovery entry exceeds its declared size.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (written != file.Size) throw new InvalidDataException("Recovery entry is truncated.");
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, (UnixFileMode)(file.UnixMode is { } mode ? mode & 511 : 384));
                if (await ArchiveIO.Sha256Async(target, token) != file.Sha256) throw new InvalidDataException($"Recovery checksum failed for {item.FullName}.");
            }
            if (expected.Count != 0) throw new InvalidDataException("Recovery package is incomplete.");
            return manifest;
        }
        catch { Directory.Delete(stage, true); throw; }
    }
}
