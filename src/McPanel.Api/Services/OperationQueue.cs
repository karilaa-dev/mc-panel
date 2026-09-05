using System.Threading.Channels;
using System.Collections.Concurrent;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Hubs;
using McPanel.Api.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed class OperationQueue(
    IServiceProvider serviceProvider,
    IDbContextFactory<StateDbContext> stateFactory,
    IHubContext<PanelHub> hub,
    SessionAudience audience,
    IHostApplicationLifetime lifetime,
    ILogger<OperationQueue> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeCancellation = new();
    private readonly Channel<QueuedOperation> _channel = Channel.CreateBounded<QueuedOperation>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false
    });

    public static JobEntity CreatePending(string type, Guid? serverId, string? clientRequestId = null) => new()
    {
        Id = Guid.NewGuid(), Type = type, ServerId = serverId, Message = "Waiting to run",
        ClientRequestId = string.IsNullOrWhiteSpace(clientRequestId) ? null : clientRequestId.Trim()
    };

    public async Task<JobDto> HandoffCommittedAsync(JobEntity job,
        Func<IServiceProvider, Guid, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        try
        {
            // The database row is authoritative. Cancellation of the HTTP request after commit
            // must not make a successfully-created server look like it never existed.
            await _channel.Writer.WriteAsync(new QueuedOperation(job.Id, action), lifetime.ApplicationStopping);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Committed job {JobId} could not be handed to the operation queue", job.Id);
            job.State = JobState.Failed;
            job.Progress = 100;
            job.Message = "Queue handoff failed";
            job.Error = "The server was created, but its installation could not be queued. Retry from this committed server record.";
            job.UpdatedAt = DateTimeOffset.UtcNow;
            try
            {
                await SetStateAsync(job.Id, job.State, job.Progress, job.Message, job.Error, CancellationToken.None);
                return await GetAsync(job.Id, CancellationToken.None) ?? Map(job);
            }
            catch (Exception persistenceException)
            {
                logger.LogError(persistenceException, "Could not persist failed queue handoff for committed job {JobId}", job.Id);
                return Map(job);
            }
        }
        var dto = Map(job);
        try { await audience.PublishAsync(group => hub.Clients.Group(group).SendAsync("JobUpdated", dto, cancellationToken), cancellationToken); }
        catch (Exception exception) { logger.LogDebug(exception, "Could not broadcast job {JobId}", job.Id); }
        return dto;
    }

    public async Task<JobDto> EnqueueAsync(string type, Guid? serverId, Func<IServiceProvider, Guid, CancellationToken, Task> action,
        CancellationToken cancellationToken, string? clientRequestId = null, string? inputJson = null, Guid? retryOf = null)
    {
        if (!string.IsNullOrWhiteSpace(clientRequestId))
        {
            await using var lookup = await stateFactory.CreateDbContextAsync(cancellationToken);
            var existing = await lookup.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.ClientRequestId == clientRequestId, cancellationToken);
            if (existing is not null)
            {
                if (existing.Type != type || existing.ServerId != serverId || existing.InputJson != inputJson)
                    throw PanelProblems.Conflict("REQUEST_ID_REUSED", "This request identifier belongs to a different operation.");
                return Map(existing);
            }
        }
        var job = CreatePending(type, serverId, clientRequestId);
        job.InputJson = inputJson; job.RetryOf = retryOf;
        await using (var db = await stateFactory.CreateDbContextAsync(cancellationToken))
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
        }
        return await HandoffCommittedAsync(job, action, cancellationToken);
    }

    public async Task ProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.FindAsync([jobId], cancellationToken);
        if (job is null) return;
        job.Progress = Math.Clamp(progress, 0, 99); job.Message = message; job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        try { await audience.PublishAsync(group => hub.Clients.Group(group).SendAsync("JobUpdated", Map(job), cancellationToken), cancellationToken); } catch (Exception exception) { logger.LogDebug(exception, "Could not broadcast job {JobId}", job.Id); }
    }

    public async Task<JobDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return job is null ? null : Map(job);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, 4).Select(_ => WorkerAsync(stoppingToken)).ToArray();
        await Task.WhenAll(workers);
    }

    private async Task WorkerAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _activeCancellation[item.JobId] = execution;
            try
            {
                await using (var db = await stateFactory.CreateDbContextAsync(stoppingToken))
                {
                    var claimed = await db.Jobs.Where(x => x.Id == item.JobId && x.State == JobState.Queued && !x.CancellationRequested)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, JobState.Running)
                            .SetProperty(x => x.Progress, 1).SetProperty(x => x.Message, "Running").SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), stoppingToken);
                    if (claimed == 0) continue;
                }
                execution.Token.ThrowIfCancellationRequested();
                await item.Action(serviceProvider, item.JobId, execution.Token);
                await SetStateAsync(item.JobId, JobState.Completed, 100, "Completed", null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await TrySetTerminalAsync(item.JobId, JobState.Interrupted, "Interrupted", "The panel stopped before completion was confirmed. Review server state before retrying.");
            }
            catch (OperationCanceledException) when (execution.IsCancellationRequested)
            {
                await TrySetTerminalAsync(item.JobId, JobState.Canceled, "Canceled", "Canceled at a safe operation boundary.");
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Operation {JobId} failed", item.JobId);
                await TrySetTerminalAsync(item.JobId, JobState.Failed, "Failed", exception.Message);
            }
            finally { _activeCancellation.TryRemove(item.JobId, out _); }
        }
    }

    private async Task TrySetTerminalAsync(Guid id, JobState state, string message, string error)
    {
        try { await SetStateAsync(id, state, 100, message, error, CancellationToken.None); }
        catch (Exception exception) { logger.LogError(exception, "Could not record terminal state for {JobId}; startup reconciliation is required", id); }
    }

    public async Task<IReadOnlyList<JobDto>> ListAsync(Guid? serverId, int limit, CancellationToken token)
    {
        await using var db = await stateFactory.CreateDbContextAsync(token);
        var query = db.Jobs.AsNoTracking();
        if (serverId.HasValue) query = query.Where(x => x.ServerId == serverId);
        return (await query.OrderByDescending(x => x.CreatedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(token)).Select(Map).ToList();
    }

    public async Task<JobDto> CancelAsync(Guid id, CancellationToken token)
    {
        await using var db = await stateFactory.CreateDbContextAsync(token);
        var changed = await db.Jobs.Where(x => x.Id == id && !x.CancellationRequested &&
                (x.State == JobState.Queued || x.State == JobState.Running && x.Type == "Backup"))
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CancellationRequested, true)
                .SetProperty(x => x.State, x => x.State == JobState.Queued ? JobState.Canceled : x.State)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), token);
        if (changed == 0) throw PanelProblems.Conflict("JOB_NOT_CANCELABLE", "This operation has completed or cannot be safely canceled while running.");
        if (_activeCancellation.TryGetValue(id, out var execution))
        { try { await execution.CancelAsync(); } catch (ObjectDisposedException) { } }
        return (await GetAsync(id, token))!;
    }

    private async Task SetStateAsync(Guid id, JobState state, int progress, string message, string? error, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.Jobs.FindAsync([id], cancellationToken);
        if (job is null) return;
        job.State = state; job.Progress = progress; job.Message = message; job.Error = error; job.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        try { await audience.PublishAsync(group => hub.Clients.Group(group).SendAsync("JobUpdated", Map(job), cancellationToken), cancellationToken); } catch (Exception exception) { logger.LogDebug(exception, "Could not broadcast job {JobId}", job.Id); }
    }

    public static bool IsTerminal(JobState state) => state is JobState.Completed or JobState.Failed or JobState.Interrupted or JobState.Canceled;
    private static JobDto Map(JobEntity job) => new(job.Id, job.Type, job.State, job.Progress, job.Message, job.Error, job.ServerId,
        job.CreatedAt, job.UpdatedAt,
        !job.CancellationRequested && (job.State == JobState.Queued || job.State == JobState.Running && job.Type == "Backup"),
        job.State is JobState.Failed or JobState.Interrupted or JobState.Canceled && job.InputJson is not null && job.Type is "Backup" or "Restore" or "Start" or "Stop" or "Install",
        job.RetryOf);
    private sealed record QueuedOperation(Guid JobId, Func<IServiceProvider, Guid, CancellationToken, Task> Action);
}
