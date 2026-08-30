using McPanel.Api.Configuration;
using McPanel.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed class InstancePermissionService(
    PanelPaths paths,
    IDbContextFactory<StateDbContext> stateFactory,
    ILogger<InstancePermissionService> logger)
{
    public async Task NormalizeAllAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return;
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var servers = await db.Servers.AsNoTracking().Select(x => new { x.Id, x.Kind }).ToListAsync(cancellationToken);
        foreach (var server in servers)
        {
            try { NormalizeTree(paths.Instance(server.Id), server.Kind == ServerKind.Gate); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogWarning(exception, "Could not normalize permissions for {ServerId}", server.Id); }
        }
    }

    public async Task NormalizeAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) return;
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var kind = await db.Servers.AsNoTracking().Where(x => x.Id == serverId)
            .Select(x => (ServerKind?)x.Kind).SingleOrDefaultAsync(cancellationToken);
        if (kind is null) return;
        NormalizeTree(paths.Instance(serverId), kind == ServerKind.Gate);
    }

#pragma warning disable CA1416 // Guarded by the public non-Windows entry points and import call site.
    internal static void NormalizeTree(string root, bool privateInstance)
    {
        if (!Directory.Exists(root)) return;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false
        };
        foreach (var file in Directory.EnumerateFiles(root, "*", enumeration))
        {
            var current = File.GetUnixFileMode(file);
            var executable = (current & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute)) != 0;
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (executable) mode |= UnixFileMode.UserExecute;
            if (!privateInstance)
            {
                mode |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite;
                if (executable) mode |= UnixFileMode.GroupExecute;
            }
            File.SetUnixFileMode(file, mode);
        }
        foreach (var directory in Directory.EnumerateDirectories(root, "*", enumeration).Prepend(root))
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            if (!privateInstance)
                mode |= UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup;
            File.SetUnixFileMode(directory, mode);
        }
    }
#pragma warning restore CA1416
}
