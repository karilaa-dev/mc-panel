using System.IO.Compression;
using System.Security.Cryptography;

namespace McPanel.Api.Infrastructure;

public static class ArchiveIO
{
    public static IEnumerable<string> Files(string root) => Directory.EnumerateFiles(root, "*", new EnumerationOptions
    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false });

    public static IEnumerable<string> Directories(string root) => Directory.EnumerateDirectories(root, "*", new EnumerationOptions
    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false });

    public static (long Bytes, int Entries) Measure(string root)
    {
        long bytes = 0; var entries = 0;
        if (!Directory.Exists(root)) return (0, 0);
        foreach (var file in Files(root)) { bytes = checked(bytes + new FileInfo(file).Length); entries = checked(entries + 1); }
        return (bytes, entries);
    }

    public static DriveInfo DataDrive(string directory) => DriveInfo.GetDrives()
        .Where(drive => Path.GetFullPath(directory).Equals(Path.TrimEndingDirectorySeparator(drive.Name), StringComparison.Ordinal) ||
            Path.GetFullPath(directory).StartsWith(Path.EndsInDirectorySeparator(drive.Name) ? drive.Name : drive.Name + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .OrderByDescending(drive => drive.Name.Length).First();

    public static void RequireSpace(string directory, long bytes, long reserve)
    {
        if (DataDrive(directory).AvailableFreeSpace < checked(bytes + Math.Max(0, reserve)))
            throw new PanelException(409, "INSUFFICIENT_DISK_SPACE", "There is not enough free space for this operation and its recovery reserve.");
    }

    private static readonly object SpaceGate = new();
    private static readonly Dictionary<string, long> Reservations = new();
    public static IDisposable ReserveSpace(string directory, long bytes, long reserve)
    {
        var drive = DataDrive(directory);
        lock (SpaceGate)
        {
            var current = Reservations.GetValueOrDefault(drive.Name);
            if (bytes < 0 || drive.AvailableFreeSpace < checked(current + bytes + Math.Max(0, reserve)))
                throw new PanelException(409, "INSUFFICIENT_DISK_SPACE", "Concurrent operations would exceed the data filesystem capacity and recovery reserve.");
            Reservations[drive.Name] = checked(current + bytes);
            return new SpaceReservation(drive.Name, bytes);
        }
    }
    private sealed class SpaceReservation(string drive, long bytes) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) lock (SpaceGate) Reservations[drive] -= bytes; }
    }

    private static async Task CopyStreamAsync(Stream input, Stream output, CancellationToken token, Func<long, Task>? progress)
    {
        if (progress is null) { await input.CopyToAsync(output, token); return; }
        var buffer = new byte[128 * 1024]; long copied = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        { await output.WriteAsync(buffer.AsMemory(0, read), token); copied += read; await progress(copied); }
    }

    public static async Task CopyAsync(string source, string destination, CancellationToken token, Func<long, Task>? progress = null)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directories(source)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        long copied = 0;
        foreach (var file in Files(source))
        {
            token.ThrowIfCancellationRequested();
            if (Path.GetFileName(file).Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, true);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            await CopyStreamAsync(input, output, token, progress is null ? null : bytes => progress(copied + bytes)); copied += input.Length;
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file) & (UnixFileMode)511);
            if (progress is not null) await progress(copied);
        }
    }

    public static async Task CompressAsync(string source, string destination, CancellationToken token, Func<long, Task>? progress = null)
    {
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var directory in Directories(source)) archive.CreateEntry(Path.GetRelativePath(source, directory).Replace('\\', '/') + "/");
        long copied = 0;
        foreach (var file in Files(source))
        {
            token.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(Path.GetRelativePath(source, file).Replace('\\', '/'), CompressionLevel.Fastest);
            await using var entryStream = entry.Open();
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            await CopyStreamAsync(input, entryStream, token, progress is null ? null : bytes => progress(copied + bytes)); copied += input.Length;
            if (progress is not null) await progress(copied);
        }
    }

    public static async Task<string> Sha256Async(string file, CancellationToken token)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(input, token)).ToLowerInvariant();
    }
}
