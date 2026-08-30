using System.ComponentModel;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public enum ServerImportFailureKind
{
    Usage = 2,
    InvalidSource = 3,
    Conflict = 4,
    Operation = 5
}

public sealed class ServerImportException(
    ServerImportFailureKind kind,
    string code,
    string message,
    string? field = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public ServerImportFailureKind Kind { get; } = kind;
    public string Code { get; } = code;
    public string? Field { get; } = field;
}

public sealed record ServerImportLauncher(string Path, LaunchMode Mode, ServerKind? SuggestedKind);

public sealed record ServerImportInspection(
    int? PropertiesPort,
    IReadOnlyList<ServerImportLauncher> Launchers,
    ServerKind? SuggestedKind,
    string? SuggestedVersion,
    string? SuggestedLoaderVersion);

public sealed record ServerImportRequest(
    string Name,
    ServerKind Kind,
    string Version,
    string? LoaderVersion,
    string LaunchTarget,
    string JavaRuntime,
    int MemoryMb,
    int Port,
    string JvmArguments,
    bool EulaAccepted);

public sealed record ServerImportValidation(
    string Name,
    ServerKind Kind,
    string Version,
    string? LoaderVersion,
    string LaunchTarget,
    LaunchMode LaunchMode,
    JavaRuntimeEntity Runtime,
    int RequiredJavaMajor,
    int MemoryMb,
    int MemoryLimitMb,
    int Port,
    string JvmArguments);

public sealed record ServerImportResult(
    Guid ServerId,
    string Name,
    ServerKind Kind,
    string Version,
    string InstanceDirectory,
    ServerState State);

public static class ServerImportSource
{
    public const int MaximumEntries = 1_000_000;
    public const long MaximumExpandedBytes = 1L * 1024 * 1024 * 1024 * 1024;
    public const long FreeSpaceReserveBytes = 1L * 1024 * 1024 * 1024;

    public static async Task StageAsync(string source, string destination, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source))
            throw Invalid("IMPORT_SOURCE_INVALID", "The import source must be an absolute path.");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw Invalid("IMPORT_DESTINATION_EXISTS", "The import staging destination already exists.");

        source = Path.GetFullPath(source);
        var directorySource = Directory.Exists(source);
        var fileSource = File.Exists(source);
        if (!directorySource && !fileSource)
            throw Invalid("IMPORT_SOURCE_NOT_FOUND", "The import source does not exist.");
        RejectLink(source);
        var parent = Path.GetDirectoryName(destination) ?? throw Invalid("IMPORT_DESTINATION_INVALID", "The import staging destination is invalid.");
        Directory.CreateDirectory(parent);
        var available = AvailableBytes(parent);
        var byteLimit = Math.Min(MaximumExpandedBytes, Math.Max(0, available - FreeSpaceReserveBytes));
        if (byteLimit <= 0)
            throw Invalid("IMPORT_DISK_SPACE", "At least 1 GiB of free disk space must remain after staging the import.");

        try
        {
            if (directorySource)
            {
                await CopyDirectoryAsync(source, destination, byteLimit, cancellationToken);
                return;
            }
            var extension = source.ToLowerInvariant();
            if (extension.EndsWith(".zip", StringComparison.Ordinal))
                await ExtractZipAsync(source, destination, byteLimit, cancellationToken);
            else if (extension.EndsWith(".tar", StringComparison.Ordinal) ||
                     extension.EndsWith(".tar.gz", StringComparison.Ordinal) ||
                     extension.EndsWith(".tgz", StringComparison.Ordinal))
                await ExtractTarAsync(source, destination, byteLimit, cancellationToken);
            else
                throw Invalid("IMPORT_ARCHIVE_TYPE", "Supported archive types are .zip, .tar, .tar.gz, and .tgz.");
        }
        catch
        {
            try { if (Directory.Exists(destination)) Directory.Delete(destination, true); } catch { }
            throw;
        }
    }

    private static async Task CopyDirectoryAsync(
        string source,
        string destination,
        long byteLimit,
        CancellationToken cancellationToken)
    {
        var entries = new List<(string Source, string Relative, bool Directory, long Length, DateTime LastWriteUtc)>();
        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((source, ""));
        long total = 0;
        while (pending.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLink(current.Path);
            IEnumerable<FileSystemInfo> children;
            try { children = new DirectoryInfo(current.Path).EnumerateFileSystemInfos().ToArray(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw Invalid("IMPORT_SOURCE_UNREADABLE", $"The source directory could not be read: {current.Path}", exception); }
            foreach (var child in children)
            {
                RejectLink(child.FullName);
                var relative = current.Relative.Length == 0 ? child.Name : Path.Combine(current.Relative, child.Name);
                if (++total > MaximumEntries) throw Invalid("IMPORT_ENTRY_LIMIT", $"The source contains more than {MaximumEntries:N0} entries.");
                if (child is DirectoryInfo directory)
                {
                    entries.Add((directory.FullName, relative, true, 0, directory.LastWriteTimeUtc));
                    pending.Push((directory.FullName, relative));
                }
                else if (child is FileInfo file)
                {
                    RejectHardLink(file.FullName);
                    long length;
                    try { length = file.Length; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    { throw Invalid("IMPORT_SOURCE_UNREADABLE", $"The source file could not be read: {file.FullName}", exception); }
                    entries.Add((file.FullName, relative, false, length, file.LastWriteTimeUtc));
                }
                else throw Invalid("IMPORT_SPECIAL_FILE", $"The source contains an unsupported file: {relative}");
            }
        }

        long declaredBytes = 0;
        foreach (var entry in entries)
        {
            if (entry.Directory) continue;
            try { declaredBytes = checked(declaredBytes + entry.Length); }
            catch (OverflowException) { throw Invalid("IMPORT_SIZE_LIMIT", "The source size is too large."); }
            EnsureBytes(declaredBytes, byteLimit);
        }

        Directory.CreateDirectory(destination);
        SetDirectoryMode(destination);
        long copiedBytes = 0;
        foreach (var entry in entries.Where(x => x.Directory).OrderBy(x => x.Relative.Count(c => c == Path.DirectorySeparatorChar)))
        {
            var target = ResolveDestination(destination, entry.Relative);
            Directory.CreateDirectory(target);
            SetDirectoryMode(target);
        }
        foreach (var entry in entries.Where(x => !x.Directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveDestination(destination, entry.Relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(entry.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            copiedBytes = await CopyLimitedAsync(input, output, copiedBytes, byteLimit, cancellationToken);
            File.SetLastWriteTimeUtc(target, entry.LastWriteUtc);
            SetFileMode(target);
        }
    }

    private static async Task ExtractZipAsync(
        string source,
        string destination,
        long byteLimit,
        CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(source);
            if (archive.Entries.Count > MaximumEntries)
                throw Invalid("IMPORT_ENTRY_LIMIT", $"The archive contains more than {MaximumEntries:N0} entries.");
            var paths = new HashSet<string>(StringComparer.Ordinal);
            long declaredBytes = 0;
            foreach (var entry in archive.Entries)
            {
                var relative = NormalizeEntryPath(entry.FullName);
                if (!paths.Add(relative)) throw Invalid("IMPORT_DUPLICATE_PATH", $"The archive repeats path '{relative}'.");
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal) || unixType == 0x4000;
                if (unixType != 0 && unixType is not (0x4000 or 0x8000))
                    throw Invalid("IMPORT_SPECIAL_FILE", $"The archive contains a link or special file: {relative}");
                if (directory) continue;
                try { declaredBytes = checked(declaredBytes + entry.Length); }
                catch (OverflowException) { throw Invalid("IMPORT_SIZE_LIMIT", "The archive expands beyond the supported size."); }
                EnsureBytes(declaredBytes, byteLimit);
            }

            Directory.CreateDirectory(destination);
            SetDirectoryMode(destination);
            long extractedBytes = 0;
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = NormalizeEntryPath(entry.FullName);
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                var directory = entry.FullName.EndsWith("/", StringComparison.Ordinal) || unixType == 0x4000;
                var target = ResolveDestination(destination, relative);
                if (directory)
                {
                    Directory.CreateDirectory(target);
                    SetDirectoryMode(target);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = entry.Open();
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                extractedBytes = await CopyLimitedAsync(input, output, extractedBytes, byteLimit, cancellationToken);
                if (entry.LastWriteTime != default) File.SetLastWriteTimeUtc(target, entry.LastWriteTime.UtcDateTime);
                SetFileMode(target);
            }
        }
        catch (ServerImportException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        { throw Invalid("IMPORT_ARCHIVE_INVALID", "The ZIP archive is invalid or unreadable.", exception); }
    }

    private static async Task ExtractTarAsync(
        string source,
        string destination,
        long byteLimit,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            Stream input = file;
            if (source.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) || source.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                input = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
            await using (input)
            using (var reader = new TarReader(input, leaveOpen: false))
            {
                Directory.CreateDirectory(destination);
                SetDirectoryMode(destination);
                var paths = new HashSet<string>(StringComparer.Ordinal);
                long extractedBytes = 0;
                var entries = 0;
                TarEntry? entry;
                while ((entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken)) is not null)
                {
                    if (++entries > MaximumEntries)
                        throw Invalid("IMPORT_ENTRY_LIMIT", $"The archive contains more than {MaximumEntries:N0} entries.");
                    var relative = NormalizeEntryPath(entry.Name);
                    if (!paths.Add(relative)) throw Invalid("IMPORT_DUPLICATE_PATH", $"The archive repeats path '{relative}'.");
                    if (entry.EntryType == TarEntryType.Directory)
                    {
                        var directory = ResolveDestination(destination, relative);
                        Directory.CreateDirectory(directory);
                        SetDirectoryMode(directory);
                        continue;
                    }
                    if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        throw Invalid("IMPORT_SPECIAL_FILE", $"The archive contains a link or special file: {relative}");
                    try { EnsureBytes(checked(extractedBytes + entry.Length), byteLimit); }
                    catch (OverflowException) { throw Invalid("IMPORT_SIZE_LIMIT", "The archive expands beyond the supported size."); }
                    var target = ResolveDestination(destination, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    extractedBytes = await CopyLimitedAsync(entry.DataStream ?? Stream.Null, output, extractedBytes, byteLimit, cancellationToken);
                    if (entry.ModificationTime != default) File.SetLastWriteTimeUtc(target, entry.ModificationTime.UtcDateTime);
                    SetFileMode(target);
                }
            }
        }
        catch (ServerImportException) { throw; }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        { throw Invalid("IMPORT_ARCHIVE_INVALID", "The tar archive is invalid or unreadable.", exception); }
    }

    private static async Task<long> CopyLimitedAsync(
        Stream input,
        Stream output,
        long priorBytes,
        long byteLimit,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        var total = priorBytes;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            try { total = checked(total + read); }
            catch (OverflowException) { throw Invalid("IMPORT_SIZE_LIMIT", "The source expands beyond the supported size."); }
            EnsureBytes(total, byteLimit);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return total;
    }

    private static string NormalizeEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0)
            throw Invalid("IMPORT_ARCHIVE_PATH", "The archive contains an empty or invalid path.");
        var normalized = value.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0 || normalized.StartsWith("/", StringComparison.Ordinal) || Regex.IsMatch(normalized, "^[A-Za-z]:"))
            throw Invalid("IMPORT_ARCHIVE_PATH", $"The archive contains an absolute path: {value}");
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw Invalid("IMPORT_ARCHIVE_PATH", $"The archive contains an unsafe path: {value}");
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private static string ResolveDestination(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        var destination = Path.GetFullPath(Path.Combine(root, relative));
        if (!destination.StartsWith(fullRoot, StringComparison.Ordinal))
            throw Invalid("IMPORT_ARCHIVE_PATH", $"The source path escapes the import root: {relative}");
        return destination;
    }

    private static void RejectLink(string path)
    {
        try
        {
            var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw Invalid("IMPORT_SYMBOLIC_LINK", $"The source contains a symbolic link: {path}");
        }
        catch (ServerImportException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { throw Invalid("IMPORT_SOURCE_UNREADABLE", $"The source path could not be inspected: {path}", exception); }
    }

    private static void RejectHardLink(string path)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            if (NativeMethods.Statx(NativeMethods.AtCurrentWorkingDirectory, path,
                    NativeMethods.NoFollow, NativeMethods.LinkCount, out var status) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            if (status.LinkCountValue > 1)
                throw Invalid("IMPORT_HARD_LINK", $"The source contains a hard-linked file: {path}");
        }
        catch (ServerImportException) { throw; }
        catch (Exception exception) when (exception is Win32Exception or EntryPointNotFoundException)
        { throw Invalid("IMPORT_SOURCE_UNREADABLE", $"The source file link count could not be inspected: {path}", exception); }
    }

    private static class NativeMethods
    {
        public const int AtCurrentWorkingDirectory = -100;
        public const int NoFollow = 0x100;
        public const uint LinkCount = 0x00000004;

        [DllImport("libc", EntryPoint = "statx", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern int Statx(int directoryFileDescriptor, string path, int flags, uint mask, out StatxBuffer buffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct StatxTimestamp
        {
            public long Seconds;
            public uint Nanoseconds;
            public int Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StatxBuffer
        {
            public uint Mask;
            public uint BlockSize;
            public ulong Attributes;
            public uint LinkCountValue;
            public uint UserId;
            public uint GroupId;
            public ushort Mode;
            public ushort Reserved;
            public ulong Inode;
            public ulong Size;
            public ulong Blocks;
            public ulong AttributesMask;
            public StatxTimestamp AccessTime;
            public StatxTimestamp BirthTime;
            public StatxTimestamp ChangeTime;
            public StatxTimestamp ModificationTime;
            public uint DeviceMajor;
            public uint DeviceMinor;
            public uint DeviceIdMajor;
            public uint DeviceIdMinor;
            public ulong Spare00;
            public ulong Spare01;
            public ulong Spare02;
            public ulong Spare03;
            public ulong Spare04;
            public ulong Spare05;
            public ulong Spare06;
            public ulong Spare07;
            public ulong Spare08;
            public ulong Spare09;
            public ulong Spare10;
            public ulong Spare11;
            public ulong Spare12;
            public ulong Spare13;
        }
    }

    private static long AvailableBytes(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception exception) { throw Invalid("IMPORT_DISK_SPACE", "Free disk space could not be determined.", exception); }
    }

    private static void EnsureBytes(long bytes, long byteLimit)
    {
        if (bytes > MaximumExpandedBytes)
            throw Invalid("IMPORT_SIZE_LIMIT", "The source expands beyond the 1 TiB import limit.");
        if (bytes > byteLimit)
            throw Invalid("IMPORT_DISK_SPACE", "The import would leave less than 1 GiB of free disk space.");
    }

    private static void SetDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void SetFileMode(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static ServerImportException Invalid(string code, string message, Exception? inner = null) =>
        new(ServerImportFailureKind.InvalidSource, code, message, innerException: inner);
}

public sealed partial class ServerImportService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    JavaDiscoveryService javaDiscovery,
    IOptions<PanelOptions> options)
{
    public async Task<ServerImportInspection> InspectAsync(string root, CancellationToken cancellationToken)
    {
        root = ValidateRoot(root);
        var propertiesPath = Path.Combine(root, "server.properties");
        if (!File.Exists(propertiesPath))
            throw Invalid("IMPORT_PROPERTIES_MISSING", "The source root must contain server.properties. Archives with a containing folder are not supported.");
        RejectManagedLink(propertiesPath, root);
        PropertiesDocument properties;
        try { properties = PropertiesDocument.Parse(await File.ReadAllTextAsync(propertiesPath, cancellationToken)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        { throw Invalid("IMPORT_PROPERTIES_INVALID", "server.properties could not be read as text.", exception); }
        int? port = int.TryParse(properties.Get("server-port"), out var parsedPort) && parsedPort is >= 1024 and <= 65535 ? parsedPort : null;

        var launchers = FindLaunchers(root);
        if (launchers.Count == 0)
            throw Invalid("IMPORT_LAUNCHER_MISSING", "The source root does not contain a supported server JAR or Forge-style unix_args.txt launcher.");
        var suggestedKind = launchers.Select(x => x.SuggestedKind).FirstOrDefault(x => x is not null);
        var suggestedLoader = SuggestedLoader(launchers);
        var suggestedVersion = SuggestedVersion(root, launchers, suggestedKind, suggestedLoader);
        return new(port, launchers, suggestedKind, suggestedVersion, suggestedLoader);
    }

    public async Task<ServerImportValidation> ValidateAsync(
        string root,
        ServerImportRequest request,
        CancellationToken cancellationToken)
    {
        root = ValidateRoot(root);
        _ = await InspectAsync(root, cancellationToken);
        if (request is null) throw Usage("IMPORT_REQUEST_REQUIRED", "Import settings are required.");
        var name = request.Name?.Trim() ?? "";
        if (!NameRegex().IsMatch(name))
            throw Usage("IMPORT_NAME_INVALID", "Server names may contain letters, numbers, spaces, '-' and '_' and must be 2 to 48 characters.", "name");
        if (request.Kind == ServerKind.Gate)
            throw Usage("IMPORT_KIND_INVALID", "Gate installations cannot be imported with import-server.", "kind");
        var version = request.Version?.Trim() ?? "";
        if (version.Length is < 1 or > 64)
            throw Usage("IMPORT_VERSION_INVALID", "The Minecraft version must be 1 to 64 characters.", "version");
        var loaderVersion = string.IsNullOrWhiteSpace(request.LoaderVersion) ? null : request.LoaderVersion.Trim();
        if (request.Kind is ServerKind.Fabric or ServerKind.Forge or ServerKind.NeoForge)
        {
            if (loaderVersion is null)
                throw Usage("IMPORT_LOADER_REQUIRED", $"{request.Kind} imports require a loader version.", "loader-version");
            if (loaderVersion.Length > 64)
                throw Usage("IMPORT_LOADER_INVALID", "The loader version must not exceed 64 characters.", "loader-version");
        }
        else if (loaderVersion is not null)
            throw Usage("IMPORT_LOADER_INVALID", "Loader version is only valid for Fabric, Forge, or NeoForge imports.", "loader-version");

        if (!request.EulaAccepted)
            throw Usage("IMPORT_EULA_REQUIRED", "You must explicitly accept the Minecraft EULA.", "accept-eula");
        if (request.MemoryMb is < PanelOptions.MinimumServerMemoryMb or > 1_048_576 || request.MemoryMb % PanelOptions.ServerMemoryStepMb != 0)
            throw Usage("IMPORT_MEMORY_INVALID", $"RAM must be at least {PanelOptions.MinimumServerMemoryMb} MiB and use {PanelOptions.ServerMemoryStepMb} MiB increments.", "memory-mb");
        if (request.Port is < 1024 or > 65535)
            throw Usage("IMPORT_PORT_INVALID", "Port must be between 1024 and 65535.", "port");
        try { _ = JvmArgumentParser.Parse(request.JvmArguments ?? ""); }
        catch (PanelException exception) { throw Usage("IMPORT_JVM_ARGUMENTS_INVALID", exception.Message, "jvm-args", exception); }

        var launchTarget = NormalizeLaunchTarget(request.LaunchTarget);
        var launchPath = Path.GetFullPath(Path.Combine(root, launchTarget));
        EnsureWithin(root, launchPath, "The launch target must stay inside the imported server root.");
        if (!File.Exists(launchPath)) throw Invalid("IMPORT_LAUNCHER_MISSING", $"The launch target does not exist: {launchTarget}");
        RejectManagedLink(launchPath, root);
        var launchMode = launchTarget.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
            ? LaunchMode.Jar
            : Path.GetFileName(launchTarget).Equals("unix_args.txt", StringComparison.OrdinalIgnoreCase)
                ? LaunchMode.ArgumentFile
                : throw Usage("IMPORT_LAUNCHER_INVALID", "The launch target must be a .jar file or Forge-style unix_args.txt.", "launch-target");
        ValidateLauncherKind(request.Kind, launchTarget, launchMode);

        await using (var conflictDb = await stateFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await conflictDb.Servers.AnyAsync(x => x.Name.ToLower() == name.ToLower(), cancellationToken))
                throw Conflict("IMPORT_NAME_CONFLICT", "A server with that name already exists.", "name");
            if (await conflictDb.Servers.AnyAsync(x => x.Port == request.Port, cancellationToken))
                throw Conflict("IMPORT_PORT_CONFLICT", $"Port {request.Port} is already assigned to another server.", "port");
        }

        JavaRuntimeEntity runtime;
        if (Path.IsPathFullyQualified(request.JavaRuntime))
        {
            JavaRuntimeDto probed;
            try { probed = await javaDiscovery.ProbeAsync(request.JavaRuntime, true, cancellationToken); }
            catch (PanelException exception)
            { throw Usage("IMPORT_JAVA_NOT_FOUND", exception.Message, "java-runtime", exception); }
            runtime = new JavaRuntimeEntity
            {
                Id = probed.Id,
                Path = probed.Path,
                Version = probed.Version,
                Major = probed.Major,
                Vendor = probed.Vendor,
                Architecture = probed.Architecture,
                IsCustom = probed.IsCustom
            };
        }
        else
        {
            await using var runtimeDb = await stateFactory.CreateDbContextAsync(cancellationToken);
            runtime = await runtimeDb.JavaRuntimes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.JavaRuntime, cancellationToken)
                ?? throw Usage("IMPORT_JAVA_NOT_FOUND", "The selected Java runtime was not found. Use a listed runtime ID or an absolute Java executable path.", "java-runtime");
            try
            {
                var probed = await javaDiscovery.ProbeAsync(runtime.Path, runtime.IsCustom, cancellationToken);
                runtime.Major = probed.Major;
                runtime.Version = probed.Version;
            }
            catch (PanelException exception)
            { throw Usage("IMPORT_JAVA_NOT_FOUND", exception.Message, "java-runtime", exception); }
        }

        var requiredJava = RequiredJava(request.Kind, version);
        if (runtime.Major < requiredJava)
            throw Usage("IMPORT_JAVA_INCOMPATIBLE", $"Minecraft {version} requires Java {requiredJava} or newer.", "java-runtime");
        if (request.Kind == ServerKind.Forge && requiredJava == 8 && runtime.Major != 8)
            throw Usage("IMPORT_JAVA_INCOMPATIBLE", $"Legacy Forge for Minecraft {version} requires Java 8.", "java-runtime");
        var totalLimitMb = MemorySizing.TotalForExistingHeapMb(request.MemoryMb);
        var totalMemory = HostMetricsService.ReadMemory().Total;
        if ((long)totalLimitMb * 1024 * 1024 > totalMemory * options.Value.MemoryAllocationFraction)
            throw Usage("IMPORT_MEMORY_LIMIT", "The selected memory exceeds the host allocation limit.", "memory-mb");

        return new(name, request.Kind, version, loaderVersion, launchTarget, launchMode, runtime,
            requiredJava, request.MemoryMb, totalLimitMb, request.Port, request.JvmArguments ?? "");
    }

    public async Task<ServerImportResult> ImportAsync(
        string root,
        ServerImportRequest request,
        CancellationToken cancellationToken)
    {
        var validated = await ValidateAsync(root, request, cancellationToken);
        root = ValidateRoot(root);
        var propertiesPath = Path.Combine(root, "server.properties");
        var properties = PropertiesDocument.Parse(await File.ReadAllTextAsync(propertiesPath, cancellationToken));
        properties.Set("server-port", validated.Port.ToString());
        await File.WriteAllTextAsync(propertiesPath, properties.ToString(), new UTF8Encoding(false), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(root, "eula.txt"),
            $"# Accepted during MC Panel import at {DateTimeOffset.UtcNow:O}{Environment.NewLine}eula=true{Environment.NewLine}",
            new UTF8Encoding(false), cancellationToken);

        var id = Guid.NewGuid();
        var destination = paths.Instance(id);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new ServerImportException(ServerImportFailureKind.Operation, "IMPORT_DESTINATION_EXISTS", "The generated managed server directory already exists.");
        var entity = new ServerEntity
        {
            Id = id,
            Name = validated.Name,
            Kind = validated.Kind,
            Version = validated.Version,
            LoaderVersion = validated.LoaderVersion,
            LaunchMode = validated.LaunchMode,
            LaunchTarget = validated.LaunchTarget,
            RequiredJavaMajor = validated.RequiredJavaMajor,
            JavaRuntimeId = validated.Runtime.Id,
            InitialMemoryMb = validated.MemoryMb,
            MemoryMb = validated.MemoryMb,
            MemoryLimitMb = validated.MemoryLimitMb,
            Port = validated.Port,
            JvmArguments = validated.JvmArguments,
            State = ServerState.Stopped,
            StartOnBoot = false,
            CrashRecovery = true,
            EulaAcceptedAt = DateTimeOffset.UtcNow
        };

        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var activated = false;
        try
        {
            var existingRuntime = await db.JavaRuntimes.FindAsync([validated.Runtime.Id], cancellationToken);
            if (existingRuntime is null)
            {
                db.JavaRuntimes.Add(new JavaRuntimeEntity
                {
                    Id = validated.Runtime.Id,
                    Path = validated.Runtime.Path,
                    Version = validated.Runtime.Version,
                    Major = validated.Runtime.Major,
                    Vendor = validated.Runtime.Vendor,
                    Architecture = validated.Runtime.Architecture,
                    IsCustom = Path.IsPathFullyQualified(request.JavaRuntime)
                });
            }
            else if (Path.IsPathFullyQualified(request.JavaRuntime))
            {
                existingRuntime.Path = validated.Runtime.Path;
                existingRuntime.Version = validated.Runtime.Version;
                existingRuntime.Major = validated.Runtime.Major;
                existingRuntime.Vendor = validated.Runtime.Vendor;
                existingRuntime.Architecture = validated.Runtime.Architecture;
                existingRuntime.IsCustom = true;
                existingRuntime.LastSeenAt = DateTimeOffset.UtcNow;
            }
            db.Servers.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            Directory.Move(root, destination);
            if (!OperatingSystem.IsWindows()) InstancePermissionService.NormalizeTree(destination, false);
            activated = true;
            await transaction.CommitAsync(cancellationToken);
        }
        catch (ServerImportException) { throw; }
        catch (Exception exception)
        {
            try { await transaction.RollbackAsync(CancellationToken.None); } catch { }
            if (activated)
            {
                try { Directory.Move(destination, root); }
                catch (Exception rollbackException)
                {
                    throw new ServerImportException(ServerImportFailureKind.Operation, "IMPORT_ROLLBACK_FAILED",
                        $"The import failed and the activated directory could not be rolled back: {destination}", innerException: new AggregateException(exception, rollbackException));
                }
            }
            throw new ServerImportException(ServerImportFailureKind.Operation, "IMPORT_FAILED", "The server could not be registered.", innerException: exception);
        }

        return new(id, entity.Name, entity.Kind, entity.Version, destination, entity.State);
    }

    public async Task<IReadOnlyList<JavaRuntimeEntity>> JavaRuntimesAsync(CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        return await db.JavaRuntimes.AsNoTracking().OrderByDescending(x => x.Major).ThenBy(x => x.Path).ToListAsync(cancellationToken);
    }

    private static List<ServerImportLauncher> FindLaunchers(string root)
    {
        var launchers = new List<ServerImportLauncher>();
        foreach (var file in Directory.EnumerateFiles(root, "*.jar", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            RejectManagedLink(file, root);
            var name = Path.GetFileName(file);
            ServerKind? kind = name.Contains("fabric", StringComparison.OrdinalIgnoreCase) ? ServerKind.Fabric
                : name.Contains("paper", StringComparison.OrdinalIgnoreCase) || name.Contains("purpur", StringComparison.OrdinalIgnoreCase) || name.Contains("spigot", StringComparison.OrdinalIgnoreCase) ? ServerKind.Paper
                : name.Contains("forge", StringComparison.OrdinalIgnoreCase) ? ServerKind.Forge
                : name.Equals("server.jar", StringComparison.OrdinalIgnoreCase) ? ServerKind.Vanilla
                : null;
            launchers.Add(new(name, LaunchMode.Jar, kind));
        }
        foreach (var family in new[]
                 {
                     (Path.Combine(root, "libraries", "net", "minecraftforge", "forge"), ServerKind.Forge),
                     (Path.Combine(root, "libraries", "net", "neoforged", "neoforge"), ServerKind.NeoForge)
                 })
        {
            if (!Directory.Exists(family.Item1)) continue;
            foreach (var file in Directory.EnumerateFiles(family.Item1, "unix_args.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                RejectManagedLink(file, root);
                launchers.Add(new(Path.GetRelativePath(root, file), LaunchMode.ArgumentFile, family.Item2));
            }
        }
        return launchers;
    }

    private static string? SuggestedLoader(IReadOnlyList<ServerImportLauncher> launchers)
    {
        var target = launchers.FirstOrDefault(x => x.Mode == LaunchMode.ArgumentFile);
        if (target is null) return null;
        var parent = Directory.GetParent(target.Path)?.Name;
        if (string.IsNullOrWhiteSpace(parent)) return null;
        if (target.SuggestedKind == ServerKind.Forge)
        {
            var dash = parent.IndexOf('-');
            return dash >= 0 && dash + 1 < parent.Length ? parent[(dash + 1)..] : null;
        }
        return parent;
    }

    private static string? SuggestedVersion(
        string root,
        IReadOnlyList<ServerImportLauncher> launchers,
        ServerKind? kind,
        string? loaderVersion)
    {
        var argument = launchers.FirstOrDefault(x => x.Mode == LaunchMode.ArgumentFile);
        if (argument is not null)
        {
            var coordinate = Directory.GetParent(argument.Path)?.Name;
            if (kind == ServerKind.Forge && coordinate is not null)
            {
                var dash = coordinate.IndexOf('-');
                if (dash > 0) return coordinate[..dash];
            }
            if (kind == ServerKind.NeoForge && loaderVersion is not null)
                return DistributionCatalogService.NeoForgeMinecraftVersion(loaderVersion);
        }
        foreach (var launcher in launchers.Where(x => x.Mode == LaunchMode.Jar))
        {
            var version = TryJarVersion(Path.Combine(root, launcher.Path));
            if (version is not null) return version;
        }
        return null;
    }

    private static string? TryJarVersion(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var versionEntry = archive.GetEntry("version.json");
            if (versionEntry is not null)
            {
                using var stream = versionEntry.Open();
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value && value.Length <= 64)
                    return value;
            }
            var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
            if (manifest is null) return null;
            using var reader = new StreamReader(manifest.Open(), Encoding.UTF8, true, 4096, false);
            var text = reader.ReadToEnd();
            var match = VersionRegex().Match(text);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static int RequiredJava(ServerKind kind, string version) =>
        kind == ServerKind.Paper ? DistributionCatalogService.InferPaperJava(version) : InferJava(version);

    private static int InferJava(string version)
    {
        var pieces = version.Split('-', '+')[0].Split('.');
        if (pieces.Length > 0 && int.TryParse(pieces[0], out var calendar) && calendar >= 26) return 25;
        if (pieces.Length < 2 || !int.TryParse(pieces[1], out var minor)) return 21;
        var patch = pieces.Length > 2 && int.TryParse(pieces[2], out var parsed) ? parsed : 0;
        return minor > 20 || minor == 20 && patch >= 5 ? 21 : minor >= 18 ? 17 : minor == 17 ? 16 : 8;
    }

    private static void ValidateLauncherKind(ServerKind kind, string target, LaunchMode mode)
    {
        var normalized = target.Replace('\\', '/');
        if (mode == LaunchMode.ArgumentFile)
        {
            if (kind == ServerKind.Forge && normalized.Contains("/minecraftforge/forge/", StringComparison.OrdinalIgnoreCase)) return;
            if (kind == ServerKind.NeoForge && normalized.Contains("/neoforged/neoforge/", StringComparison.OrdinalIgnoreCase)) return;
            throw Usage("IMPORT_LAUNCHER_KIND", "Argument-file launchers must match the selected Forge or NeoForge kind.", "launch-target");
        }
        if (kind == ServerKind.NeoForge && normalized.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase))
            throw Usage("IMPORT_LAUNCHER_KIND", "The selected launcher appears to be Forge, not NeoForge.", "launch-target");
        if (kind == ServerKind.Forge && normalized.Contains("neoforge", StringComparison.OrdinalIgnoreCase))
            throw Usage("IMPORT_LAUNCHER_KIND", "The selected launcher appears to be NeoForge, not Forge.", "launch-target");
    }

    private static string NormalizeLaunchTarget(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw Usage("IMPORT_LAUNCHER_REQUIRED", "A launch target is required.", "launch-target");
        var normalized = value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(x => x is "" or "." or ".."))
            throw Usage("IMPORT_LAUNCHER_INVALID", "The launch target must be a safe relative path.", "launch-target");
        if (normalized.Length > 512) throw Usage("IMPORT_LAUNCHER_INVALID", "The launch target must not exceed 512 characters.", "launch-target");
        return normalized;
    }

    private static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw Invalid("IMPORT_ROOT_INVALID", "The staged server root does not exist.");
        root = Path.GetFullPath(root);
        RejectManagedLink(root, root);
        return root;
    }

    private static void RejectManagedLink(string path, string root)
    {
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw Invalid("IMPORT_SYMBOLIC_LINK", $"The imported server contains a symbolic link: {Path.GetRelativePath(root, path)}");
    }

    private static void EnsureWithin(string root, string path, string message)
    {
        var prefix = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) throw Usage("IMPORT_LAUNCHER_INVALID", message, "launch-target");
    }

    private static ServerImportException Usage(string code, string message, string? field = null, Exception? inner = null) =>
        new(ServerImportFailureKind.Usage, code, message, field, inner);
    private static ServerImportException Invalid(string code, string message, Exception? inner = null) =>
        new(ServerImportFailureKind.InvalidSource, code, message, innerException: inner);
    private static ServerImportException Conflict(string code, string message, string field) =>
        new(ServerImportFailureKind.Conflict, code, message, field);

    [GeneratedRegex("^[A-Za-z0-9 _-]{2,48}$")]
    private static partial Regex NameRegex();
    [GeneratedRegex("(?<![0-9])([0-9]+\\.[0-9]+(?:\\.[0-9]+)?)(?![0-9])")]
    private static partial Regex VersionRegex();
}
