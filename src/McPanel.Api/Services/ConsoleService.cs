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
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _playerLocks = new();
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
        await TrackPlayerAsync(dto, cancellationToken);
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

    public async Task<IReadOnlyList<ConsoleEventDto>> ReadGlobalAsync(long after, int limit, CancellationToken cancellationToken)
    {
        await using var db = await consoleFactory.CreateDbContextAsync(cancellationToken);
        return await db.Lines.Where(x => x.Sequence > Math.Max(0, after)).OrderBy(x => x.Sequence).Take(Math.Clamp(limit, 1, 2_000))
            .Select(x => new ConsoleEventDto(x.ServerId, x.Sequence, x.Timestamp, x.Stream, x.Level, x.Text)).ToListAsync(cancellationToken);
    }

    public async Task NormalizeRuntimeServerIdsAsync(long after, CancellationToken cancellationToken)
    {
        await using var db = await consoleFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "Lines" SET "ServerId" = upper("ServerId")
            WHERE "Sequence" > {Math.Max(0, after)} AND "ServerId" <> upper("ServerId");
            """, cancellationToken);
    }

    public async Task PublishExistingAsync(IReadOnlyList<ConsoleEventDto> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return;
        foreach (var line in lines) { SignalWaiters(line); await TrackPlayerAsync(line, cancellationToken); }
        try { await audience.PublishAsync(group => hub.Clients.Group(group).SendAsync("ConsoleBatch", lines, cancellationToken), cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogDebug(exception, "Could not broadcast imported console lines"); }
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

    private async Task TrackPlayerAsync(ConsoleEventDto line, CancellationToken cancellationToken)
    {
        var playerLock = _playerLocks.GetOrAdd(line.ServerId, static _ => new SemaphoreSlim(1, 1));
        await playerLock.WaitAsync(cancellationToken);
        try
        {
            var text = MinecraftLogText.SanitizeForParsing(line.Text);
            var joined = JoinedRegex().Match(text);
            var left = LeftRegex().Match(text);
            var uuid = UuidRegex().Match(text);
            if (!joined.Success && !left.Success && !uuid.Success) return;
            var name = (joined.Success ? joined : left.Success ? left : uuid).Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name)) return;
            var parsedUuid = uuid.Success ? NormalizeUuid(uuid.Groups["uuid"].Value) : null;
            await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
            var serverPlayers = await db.Players.Where(x => x.ServerId == line.ServerId).ToListAsync(cancellationToken);
            var player = parsedUuid is null ? null : serverPlayers.FirstOrDefault(candidate =>
                candidate.Uuid is not null && NormalizeUuid(candidate.Uuid) == parsedUuid);
            player ??= serverPlayers.FirstOrDefault(candidate =>
                candidate.Uuid is not null && candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            player ??= serverPlayers.FirstOrDefault(candidate => candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (player is null)
            {
                player = new PlayerEntity { ServerId = line.ServerId, Name = name };
                db.Players.Add(player);
                serverPlayers.Add(player);
            }
            bool? observedOnline = joined.Success ? true : left.Success ? false : null;
            if (observedOnline.HasValue) player.Online = observedOnline.Value;
            if (parsedUuid is not null) player.Uuid = parsedUuid;
            player.Name = name;
            player.LastSeenAt = DateTimeOffset.UtcNow;
            if (player.Uuid is not null)
            {
                foreach (var duplicate in serverPlayers.Where(candidate => candidate != player &&
                    ((candidate.Uuid is not null && NormalizeUuid(candidate.Uuid) == NormalizeUuid(player.Uuid)) ||
                     candidate.Name.Equals(player.Name, StringComparison.OrdinalIgnoreCase) ||
                     (candidate.Uuid is null && MinecraftLogText.IsLegacyAnsiLeakOf(candidate.Name, player.Name)))).ToList())
                {
                    if (!observedOnline.HasValue) player.Online |= duplicate.Online;
                    player.Whitelisted |= duplicate.Whitelisted;
                    player.Operator |= duplicate.Operator;
                    player.Banned |= duplicate.Banned;
                    if (duplicate.LastSeenAt > player.LastSeenAt) player.LastSeenAt = duplicate.LastSeenAt;
                    db.Players.Remove(duplicate);
                }
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { logger.LogDebug(exception, "Could not update player state from console"); }
        finally { playerLock.Release(); }
    }

    private static string NormalizeUuid(string value)
    {
        var hex = value.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        return hex.Length == 32
            ? $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}"
            : hex;
    }

    private static string DetectLevel(string line) =>
        line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("FATAL", StringComparison.OrdinalIgnoreCase) ? "error" :
        line.Contains("WARN", StringComparison.OrdinalIgnoreCase) ? "warn" : line.Contains("DEBUG", StringComparison.OrdinalIgnoreCase) ? "debug" : "info";

    private sealed record WaitRegistration(Func<ConsoleEventDto, bool> Predicate, TaskCompletionSource<bool> Source);

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?<name>[A-Za-z0-9_]{1,16})(?![A-Za-z0-9_]) joined the game")]
    private static partial Regex JoinedRegex();
    [GeneratedRegex(@"(?<![A-Za-z0-9_])(?<name>[A-Za-z0-9_]{1,16})(?![A-Za-z0-9_]) left the game")]
    private static partial Regex LeftRegex();
    [GeneratedRegex(@"UUID of player (?<name>[A-Za-z0-9_]{1,16})(?![A-Za-z0-9_]) is (?<uuid>[0-9a-fA-F-]{32,36})")]
    private static partial Regex UuidRegex();
}
