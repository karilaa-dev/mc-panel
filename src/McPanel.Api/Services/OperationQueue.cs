using System.Threading.Channels;
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
    private readonly Channel<QueuedOperation> _channel = Channel.CreateBounded<QueuedOperation>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false
    });

    public async Task<JobDto> EnqueueAsync(string type, Guid? serverId, Func<IServiceProvider, Guid, CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        var job = new JobEntity { Id = Guid.NewGuid(), Type = type, ServerId = serverId, Message = "Waiting to run" };
        await using (var db = await stateFactory.CreateDbContextAsync(cancellationToken))
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
        }
        try
        {
            // Once the durable job row exists, request cancellation must not strand it outside
            // the in-memory queue. Application shutdown remains the only handoff cancellation.
            await _channel.Writer.WriteAsync(new QueuedOperation(job.Id, action), lifetime.ApplicationStopping);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
        {
            await SetStateAsync(job.Id, JobState.Failed, 100, "Interrupted", "The panel stopped before this operation could be queued.", CancellationToken.None);
            throw new PanelException(503, "OPERATION_FAILED", "The panel is stopping and cannot queue the operation.");
        }
        var dto = Map(job);
        try { await audience.PublishAsync(group => hub.Clients.Group(group).SendAsync("JobUpdated", dto, cancellationToken), cancellationToken); } catch (Exception exception) { logger.LogDebug(exception, "Could not broadcast job {JobId}", job.Id); }
        return dto;
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
            try
            {
                await SetStateAsync(item.JobId, JobState.Running, 1, "Running", null, stoppingToken);
                await item.Action(serviceProvider, item.JobId, stoppingToken);
                await SetStateAsync(item.JobId, JobState.Completed, 100, "Completed", null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                await SetStateAsync(item.JobId, JobState.Failed, 100, "Interrupted", "The panel stopped before this operation completed.", CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Operation {JobId} failed", item.JobId);
                await SetStateAsync(item.JobId, JobState.Failed, 100, "Failed", exception.Message, CancellationToken.None);
            }
        }
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

    private static JobDto Map(JobEntity job) => new(job.Id, job.Type, job.State, job.Progress, job.Message, job.Error);
    private sealed record QueuedOperation(Guid JobId, Func<IServiceProvider, Guid, CancellationToken, Task> Action);
}
