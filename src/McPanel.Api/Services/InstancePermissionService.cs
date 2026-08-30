using McPanel.Api.Configuration;
using McPanel.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed class InstancePermissionService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    ILogger<InstancePermissionService> logger)
{
    public async Task NormalizeAllAsync(CancellationToken cancellationToken, bool tolerateFailures = true)
    {
        if (OperatingSystem.IsWindows()) return;
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var servers = await db.Servers.AsNoTracking().Select(x => new { x.Id, x.Kind }).ToListAsync(cancellationToken);
        foreach (var server in servers)
        {
            try { NormalizeTree(paths.Instance(server.Id), server.Kind == ServerKind.Gate, tolerateMissing: true); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Could not normalize permissions for {ServerId}", server.Id);
                if (!tolerateFailures) throw;
            }
        }
    }

    public async Task NormalizeInstanceAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) return;
        var kind = await KindAsync(serverId, cancellationToken);
        if (kind is null) return;
        NormalizeTree(paths.Instance(serverId), kind == ServerKind.Gate);
    }

    public Task NormalizeMutationAsync(
        Guid serverId,
        string changedPath,
        CancellationToken cancellationToken = default) =>
        NormalizeMutationsAsync(serverId, [changedPath], cancellationToken);

    public async Task NormalizeMutationsAsync(
        Guid serverId,
        IEnumerable<string> changedPaths,
        CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Instance(serverId)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var mutations = changedPaths.Select(Path.GetFullPath).Distinct(StringComparerFrom(comparison)).ToArray();
        if (mutations.Length == 0) return;
        foreach (var mutation in mutations)
        {
            if (!mutation.Equals(root, comparison) &&
                !mutation.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new InvalidOperationException("A permission mutation path escaped the server instance.");
        }

        var kind = await KindAsync(serverId, cancellationToken);
        if (kind is null) return;
        var privateInstance = kind == ServerKind.Gate;
        foreach (var mutation in mutations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NormalizeAncestors(root, mutation, privateInstance);
            NormalizeEntry(mutation, privateInstance);
        }
    }

    private async Task<ServerKind?> KindAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        return await db.Servers.AsNoTracking().Where(x => x.Id == serverId)
            .Select(x => (ServerKind?)x.Kind).SingleOrDefaultAsync(cancellationToken);
    }

#pragma warning disable CA1416 // Guarded by the public non-Windows entry points and import call site.
    internal static void NormalizeTree(string root, bool privateInstance, bool tolerateMissing = false)
    {
        if (!Directory.Exists(root)) return;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        string[] files;
        string[] directories;
        try
        {
            files = Directory.EnumerateFiles(root, "*", enumeration).ToArray();
            directories = Directory.EnumerateDirectories(root, "*", enumeration).Prepend(root).ToArray();
        }
        catch (Exception exception) when (tolerateMissing && exception is FileNotFoundException or DirectoryNotFoundException)
        { return; }
        foreach (var file in files) NormalizeEntry(file, privateInstance, tolerateMissing);
        foreach (var directory in directories) NormalizeEntry(directory, privateInstance, tolerateMissing);
    }

    private static void NormalizeAncestors(string root, string path, bool privateInstance)
    {
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (directory is not null)
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            if (normalized != root && !normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return;
            NormalizeEntry(directory, privateInstance, tolerateMissing: true);
            if (normalized == root) return;
            directory = Path.GetDirectoryName(directory);
        }
    }

    private static void NormalizeEntry(string path, bool privateInstance, bool tolerateMissing = true)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                if (!privateInstance)
                    mode |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup;
                File.SetUnixFileMode(path, mode);
                return;
            }

            var current = File.GetUnixFileMode(path);
            var executable = (current & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute)) != 0;
            var fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (executable) fileMode |= UnixFileMode.UserExecute;
            if (!privateInstance)
            {
                fileMode |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
                if (executable) fileMode |= UnixFileMode.GroupExecute;
            }
            File.SetUnixFileMode(path, fileMode);
        }
        catch (Exception exception) when (tolerateMissing && exception is FileNotFoundException or DirectoryNotFoundException)
        { }
    }

    private static StringComparer StringComparerFrom(StringComparison comparison) =>
        comparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
#pragma warning restore CA1416
}
