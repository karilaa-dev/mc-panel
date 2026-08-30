using System.IO.Compression;
using System.Text;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed class FileManagerService(
    PanelPaths paths,
    SafePathResolver resolver,
    IOptions<PanelOptions> options,
    IDbContextFactory<StateDbContext> stateFactory,
    AsyncKeyedLock keyedLock,
    IServerProcessStatus processStatus,
    InstancePermissionService? permissions = null)
{
    public IReadOnlyList<FileEntryDto> List(Guid serverId, string relativePath)
    {
        var access = RequireAccess(serverId);
        var root = access.Root;
        var directory = resolver.Resolve(root, relativePath ?? "", false);
        RejectProtectedGatePath(access, directory);
        if (!Directory.Exists(directory)) throw PanelProblems.NotFound("Directory");
        return Directory.EnumerateFileSystemEntries(directory)
            .Where(path => !access.IsGate || !IsProtectedGatePath(root, path))
            .Select(path =>
        {
            var info = new FileInfo(path);
            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            return new FileEntryDto(info.Name, resolver.Relative(root, path), isDirectory, isDirectory ? 0 : info.Length, info.LastWriteTimeUtc);
        }).Where(x => x is not null).Select(x => x!).OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string> ReadTextAsync(Guid serverId, string relativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw PanelProblems.Validation("A file path is required.");
        var access = RequireAccess(serverId);
        var path = resolver.Resolve(access.Root, relativePath, false);
        RejectProtectedGatePath(access, path);
        var info = new FileInfo(path);
        if (!info.Exists) throw PanelProblems.NotFound("File");
        if (info.Length > options.Value.MaxTextFileBytes) throw new PanelException(413, "FILE_TOO_LARGE", "The file is too large for the text editor.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        try { return await reader.ReadToEndAsync(cancellationToken); }
        catch (DecoderFallbackException) { throw PanelProblems.Validation("The file is not valid UTF-8 text."); }
    }

    public async Task WriteTextAsync(Guid serverId, string relativePath, string content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || content is null) throw PanelProblems.Validation("A file path and content are required.");
        if (Encoding.UTF8.GetByteCount(content) > options.Value.MaxTextFileBytes) throw new PanelException(413, "FILE_TOO_LARGE", "The text is too large.");
        using var mutation = await AcquireMutationAsync(serverId, cancellationToken);
        var target = resolver.Resolve(mutation.Root, relativePath);
        RejectProtectedGatePath(mutation, target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + $".mcpanel-{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (permissions is not null) await permissions.NormalizeMutationAsync(serverId, target, cancellationToken);
    }

    public async Task CreateAsync(Guid serverId, string relativePath, bool directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw PanelProblems.Validation("A path is required.");
        using var mutation = await AcquireMutationAsync(serverId, cancellationToken);
        var path = resolver.Resolve(mutation.Root, relativePath);
        RejectProtectedGatePath(mutation, path);
        if (File.Exists(path) || Directory.Exists(path)) throw PanelProblems.Conflict("VALIDATION_FAILED", "The path already exists.");
        if (directory) Directory.CreateDirectory(path);
        else { Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var _ = new FileStream(path, FileMode.CreateNew); }
        if (permissions is not null) await permissions.NormalizeMutationAsync(serverId, path, cancellationToken);
    }

    public async Task UploadAsync(Guid serverId, string relativeDirectory, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > options.Value.MaxUploadBytes) throw new PanelException(413, "FILE_TOO_LARGE", "The uploaded file exceeds the configured limit.");
        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName != file.FileName.Replace('\\', '/').Split('/').Last())
            throw PanelProblems.Validation("The upload file name is invalid.");
        using var mutation = await AcquireMutationAsync(serverId, cancellationToken);
        var directory = resolver.Resolve(mutation.Root, relativeDirectory ?? "", false);
        RejectProtectedGatePath(mutation, directory);
        if (!Directory.Exists(directory)) throw PanelProblems.NotFound("Directory");
        var target = resolver.Resolve(directory, fileName);
        RejectProtectedGatePath(mutation, target);
        var temporary = target + $".mcpanel-{Guid.NewGuid():N}.upload";
        try
        {
            await using var source = file.OpenReadStream();
            await using var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > options.Value.MaxUploadBytes) throw new PanelException(413, "FILE_TOO_LARGE", "The uploaded file exceeds the configured limit.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await destination.FlushAsync(cancellationToken);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (permissions is not null) await permissions.NormalizeMutationAsync(serverId, target, cancellationToken);
    }

    public (string Path, string Name) Download(Guid serverId, string relativePath)
    {
        var access = RequireAccess(serverId);
        var path = resolver.Resolve(access.Root, relativePath, false);
        RejectProtectedGatePath(access, path);
        if (!File.Exists(path)) throw PanelProblems.NotFound("File");
        return (path, Path.GetFileName(path));
    }

    public async Task MoveAsync(Guid serverId, string sourceRelative, string destinationRelative, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceRelative) || string.IsNullOrWhiteSpace(destinationRelative)) throw PanelProblems.Validation("Source and destination paths are required.");
        using var mutation = await AcquireMutationAsync(serverId, cancellationToken);
        var root = mutation.Root;
        var source = resolver.Resolve(root, sourceRelative, false);
        RejectProtectedGatePath(mutation, source);
        var sourceIsFile = File.Exists(source);
        var sourceIsDirectory = Directory.Exists(source);
        if (!sourceIsFile && !sourceIsDirectory) throw PanelProblems.NotFound("Path");
        var destination = resolver.Resolve(root, destinationRelative);
        RejectProtectedGatePath(mutation, destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw PanelProblems.Conflict("VALIDATION_FAILED", "The destination already exists.");
        if (sourceIsDirectory && IsDescendant(destination, source))
            throw PanelProblems.Validation("A directory cannot be moved inside itself.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (sourceIsFile) File.Move(source, destination);
        else Directory.Move(source, destination);
        if (permissions is not null) await permissions.NormalizeMutationAsync(serverId, destination, cancellationToken);
    }

    public async Task DeleteAsync(Guid serverId, string relativePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) throw PanelProblems.Validation("The server root cannot be deleted through the file manager.");
        using var mutation = await AcquireMutationAsync(serverId, cancellationToken);
        var path = resolver.Resolve(mutation.Root, relativePath, false);
        RejectProtectedGatePath(mutation, path);
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);
        else throw PanelProblems.NotFound("Path");
    }

    public async Task ExtractAsync(Guid serverId, string archiveRelative, string destinationRelative, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(archiveRelative) || destinationRelative is null) throw PanelProblems.Validation("Archive and destination paths are required.");
        using var mutation = await AcquireMutationAsync(serverId, cancellationToken);
        var root = mutation.Root;
        var archivePath = resolver.Resolve(root, archiveRelative, false);
        RejectProtectedGatePath(mutation, archivePath);
        var destination = resolver.Resolve(root, destinationRelative);
        RejectProtectedGatePath(mutation, destination);
        if (!File.Exists(archivePath)) throw PanelProblems.NotFound("Archive");
        var stage = Path.Combine(paths.Staging, $"extract-{serverId:N}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count > options.Value.MaxArchiveEntries) throw new PanelException(400, "ZIP_LIMIT_EXCEEDED", "The archive contains too many entries.");
            long declared = 0;
            foreach (var entry in archive.Entries)
            {
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw new PanelException(400, "PATH_OUTSIDE_SERVER", "Archives containing symbolic links are rejected.");
                declared = checked(declared + entry.Length);
                if (declared > options.Value.MaxExtractedBytes || entry.CompressedLength > 0 && entry.Length > Math.Max(100L * entry.CompressedLength, 100L * 1024 * 1024))
                    throw new PanelException(400, "ZIP_LIMIT_EXCEEDED", "The archive expands beyond the safe extraction limit.");
                _ = resolver.Resolve(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            }
            long actual = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = resolver.Resolve(stage, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(output); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var source = entry.Open();
                await using var target = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, true);
                actual = await CopyWithLimitAsync(source, target, actual, options.Value.MaxExtractedBytes, cancellationToken);
            }
            if (mutation.IsGate) ValidateGateActivation(stage, destination, root);
            ValidateActivation(stage, destination);
            var changedPaths = Directory.EnumerateFileSystemEntries(stage, "*", SearchOption.AllDirectories)
                .Select(path => Path.Combine(destination, Path.GetRelativePath(stage, path)))
                .Append(destination)
                .ToArray();
            if (!Directory.Exists(destination)) Directory.Move(stage, destination);
            else ActivateDirectory(stage, destination);
            if (permissions is not null) await permissions.NormalizeMutationsAsync(serverId, changedPaths, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            throw PanelProblems.Validation("The archive is not a valid ZIP file.");
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    private static async Task<long> CopyWithLimitAsync(Stream source, Stream target, long total, long limit, CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024]; int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total = checked(total + read);
            if (total > limit) throw new PanelException(400, "ZIP_LIMIT_EXCEEDED", "The archive expands beyond the safe extraction limit.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return total;
    }

    private static void ActivateDirectory(string source, string destination)
    {
        if (Directory.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new PanelException(400, "PATH_OUTSIDE_SERVER", "Extraction cannot merge through a symbolic link.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source)) File.Move(file, Path.Combine(destination, Path.GetFileName(file)), true);
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(directory));
            if (Directory.Exists(target))
            {
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new PanelException(400, "PATH_OUTSIDE_SERVER", "Extraction cannot merge through a symbolic link.");
                ActivateDirectory(directory, target); Directory.Delete(directory);
            }
            else Directory.Move(directory, target);
        }
    }

    private static void ValidateActivation(string source, string destination)
    {
        var destinationAttributes = Attributes(destination);
        if (destinationAttributes.HasValue)
        {
            RejectActivationLink(destinationAttributes.Value);
            if ((destinationAttributes.Value & FileAttributes.Directory) == 0)
                throw ArchivePathConflict();
        }

        foreach (var sourceFile in Directory.EnumerateFiles(source))
        {
            var targetAttributes = Attributes(Path.Combine(destination, Path.GetFileName(sourceFile)));
            if (!targetAttributes.HasValue) continue;
            RejectActivationLink(targetAttributes.Value);
            if ((targetAttributes.Value & FileAttributes.Directory) != 0)
                throw ArchivePathConflict();
        }

        foreach (var sourceDirectory in Directory.EnumerateDirectories(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(sourceDirectory));
            var targetAttributes = Attributes(target);
            if (!targetAttributes.HasValue) continue;
            RejectActivationLink(targetAttributes.Value);
            if ((targetAttributes.Value & FileAttributes.Directory) == 0)
                throw ArchivePathConflict();
            ValidateActivation(sourceDirectory, target);
        }
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void RejectActivationLink(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new PanelException(400, "PATH_OUTSIDE_SERVER", "Extraction cannot merge through a symbolic link.");
    }

    private static PanelException ArchivePathConflict() =>
        PanelProblems.Conflict("VALIDATION_FAILED", "The archive contains a file that conflicts with an existing directory, or a directory that conflicts with an existing file.");

    private static bool IsDescendant(string candidate, string parent)
    {
        var prefix = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.StartsWith(prefix, comparison);
    }

    private ServerFileAccess RequireAccess(Guid serverId)
    {
        using (var db = stateFactory.CreateDbContext())
        {
            var server = db.Servers.AsNoTracking().SingleOrDefault(x => x.Id == serverId)
                ?? throw PanelProblems.NotFound("Server");
            var root = paths.Instance(serverId);
            if (!Directory.Exists(root)) throw PanelProblems.NotFound("Server directory");
            return new ServerFileAccess(root, server.Kind == ServerKind.Gate);
        }
    }

    private async Task<MutationLease> AcquireMutationAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var serverLock = await keyedLock.AcquireAsync(serverId, cancellationToken);
        try
        {
            await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
            var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serverId, cancellationToken)
                ?? throw PanelProblems.NotFound("Server");
            var processRunning = processStatus.IsRunning(serverId);
            var stateMatchesProcess = server.State switch
            {
                ServerState.Running => processRunning,
                ServerState.Stopped or ServerState.Crashed => !processRunning,
                _ => false
            };
            if (!stateMatchesProcess)
                throw PanelProblems.Conflict("SERVER_BUSY", "Files cannot be changed while the server is changing state.");

            var root = paths.Instance(serverId);
            if (!Directory.Exists(root)) throw PanelProblems.NotFound("Server directory");
            return new MutationLease(root, server.Kind == ServerKind.Gate, serverLock);
        }
        catch
        {
            serverLock.Dispose();
            throw;
        }
    }

    private static void ValidateGateActivation(string source, string destination, string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, entry);
            var target = Path.GetFullPath(Path.Combine(destination, relative));
            if (IsProtectedGatePath(root, target)) throw PanelProblems.NotFound("Path");
        }
    }

    private static void RejectProtectedGatePath(ServerFileAccess access, string path)
    {
        if (access.IsGate && IsProtectedGatePath(access.Root, path)) throw PanelProblems.NotFound("Path");
    }

    private static void RejectProtectedGatePath(MutationLease mutation, string path)
    {
        if (mutation.IsGate && IsProtectedGatePath(mutation.Root, path)) throw PanelProblems.NotFound("Path");
    }

    private static bool IsProtectedGatePath(string root, string path)
    {
        var protectedRoot = Path.GetFullPath(Path.Combine(root, "keys"));
        var candidate = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Equals(protectedRoot, comparison)
            || candidate.StartsWith(protectedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private record ServerFileAccess(string Root, bool IsGate);

    private sealed class MutationLease(string root, bool isGate, IDisposable serverLock) : IDisposable
    {
        public string Root { get; } = root;
        public bool IsGate { get; } = isGate;
        public void Dispose() => serverLock.Dispose();
    }
}
