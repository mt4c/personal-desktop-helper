using Cronos;
using System.Text.Json.Serialization;

namespace PersonalDesktopHelper.Scheduling;

public sealed class CronSchedule : TaskSchedule
{
    private readonly CronExpression _expression;
    private readonly TimeZoneInfo _timeZone;

    public CronSchedule(string expression, TimeZoneInfo? timeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        _expression = CronExpression.Parse(expression, CronFormat.Standard);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
        Expression = expression;
    }

    [JsonConstructor]
    public CronSchedule(string expression, string timeZoneId)
        : this(expression, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId))
    {
    }

    public string Expression { get; }
    public string TimeZoneId => _timeZone.Id;

    public override string ToString() => $"Cron: {Expression} ({TimeZoneId})";

    public override DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        return _expression.GetNextOccurrence(after, _timeZone, inclusive: false);
    }
}
