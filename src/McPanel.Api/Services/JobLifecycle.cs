using System.Linq.Expressions;
using McPanel.Api.Data;

namespace McPanel.Api.Services;

// Expressions are used directly by SQLite conditional updates. Checking a loaded row
// before updating it would let a late progress report or worker overwrite cancellation.
internal static class JobLifecycle
{
    public static readonly Expression<Func<JobEntity, bool>> Active =
        job => job.State == JobState.Queued || job.State == JobState.Running;

    public static readonly Expression<Func<JobEntity, bool>> Claimable =
        job => job.State == JobState.Queued && !job.CancellationRequested;

    public static readonly Expression<Func<JobEntity, bool>> Cancelable =
        job => !job.CancellationRequested && (job.State == JobState.Queued || job.State == JobState.Running && job.Type == "Backup");

    public static readonly Func<JobEntity, bool> CanCancel = Cancelable.Compile();

    public static bool CanRetry(JobEntity job) =>
        job.State is JobState.Failed or JobState.Interrupted or JobState.Canceled &&
        job.InputJson is not null && job.ServerId.HasValue && job.Type is "Backup" or "Restore" or "Start" or "Stop" or "Install";

    public static bool IsTerminal(JobState state) =>
        state is JobState.Completed or JobState.Failed or JobState.Interrupted or JobState.Canceled;

    public static Expression<Func<JobEntity, bool>> CanFinishAs(JobState result) => result switch
    {
        JobState.Completed => job => job.State == JobState.Running,
        JobState.Failed or JobState.Interrupted or JobState.Canceled => Active,
        _ => throw new ArgumentOutOfRangeException(nameof(result))
    };
}
