using System.Text.Json;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed class JobRecoveryService(IDbContextFactory<StateDbContext> factory, OperationQueue queue,
    AsyncKeyedLock keyedLock, PanelPaths paths, IServiceProvider services)
{
    public async Task<JobDto> RetryAsync(Guid id, CancellationToken token)
    {
        using var retryLock = await keyedLock.AcquireAsync(id, token);
        await using var db = await factory.CreateDbContextAsync(token);
        var previous = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw PanelProblems.NotFound("Job");
        var existing = await db.Jobs.AsNoTracking().Where(x => x.RetryOf == id && (x.State == JobState.Queued || x.State == JobState.Running)).FirstOrDefaultAsync(token);
        if (existing is not null) return (await queue.GetAsync(existing.Id, token))!;
        if (previous.State is not (JobState.Failed or JobState.Interrupted or JobState.Canceled) || previous.InputJson is null || previous.ServerId is not { } serverId)
            throw PanelProblems.Conflict("JOB_NOT_RETRYABLE", "This operation has no supported retry. Review its result and use the server controls.");
        var server = await db.Servers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == serverId, token) ?? throw PanelProblems.NotFound("Server");
        if (server.RecoveryRequired) throw PanelProblems.Conflict("RECOVERY_REQUIRED", server.RecoveryReason ?? "Repair the interrupted recovery before retrying.");
        using var input = JsonDocument.Parse(previous.InputJson);
        Func<IServiceProvider, Guid, CancellationToken, Task> action;
        switch (previous.Type)
        {
            case "Backup":
                action = (_, job, ct) => services.GetRequiredService<BackupService>().CreateAsync(serverId, job, "Retry", ct); break;
            case "Restore":
                var backupId = input.RootElement.GetProperty("backupId").GetGuid();
                if (server.State != ServerState.Stopped) throw PanelProblems.Conflict("SERVER_NOT_STOPPED", "Stop the server before retrying a restore.");
                action = (_, job, ct) => services.GetRequiredService<BackupService>().RestoreAsync(serverId, backupId, job, ct); break;
            case "Start":
                if (server.State is not (ServerState.Stopped or ServerState.Crashed)) throw PanelProblems.Conflict("SERVER_BUSY", "Refresh server state before retrying start.");
                action = (_, _, ct) => services.GetRequiredService<ProcessSupervisor>().StartAsync(serverId, false, ct); break;
            case "Stop": action = (_, _, ct) => services.GetRequiredService<ProcessSupervisor>().StopAsync(serverId, ct); break;
            case "Install":
                if (server.Kind == ServerKind.CustomJar || server.State != ServerState.Error || Directory.Exists(paths.Instance(serverId)))
                    throw PanelProblems.Conflict("INSTALL_REQUIRES_REVIEW", "Installation retry requires an unactivated official server. Preserve existing files and review the Software page.");
                var request = JsonSerializer.Deserialize<CreateServerRequest>(previous.InputJson)!;
                action = (_, job, ct) => services.GetRequiredService<ServerInstallerService>().InstallAsync(serverId, job, request.IncludeExperimental, ct); break;
            default: throw PanelProblems.Conflict("JOB_NOT_RETRYABLE", "This operation needs review through its server page before it can be repeated.");
        }
        return await queue.EnqueueAsync(previous.Type, serverId, action, token, inputJson: previous.InputJson, retryOf: previous.Id);
    }
}
