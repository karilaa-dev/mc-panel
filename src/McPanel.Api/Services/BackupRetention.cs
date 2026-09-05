using McPanel.Api.Contracts;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed partial class BackupService
{
    private async Task ApplyRetentionLockedAsync(Guid serverId, CancellationToken token)
    {
        await using var db = await stateFactory.CreateDbContextAsync(token);
        var backups = await db.Backups.Where(x => x.ServerId == serverId).OrderByDescending(x => x.CreatedAt).ToListAsync(token);
        // Keep the newest verified copy even when it exceeds the configured budget.
        var protectedId = backups.FirstOrDefault(x => x.VerifiedAt != null && File.Exists(Path.Combine(paths.ServerBackups(serverId), x.FileName)))?.Id;
        long retainedBytes = 0; var retainedCount = 0;
        foreach (var backup in backups)
        {
            token.ThrowIfCancellationRequested();
            var keep = backup.Pinned || backup.Id == protectedId ||
                (retainedCount < Math.Max(1, options.Value.BackupRetentionCount) &&
                 backup.CreatedAt >= DateTimeOffset.UtcNow.AddDays(-Math.Max(1, options.Value.BackupRetentionDays)) &&
                 retainedBytes + backup.Size <= Math.Max(0, options.Value.BackupRetentionBytes));
            if (keep) { retainedBytes = checked(retainedBytes + backup.Size); retainedCount++; continue; }
            var file = Path.Combine(paths.ServerBackups(serverId), backup.FileName);
            if (File.Exists(file)) File.Delete(file);
            var modpack = BackupModpackState(serverId, backup.Id);
            if (Directory.Exists(modpack)) Directory.Delete(modpack, true);
            db.Backups.Remove(backup);
        }
        await db.SaveChangesAsync(token);
    }

    public async Task SetPinnedAsync(Guid serverId, Guid backupId, bool pinned, CancellationToken token)
    {
        using var serverLock = await keyedLock.AcquireAsync(serverId, token);
        await using var db = await stateFactory.CreateDbContextAsync(token);
        var backup = await db.Backups.SingleOrDefaultAsync(x => x.Id == backupId && x.ServerId == serverId, token) ?? throw PanelProblems.NotFound("Backup");
        backup.Pinned = pinned;
        await db.SaveChangesAsync(token);
    }
}
