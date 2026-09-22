namespace PersonalDesktopHelper.Scheduling;

public sealed class IntervalSchedule : TaskSchedule
{
    public IntervalSchedule(TimeSpan interval)
    {
        if (interval < TimeSpan.FromMinutes(1) || interval.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "The interval must be a positive whole number of minutes.");
        }

        Interval = interval;
    }

    public TimeSpan Interval { get; }

    public override string ToString() => $"Every {Interval.TotalMinutes:0} minute(s)";

    public override DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        var minuteTicks = after.UtcTicks - after.UtcTicks % TimeSpan.TicksPerMinute;
        return new DateTimeOffset(minuteTicks, TimeSpan.Zero) + Interval;
    }

    internal override DateTimeOffset? GetNextAfterRun(DateTimeOffset scheduledAt, DateTimeOffset now)
    {
        var elapsedTicks = Math.Max(0, (now - scheduledAt).Ticks);
        var intervals = elapsedTicks / Interval.Ticks + 1;
        return scheduledAt.AddTicks(checked(intervals * Interval.Ticks));
    }
}
