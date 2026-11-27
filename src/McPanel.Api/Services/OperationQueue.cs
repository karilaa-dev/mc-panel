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
    private readonly ConcurrentDictionary<Guid, IDisposable> _stagedInputs = new();
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
        Func<IServiceProvider, Guid, CancellationToken, Task> action, CancellationToken cancellationToken, IDisposable? unclaimedResource = null)
    {
        try
        {
            // The database row is authoritative. Cancellation of the HTTP request after commit
            // must not make a successfully-created server look like it never existed.
            if (unclaimedResource is not null)
            {
                _stagedInputs[job.Id] = unclaimedResource;
                // Cancellation can commit after the row is created and before ownership is registered here.
                if (await GetAsync(job.Id, lifetime.ApplicationStopping) is { State: JobState.Canceled } canceled)
                {
                    ReleaseStagedInput(job.Id);
                    return canceled;
                }
            }
            await _channel.Writer.WriteAsync(new QueuedOperation(job.Id, action), lifetime.ApplicationStopping);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Committed job {JobId} could not be handed to the operation queue", job.Id);
            ReleaseStagedInput(job.Id);
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

    public async Task<JobDto> RunImmediatelyAsync(string type, Guid serverId,
        Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var job = CreatePending(type, serverId);
        job.State = JobState.Running;
        job.Message = "Running";
        await using (var db = await stateFactory.CreateDbContextAsync(cancellationToken))
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
        }
        // Once committed, emergency work must finish even if the browser disconnects.
        try
        {
            await action(lifetime.ApplicationStopping);
            await SetStateAsync(job.Id, JobState.Completed, 100, "Completed", null, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await TrySetTerminalAsync(job.Id, JobState.Failed, "Failed", exception.Message);
            throw;
        }
        return (await GetAsync(job.Id, CancellationToken.None))!;
    }

    public async Task CancelPendingLifecycleAsync(Guid serverId, CancellationToken token)
    {
        await using var db = await stateFactory.CreateDbContextAsync(token);
        // A claimed start may not hold the server lock yet. Canceling it before the kill takes
        // that lock guarantees it cannot launch after the kill reports the server stopped.
        var pending = db.Jobs.Where(x => x.ServerId == serverId &&
            (x.State == JobState.Queued && (x.Type == "Start" || x.Type == "Stop" || x.Type == "Restart") ||
             x.State == JobState.Running && (x.Type == "Start" || x.Type == "Restart")));
        var ids = await pending.Select(x => x.Id).ToListAsync(token);
        await pending.ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.CancellationRequested, true)
            .SetProperty(x => x.State, x => x.State == JobState.Queued ? JobState.Canceled : x.State)
            .SetProperty(x => x.Message, "Canceled by immediate stop")
            .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), token);
        foreach (var id in ids)
            if (_activeCancellation.TryGetValue(id, out var execution))
            { try { await execution.CancelAsync(); } catch (ObjectDisposedException) { } }
    }

    public async Task ProgressAsync(Guid jobId, int progress, string message, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var changed = await db.Jobs.Where(x => x.Id == jobId).Where(JobLifecycle.Active)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Progress, Math.Clamp(progress, 0, 99))
                .SetProperty(x => x.Message, message).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        if (changed != 0) await PublishCurrentAsync(jobId, cancellationToken);
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
                    var claimed = await db.Jobs.Where(x => x.Id == item.JobId).Where(JobLifecycle.Claimable)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.State, JobState.Running)
                            .SetProperty(x => x.Progress, 1).SetProperty(x => x.Message, "Running").SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), stoppingToken);
                    if (claimed == 0) continue;
                }
                execution.Token.ThrowIfCancellationRequested();
                _stagedInputs.TryRemove(item.JobId, out _); // The installer now owns the claim.
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
            finally
            {
                _activeCancellation.TryRemove(item.JobId, out _);
                ReleaseStagedInput(item.JobId);
            }
        }
    }

    private void ReleaseStagedInput(Guid id)
    {
        if (!_stagedInputs.TryRemove(id, out var resource)) return;
        try { resource.Dispose(); }
        catch (Exception error) { logger.LogWarning(error, "Could not release staged input for {JobId}", id); }
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
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var changed = await db.Jobs.Where(x => x.Id == id).Where(JobLifecycle.Cancelable)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CancellationRequested, true)
                .SetProperty(x => x.Progress, x => x.State == JobState.Queued ? 100 : x.Progress)
                .SetProperty(x => x.Message, x => x.State == JobState.Queued ? "Canceled" : x.Message)
                .SetProperty(x => x.State, x => x.State == JobState.Queued ? JobState.Canceled : x.State)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), token);
        if (changed == 0) throw PanelProblems.Conflict("JOB_NOT_CANCELABLE", "This operation has completed or cannot be safely canceled while running.");
        await ReleaseUnfinishedInstallAsync(db, id, token);
        var canceled = await db.Jobs.AnyAsync(job => job.Id == id && job.State == JobState.Canceled, token);
        await transaction.CommitAsync(token);
        if (canceled) ReleaseStagedInput(id);
        if (_activeCancellation.TryGetValue(id, out var execution))
        { try { await execution.CancelAsync(); } catch (ObjectDisposedException) { } }
        await PublishCurrentAsync(id, token);
        return (await GetAsync(id, token))!;
    }

    // A worker can fail or be canceled before the installer ever runs.
    private static Task ReleaseUnfinishedInstallAsync(StateDbContext db, Guid jobId, CancellationToken token) =>
        db.Servers.Where(server => server.State == ServerState.Installing && db.Jobs.Any(job =>
            job.Id == jobId && job.ServerId == server.Id && job.State != JobState.Running &&
            (job.Type == "Install" || job.Type == "InstallModpack" || job.Type == "GateInstall")))
            .ExecuteUpdateAsync(set => set.SetProperty(server => server.State, ServerState.Error)
                .SetProperty(server => server.UpdatedAt, DateTimeOffset.UtcNow), token);

    private async Task SetStateAsync(Guid id, JobState state, int progress, string message, string? error, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = state == JobState.Completed ? null : await db.Database.BeginTransactionAsync(cancellationToken);
        var changed = await db.Jobs.Where(x => x.Id == id).Where(JobLifecycle.CanFinishAs(state))
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.State, state).SetProperty(x => x.Progress, progress)
                .SetProperty(x => x.Message, message).SetProperty(x => x.Error, error)
                .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        if (transaction is not null)
        {
            if (changed != 0) await ReleaseUnfinishedInstallAsync(db, id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        if (changed != 0) await PublishCurrentAsync(id, cancellationToken);
    }

    private async Task PublishCurrentAsync(Guid id, CancellationToken token)
    {
        try
        {
            var job = await GetAsync(id, token);
            if (job is not null)
                await audience.PublishAsync(group => hub.Clients.Group(group).SendAsync("JobUpdated", job, token), token);
        }
        catch (Exception exception) { logger.LogDebug(exception, "Could not broadcast job {JobId}", id); }
    }

    public static bool IsTerminal(JobState state) => JobLifecycle.IsTerminal(state);
    private static JobDto Map(JobEntity job) => new(job.Id, job.Type, job.State, job.Progress, job.Message, job.Error, job.ServerId,
        job.CreatedAt, job.UpdatedAt, JobLifecycle.CanCancel(job), JobLifecycle.CanRetry(job), job.RetryOf);
    private sealed record QueuedOperation(Guid JobId, Func<IServiceProvider, Guid, CancellationToken, Task> Action);
}
