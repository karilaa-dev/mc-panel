namespace McPanel.Api.Services;

public sealed class InstancePermissionRepairService(
    InstancePermissionService permissions,
    IHostApplicationLifetime lifetime,
    ILogger<InstancePermissionRepairService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var stoppingRegistration = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        try
        {
            await started.Task;
            await permissions.NormalizeAllAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not repair existing instance permissions");
        }
    }
}
