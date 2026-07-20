using McPanel.Api.Services;

namespace McPanel.Api.Tests;

public sealed class ScheduleCalculatorTests
{
    [Fact]
    public void Daily_uses_requested_timezone()
    {
        var after = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
        var next = ScheduleCalculator.Next("Daily", new ScheduleTrigger(null, null, "08:30", null, null), "America/New_York", after);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 13, 30, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Weekly_honors_day_selection()
    {
        var after = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero); // Monday
        var next = ScheduleCalculator.Next("Weekly", new ScheduleTrigger(null, null, "09:00", [(int)DayOfWeek.Wednesday], null), "UTC", after);
        Assert.Equal(new DateTimeOffset(2026, 1, 7, 9, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Cron_supports_lists_ranges_and_steps()
    {
        var after = new DateTimeOffset(2026, 1, 5, 12, 1, 0, TimeSpan.Zero);
        var next = ScheduleCalculator.Next("Cron", new ScheduleTrigger(null, null, null, null, "*/15 12 * * 1-5"), "UTC", after);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 12, 15, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void Once_does_not_replay_past_time()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Null(ScheduleCalculator.Next("Once", new ScheduleTrigger(now.AddMinutes(-1), null, null, null, null), "UTC", now));
    }
}
