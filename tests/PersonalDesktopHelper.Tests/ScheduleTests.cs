using Cronos;
using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Tests;

public sealed class ScheduleTests
{
    [Theory]
    [InlineData(-60)]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(90)]
    public void IntervalsMustBePositiveWholeMinutes(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IntervalSchedule(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void IntervalStartsOnAMinuteBoundary()
    {
        var schedule = new IntervalSchedule(TimeSpan.FromMinutes(5));
        var now = new DateTimeOffset(2026, 9, 22, 12, 0, 45, TimeSpan.FromHours(8));

        Assert.Equal(new DateTimeOffset(2026, 9, 22, 4, 5, 0, TimeSpan.Zero), schedule.GetNextOccurrence(now));
    }

    [Fact]
    public void OneShotRunsOnlyBeforeItsSpecifiedMinute()
    {
        var runAt = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.FromHours(8));
        var schedule = new OneShotSchedule(runAt);

        Assert.Equal(runAt, schedule.GetNextOccurrence(runAt.AddMinutes(-1)));
        Assert.Null(schedule.GetNextOccurrence(runAt));
        Assert.Null(schedule.GetNextOccurrence(runAt.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() => new OneShotSchedule(runAt.AddSeconds(1)));
        Assert.Throws<ArgumentException>(() => new OneShotSchedule(runAt.AddTicks(1)));
    }

    [Theory]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("not a cron expression")]
    public void CronRejectsInvalidOrSecondBasedExpressions(string expression)
    {
        Assert.Throws<CronFormatException>(() => new CronSchedule(expression));
    }

    [Fact]
    public void CronSupportsListsRangesStepsAndAnExplicitTimeZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var schedule = new CronSchedule("*/15 9,17 * * 1-5", zone);
        var fridayEvening = new DateTimeOffset(2026, 9, 25, 17, 45, 0, TimeSpan.FromHours(8));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 28, 9, 0, 0, TimeSpan.FromHours(8)),
            schedule.GetNextOccurrence(fridayEvening));
    }

    [Fact]
    public void CronHandlesTheSpringDaylightSavingGap()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var schedule = new CronSchedule("30 2 * * *", zone);
        var beforeGap = new DateTimeOffset(2026, 3, 8, 6, 59, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero),
            schedule.GetNextOccurrence(beforeGap));
    }

    [Fact]
    public void DailyCronDoesNotRepeatDuringTheAutumnDaylightSavingOverlap()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var schedule = new CronSchedule("30 1 * * *", zone);
        var firstOccurrence = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 11, 2, 6, 30, 0, TimeSpan.Zero),
            schedule.GetNextOccurrence(firstOccurrence));
    }
}
