using System.Text.Json.Serialization;

namespace PersonalDesktopHelper.Scheduling;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OneShotSchedule), "oneShot")]
[JsonDerivedType(typeof(IntervalSchedule), "interval")]
[JsonDerivedType(typeof(CronSchedule), "cron")]
public abstract class TaskSchedule
{
    public abstract DateTimeOffset? GetNextOccurrence(DateTimeOffset after);

    internal virtual DateTimeOffset? GetNextAfterRun(DateTimeOffset scheduledAt, DateTimeOffset now)
    {
        return GetNextOccurrence(now > scheduledAt ? now : scheduledAt);
    }
}
