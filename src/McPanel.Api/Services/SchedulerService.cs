using System.Globalization;
using System.Text.Json;
using McPanel.Api.Contracts;
using McPanel.Api.Data;
using McPanel.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace McPanel.Api.Services;

public sealed record ScheduleTrigger(DateTimeOffset? RunAt, int? IntervalMinutes, string? TimeOfDay, IReadOnlyList<int>? DaysOfWeek, string? Cron);

public static class ScheduleCalculator
{
    public static DateTimeOffset? Next(string frequency, ScheduleTrigger trigger, string timeZoneId, DateTimeOffset after)
    {
        var kind = frequency.Trim().ToLowerInvariant();
        if (kind is not ("once" or "interval" or "daily" or "weekly" or "cron"))
            throw PanelProblems.Validation("Frequency must be Once, Interval, Daily, Weekly, or Cron.");
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId); }
        catch (TimeZoneNotFoundException) { throw PanelProblems.Validation("The schedule time zone is unknown on this host."); }
        if (kind == "once") return trigger.RunAt > after ? trigger.RunAt : null;
        if (kind == "interval")
        {
            var minutes = trigger.IntervalMinutes.GetValueOrDefault();
            if (minutes is < 1 or > 525_600) throw PanelProblems.Validation("Interval must be between 1 and 525600 minutes.");
            return after.AddMinutes(minutes);
        }
        TimeOnly? time = null;
        if (kind is "daily" or "weekly")
        {
            if (!TimeOnly.TryParseExact(trigger.TimeOfDay, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw PanelProblems.Validation("Time of day must use HH:mm.");
            time = parsed;
        }
        var start = new DateTimeOffset(after.UtcDateTime.AddSeconds(-after.Second).AddTicks(-after.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero).AddMinutes(1);
        var limit = start.AddDays(370);
        var cron = kind == "cron" ? CronExpression.Parse(trigger.Cron) : null;
        for (var candidate = start; candidate <= limit; candidate = candidate.AddMinutes(1))
        {
            var local = TimeZoneInfo.ConvertTime(candidate, zone);
            if (kind == "daily" && local.Hour == time!.Value.Hour && local.Minute == time.Value.Minute) return candidate;
            if (kind == "weekly" && local.Hour == time!.Value.Hour && local.Minute == time.Value.Minute &&
                (trigger.DaysOfWeek?.Contains((int)local.DayOfWeek) ?? false)) return candidate;
            if (kind == "cron" && cron!.Matches(local)) return candidate;
        }
        return null;
    }

    private sealed class CronExpression(HashSet<int> minutes, HashSet<int> hours, HashSet<int> days, HashSet<int> months, HashSet<int> weekdays)
    {
        public bool Matches(DateTimeOffset value) => minutes.Contains(value.Minute) && hours.Contains(value.Hour) && days.Contains(value.Day) && months.Contains(value.Month) && weekdays.Contains((int)value.DayOfWeek);

        public static CronExpression Parse(string? expression)
        {
            var fields = (expression ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5) throw PanelProblems.Validation("Cron must contain five fields: minute hour day month weekday.");
            return new(ParseField(fields[0], 0, 59), ParseField(fields[1], 0, 23), ParseField(fields[2], 1, 31), ParseField(fields[3], 1, 12), ParseField(fields[4], 0, 6, true));
        }

        private static HashSet<int> ParseField(string field, int minimum, int maximum, bool normalizeSunday = false)
        {
            var result = new HashSet<int>();
            foreach (var part in field.Split(','))
            {
                var stepParts = part.Split('/', 2);
                var step = stepParts.Length == 2 && int.TryParse(stepParts[1], out var parsedStep) ? parsedStep : 1;
                if (step < 1 || step > maximum - minimum + 1) throw PanelProblems.Validation("Cron step is invalid.");
                var rangeText = stepParts[0];
                int start, end;
                if (rangeText == "*") { start = minimum; end = maximum; }
                else if (rangeText.Contains('-'))
                {
                    var values = rangeText.Split('-', 2);
                    if (!int.TryParse(values[0], out start) || !int.TryParse(values[1], out end)) throw PanelProblems.Validation("Cron range is invalid.");
                }
                else
                {
                    if (!int.TryParse(rangeText, out start)) throw PanelProblems.Validation("Cron value is invalid.");
                    end = start;
                }
                if (normalizeSunday && start == 7) start = 0;
                if (normalizeSunday && end == 7) end = 0;
                if (start < minimum || start > maximum || end < minimum || end > maximum || end < start) throw PanelProblems.Validation("Cron value is outside its allowed range.");
                for (var value = start; value <= end; value += step) result.Add(value);
            }
            return result;
        }
    }
}

public sealed class SchedulerService(
    IDbContextFactory<StateDbContext> stateFactory,
    ProcessSupervisor supervisor,
    BackupService backups,
    PlayerInventoryService playerInventories,
    ServerInstallerService installer,
    OperationQueue operations,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ScheduleDto>> ListAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await EnsureServerAsync(serverId, cancellationToken);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var entities = await db.Schedules.Where(x => x.ServerId == serverId).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        return entities.Select(Map).ToList();
    }

    public async Task<ScheduleDto> CreateAsync(Guid serverId, SaveScheduleRequest request, CancellationToken cancellationToken)
    {
        await EnsureServerAsync(serverId, cancellationToken);
        var normalized = Normalize(request);
        var now = DateTimeOffset.UtcNow;
        var next = request.Enabled ? ScheduleCalculator.Next(request.Frequency, normalized.Trigger, request.TimeZone, now) : null;
        var entity = new ScheduleEntity
        {
            Id = Guid.NewGuid(), ServerId = serverId, Name = request.Name.Trim(), Frequency = NormalizeFrequency(request.Frequency),
            TimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone, Enabled = request.Enabled,
            TriggerJson = JsonSerializer.Serialize(normalized.Trigger, JsonOptions), ActionsJson = JsonSerializer.Serialize(normalized.Actions, JsonOptions), NextRunAt = next
        };
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        db.Schedules.Add(entity); await db.SaveChangesAsync(cancellationToken); return Map(entity);
    }

    public async Task<ScheduleDto> UpdateAsync(Guid serverId, Guid id, SaveScheduleRequest request, CancellationToken cancellationToken)
    {
        var normalized = Normalize(request);
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Schedules.SingleOrDefaultAsync(x => x.ServerId == serverId && x.Id == id, cancellationToken) ?? throw PanelProblems.NotFound("Schedule");
        entity.Name = request.Name.Trim(); entity.Frequency = NormalizeFrequency(request.Frequency);
        entity.TimeZone = string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone; entity.Enabled = request.Enabled;
        entity.TriggerJson = JsonSerializer.Serialize(normalized.Trigger, JsonOptions); entity.ActionsJson = JsonSerializer.Serialize(normalized.Actions, JsonOptions);
        entity.NextRunAt = request.Enabled ? ScheduleCalculator.Next(entity.Frequency, normalized.Trigger, entity.TimeZone, DateTimeOffset.UtcNow) : null;
        await db.SaveChangesAsync(cancellationToken); return Map(entity);
    }

    public async Task ToggleAsync(Guid serverId, Guid id, bool enabled, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Schedules.SingleOrDefaultAsync(x => x.ServerId == serverId && x.Id == id, cancellationToken) ?? throw PanelProblems.NotFound("Schedule");
        entity.Enabled = enabled; entity.NextRunAt = enabled ? ScheduleCalculator.Next(entity.Frequency, Trigger(entity), entity.TimeZone, DateTimeOffset.UtcNow) : null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid serverId, Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Schedules.SingleOrDefaultAsync(x => x.ServerId == serverId && x.Id == id, cancellationToken) ?? throw PanelProblems.NotFound("Schedule");
        db.Schedules.Remove(entity); await db.SaveChangesAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SkipMissedAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var db = await stateFactory.CreateDbContextAsync(stoppingToken);
                var now = DateTimeOffset.UtcNow;
                var due = await db.Schedules
                    .Where(x => x.Enabled && !x.IsRunning && x.NextRunAt != null && x.NextRunAt <= now)
                    .OrderBy(x => x.NextRunAt)
                    .Select(x => x.Id)
                    .Take(20)
                    .ToListAsync(stoppingToken);
                foreach (var id in due) _ = RunAsync(id, stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException) { logger.LogError(exception, "Schedule polling failed"); }
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task RunAsync(Guid id, CancellationToken cancellationToken)
    {
        ScheduleEntity entity;
        await using (var db = await stateFactory.CreateDbContextAsync(cancellationToken))
        {
            var found = await db.Schedules.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (found is null) return;
            entity = found;
            if (entity.IsRunning || !entity.Enabled) return;
            entity.IsRunning = true; entity.LastRunAt = DateTimeOffset.UtcNow;
            entity.NextRunAt = ScheduleCalculator.Next(entity.Frequency, Trigger(entity), entity.TimeZone, entity.LastRunAt.Value);
            if (entity.Frequency.Equals("Once", StringComparison.OrdinalIgnoreCase)) entity.Enabled = false;
            await db.SaveChangesAsync(cancellationToken);
        }
        try
        {
            foreach (var action in Actions(entity)) await ExecuteActionAsync(entity.ServerId, action, cancellationToken);
            entity.LastResult = "Completed";
        }
        catch (Exception exception) { entity.LastResult = "Failed: " + exception.Message[..Math.Min(900, exception.Message.Length)]; logger.LogError(exception, "Schedule {ScheduleId} failed", id); }
        finally
        {
            await using var db = await stateFactory.CreateDbContextAsync(CancellationToken.None);
            var current = await db.Schedules.FindAsync(id);
            if (current is not null) { current.IsRunning = false; current.LastResult = entity.LastResult; await db.SaveChangesAsync(); }
        }
    }

    private async Task ExecuteActionAsync(Guid serverId, ScheduleActionDto action, CancellationToken cancellationToken)
    {
        switch (action.Action.ToLowerInvariant())
        {
            case "start": await supervisor.StartAsync(serverId, false, cancellationToken); break;
            case "stop": await supervisor.StopAsync(serverId, cancellationToken); break;
            case "restart": await supervisor.RestartAsync(serverId, cancellationToken); break;
            case "command": await supervisor.CommandAsync(serverId, action.Command ?? throw PanelProblems.Validation("Command action needs a command."), cancellationToken); break;
            case "backup": await backups.RunScheduledAsync(serverId, cancellationToken); break;
            case "inventorybackup":
                await playerInventories.CreateScheduledBackupsAsync(serverId, cancellationToken);
                break;
            case "update":
                var job = await installer.QueueUpdateAsync(serverId, cancellationToken);
                await WaitJobAsync(job.Id, cancellationToken); break;
            default: throw PanelProblems.Validation($"Unknown schedule action '{action.Action}'.");
        }
    }

    private async Task WaitJobAsync(Guid id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var job = await operations.GetAsync(id, cancellationToken) ?? throw PanelProblems.NotFound("Job");
            if (job.State == JobState.Completed) return;
            if (job.State == JobState.Failed) throw new PanelException(500, "OPERATION_FAILED", "Scheduled action failed.", job.Error);
            await Task.Delay(500, cancellationToken);
        }
    }

    private async Task SkipMissedAsync(CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var schedules = await db.Schedules.Where(x => x.IsRunning || x.Enabled && x.NextRunAt <= now).ToListAsync(cancellationToken);
        foreach (var entity in schedules)
        {
            entity.IsRunning = false; entity.LastResult = "Missed while panel was offline; not replayed.";
            entity.NextRunAt = ScheduleCalculator.Next(entity.Frequency, Trigger(entity), entity.TimeZone, now);
            if (entity.Frequency.Equals("Once", StringComparison.OrdinalIgnoreCase)) entity.Enabled = false;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static (ScheduleTrigger Trigger, IReadOnlyList<ScheduleActionDto> Actions) Normalize(SaveScheduleRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Frequency) || string.IsNullOrWhiteSpace(request.TimeZone))
            throw PanelProblems.Validation("Schedule name, frequency, and time zone are required.");
        if (request.Name.Trim().Length > 96)
            throw PanelProblems.Validation("Schedule names may contain at most 96 characters.");
        if (request.Name.Any(char.IsControl))
            throw PanelProblems.Validation("Schedule names cannot contain control characters.");
        if (request.TimeZone.Length > 128 || request.TimeZone.Any(char.IsControl))
            throw PanelProblems.Validation("Schedule time zones may contain at most 128 characters and cannot contain control characters.");
        if (request.Cron is { Length: > 256 } || request.Cron?.Any(char.IsControl) == true)
            throw PanelProblems.Validation("Cron expressions may contain at most 256 characters and cannot contain control characters.");
        if (request.DaysOfWeek is { } days &&
            (days.Count > 7 || days.Any(x => x is < 0 or > 6) || days.Distinct().Count() != days.Count))
            throw PanelProblems.Validation("Schedule days must contain at most seven unique values from 0 through 6.");
        if (request.Actions?.Any(x => x is null) == true)
            throw PanelProblems.Validation("Schedule actions cannot be null.");
        if (request.Actions is { Count: > 20 })
            throw PanelProblems.Validation("A schedule needs between one and twenty actions.");
        var actions = request.Actions?.Where(x => !string.IsNullOrWhiteSpace(x!.Action)).ToList() ?? [];
        if (actions.Count == 0 && !string.IsNullOrWhiteSpace(request.Action)) actions.Add(new ScheduleActionDto(request.Action, request.Command));
        if (actions.Count is < 1 or > 20) throw PanelProblems.Validation("A schedule needs between one and twenty actions.");
        foreach (var action in actions)
        {
            if (!new[] { "start", "stop", "restart", "backup", "inventorybackup", "update", "command" }.Contains(action.Action, StringComparer.OrdinalIgnoreCase))
                throw PanelProblems.Validation($"Unknown schedule action '{action.Action}'.");
            if (action.Action.Equals("command", StringComparison.OrdinalIgnoreCase) && (string.IsNullOrWhiteSpace(action.Command) || action.Command.Length > 4096 || action.Command.Any(c => c is '\r' or '\n' or '\0')))
                throw PanelProblems.Validation("Scheduled commands must be one line of at most 4096 characters.");
        }
        if (request.Frequency.Equals("weekly", StringComparison.OrdinalIgnoreCase) && request.DaysOfWeek is not { Count: > 0 })
            throw PanelProblems.Validation("A weekly schedule must select at least one valid day.");
        var trigger = new ScheduleTrigger(request.RunAt, request.IntervalMinutes, request.TimeOfDay, request.DaysOfWeek, request.Cron);
        var next = ScheduleCalculator.Next(request.Frequency, trigger, request.TimeZone, DateTimeOffset.UtcNow);
        if (request.Frequency.Equals("once", StringComparison.OrdinalIgnoreCase) && next is null)
            throw PanelProblems.Validation("A one-time schedule must be in the future.");
        return (trigger, actions);
    }

    private static string NormalizeFrequency(string value) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Trim().ToLowerInvariant());
    private static ScheduleTrigger Trigger(ScheduleEntity entity) => JsonSerializer.Deserialize<ScheduleTrigger>(entity.TriggerJson, JsonOptions) ?? new(null, null, null, null, null);
    private static IReadOnlyList<ScheduleActionDto> Actions(ScheduleEntity entity) => JsonSerializer.Deserialize<List<ScheduleActionDto>>(entity.ActionsJson, JsonOptions) ?? [];
    private static ScheduleDto Map(ScheduleEntity entity)
    {
        var trigger = Trigger(entity); var actions = Actions(entity); var first = actions.FirstOrDefault();
        return new(entity.Id, entity.Name, entity.Frequency, entity.TimeZone, entity.Enabled, trigger.RunAt, trigger.IntervalMinutes,
            trigger.TimeOfDay, trigger.DaysOfWeek, trigger.Cron, actions, entity.NextRunAt, entity.LastRunAt, entity.LastResult, first?.Action, first?.Command);
    }

    private async Task EnsureServerAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var db = await stateFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Servers.AnyAsync(x => x.Id == id, cancellationToken)) throw PanelProblems.NotFound("Server");
    }
}
