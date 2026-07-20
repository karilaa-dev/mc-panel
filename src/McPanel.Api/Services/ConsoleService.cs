using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using McPanel.Api.Configuration;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace McPanel.Api.Services;

public sealed partial class ConsoleService(
    IDbContextFactory<ConsoleDbContext> consoleFactory,
    IDbContextFactory<StateDbContext> stateFactory,
    IHubContext<PanelHub> hub,
    SessionAudience audience,
    IOptions<PanelOptions> options,
    ILogger<ConsoleService> logger)
{
    private readonly ConcurrentDictionary<Guid, List<WaitRegistration>> _waiters = new();
    private long _appendCount;

    public async Task<ConsoleEventDto> AppendAsync(Guid serverId, string stream, string text, CancellationToken cancellationToken = default)
    {
        text = text.Length > 16_384 ? text[..16_384] : text;
        var entity = new ConsoleLineEntity
        {
            ServerId = serverId, Stream = stream, Level = DetectLevel(text), Text = text, Timestamp = DateTimeOffset.UtcNow
        };
        await using (var db = await consoleFactory.CreateDbContextAsync(cancellationToken))
        {
            db.Lines.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
        }
        var dto = new ConsoleEventDto(serverId, entity.Sequence, entity.Timestamp, entity.Stream, entity.Level, entity.Text);
        SignalWaiters(dto);
        _ = TrackPlayerAsync(dto);
        try { await audience.PublishAsync(group => hub.Clients.Group(group).SendAsync("ConsoleBatch", new[] { dto }, cancellationToken), cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogDebug(exception, "Could not broadcast console line"); }
        if (Interlocked.Increment(ref _appendCount) % 250 == 0) _ = PruneAsync(serverId, CancellationToken.None);
        return dto;
    }

    public async Task<IReadOnlyList<ConsoleEventDto>> ReadAsync(Guid serverId, long after, int limit, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 2_000);
        await using var db = await consoleFactory.CreateDbContextAsync(cancellationToken);
        return await db.Lines.Where(x => x.ServerId == serverId && x.Sequence > Math.Max(0, after))
            .OrderBy(x => x.Sequence).Take(limit)
            .Select(x => new ConsoleEventDto(x.ServerId, x.Sequence, x.Timestamp, x.Stream, x.Level, x.Text))
            .ToListAsync(cancellationToken);
    }

    public async Task<long> LatestSequenceAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var db = await consoleFactory.CreateDbContextAsync(cancellationToken);
        return await db.Lines.Where(x => x.ServerId == serverId).MaxAsync(x => (long?)x.Sequence, cancellationToken) ?? 0;
    }

    public async Task<bool> WaitForAsync(Guid serverId, long after, Func<ConsoleEventDto, bool> predicate, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = new WaitRegistration(predicate, source);
        var list = _waiters.GetOrAdd(serverId, static _ => []);
        lock (list) list.Add(registration);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var cancel = timeoutSource.Token.Register(() => source.TrySetResult(false));
        try
        {
            var existing = await ReadAsync(serverId, after, 2_000, cancellationToken);
            if (existing.Any(predicate)) source.TrySetResult(true);
            return await source.Task;
        }
        finally { lock (list) list.Remove(registration); }
    }

    public async Task PruneAsync(Guid serverId, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await consoleFactory.CreateDbContextAsync(cancellationToken);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.ConsoleRetentionDays);
            await db.Lines.Where(x => x.ServerId == serverId && x.Timestamp < cutoff).ExecuteDeleteAsync(cancellationToken);
            var sequence = await db.Lines.Where(x => x.ServerId == serverId).OrderByDescending(x => x.Sequence)
                .Skip(options.Value.ConsoleLinesPerServer).Select(x => (long?)x.Sequence).FirstOrDefaultAsync(cancellationToken);
            if (sequence.HasValue)
                await db.Lines.Where(x => x.ServerId == serverId && x.Sequence <= sequence.Value).ExecuteDeleteAsync(cancellationToken);
        }
        catch (Exception exception) { logger.LogWarning(exception, "Console retention failed for {ServerId}", serverId); }
    }

    private void SignalWaiters(ConsoleEventDto line)
    {
        if (!_waiters.TryGetValue(line.ServerId, out var list)) return;
        WaitRegistration[] snapshot;
        lock (list) snapshot = list.ToArray();
        foreach (var waiter in snapshot)
            if (waiter.Predicate(line)) waiter.Source.TrySetResult(true);
    }

    private async Task TrackPlayerAsync(ConsoleEventDto line)
    {
        try
        {
            var joined = JoinedRegex().Match(line.Text);
            var left = LeftRegex().Match(line.Text);
            var uuid = UuidRegex().Match(line.Text);
            if (!joined.Success && !left.Success && !uuid.Success) return;
            var name = (joined.Success ? joined : left.Success ? left : uuid).Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name)) return;
            await using var db = await stateFactory.CreateDbContextAsync();
            var player = await db.Players.SingleOrDefaultAsync(x => x.ServerId == line.ServerId && x.Name == name);
            if (player is null)
            {
                player = new PlayerEntity { ServerId = line.ServerId, Name = name };
                db.Players.Add(player);
            }
            if (joined.Success) player.Online = true;
            if (left.Success) player.Online = false;
            if (uuid.Success) player.Uuid = uuid.Groups["uuid"].Value;
            player.LastSeenAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception exception) { logger.LogDebug(exception, "Could not update player state from console"); }
    }

    private static string DetectLevel(string line) =>
        line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ? "error" :
        line.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? "warn" : line.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) ? "debug" : "info";

    private sealed record WaitRegistration(Func<ConsoleEventDto, bool> Predicate, TaskCompletionSource<bool> Source);

    [GeneratedRegex(@"(?<name>[A-Za-z0-9_]{1,16}) joined the game")]
    private static partial Regex JoinedRegex();
    [GeneratedRegex(@"(?<name>[A-Za-z0-9_]{1,16}) left the game")]
    private static partial Regex LeftRegex();
    [GeneratedRegex(@"UUID of player (?<name>[A-Za-z0-9_]{1,16}) is (?<uuid>[0-9a-fA-F-]{32,36})")]
    private static partial Regex UuidRegex();
}
