namespace McPanel.Api.Services;

public sealed class SessionAudience
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _stamp;

    public void Initialize(string? stamp) => Volatile.Write(ref _stamp, Normalize(stamp));

    public bool TryGetCurrentGroup(string? stamp, out string group)
    {
        var current = Volatile.Read(ref _stamp);
        if (current is not null && string.Equals(current, stamp, StringComparison.Ordinal))
        {
            group = GroupName(current);
            return true;
        }
        group = "";
        return false;
    }

    public async Task PublishAsync(Func<string, Task> publish, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = Volatile.Read(ref _stamp);
            if (current is not null) await publish(GroupName(current));
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> SetCurrentAsync(string stamp, CancellationToken cancellationToken)
    {
        var normalized = Normalize(stamp) ?? throw new ArgumentException("A session stamp is required.", nameof(stamp));
        await _gate.WaitAsync(cancellationToken);
        try { return SwitchCurrent(normalized); }
        finally { _gate.Release(); }
    }

    public async Task<string?> RotateAfterPersistAsync(
        string stamp,
        Func<Task> persist,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(stamp) ?? throw new ArgumentException("A session stamp is required.", nameof(stamp));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await persist();
            return SwitchCurrent(normalized);
        }
        finally { _gate.Release(); }
    }

    private string? SwitchCurrent(string stamp)
    {
        var previous = Interlocked.Exchange(ref _stamp, stamp);
        return previous is not null && !string.Equals(previous, stamp, StringComparison.Ordinal)
            ? GroupName(previous)
            : null;
    }

    private static string? Normalize(string? stamp) => string.IsNullOrWhiteSpace(stamp) ? null : stamp;
    private static string GroupName(string stamp) => $"mcpanel-session:{stamp}";
}
