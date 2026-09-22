namespace PersonalDesktopHelper.Scheduling;

public sealed class OneShotSchedule : TaskSchedule
{
    public OneShotSchedule(DateTimeOffset runAt)
    {
        if (runAt.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new ArgumentException("The scheduled time must be aligned to a whole minute.", nameof(runAt));
        }

        RunAt = runAt.ToUniversalTime();
    }

    public DateTimeOffset RunAt { get; }

    public override string ToString() => $"Once at {RunAt.ToLocalTime():g}";

    public override DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        return RunAt > after ? RunAt : null;
    }
}
