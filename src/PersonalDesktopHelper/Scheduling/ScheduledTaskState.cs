using System.IO;

namespace PersonalDesktopHelper.Scheduling;

public sealed record ScheduledTaskState
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string HandlerId { get; init; }
    public required TaskSchedule Schedule { get; init; }
    public required bool IsEnabled { get; init; }
    public required bool IsCompleted { get; init; }
    public required DateTimeOffset? NextRunAt { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) ||
            string.IsNullOrWhiteSpace(HandlerId) || Schedule is null || Parameters is null ||
            Parameters.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            throw new InvalidDataException("A scheduled task must have an ID, name, handler ID and schedule.");
        }

        if (IsCompleted != (NextRunAt is null) ||
            NextRunAt is { } next && next.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            throw new InvalidDataException($"Task '{Name}' has an invalid next-run time or completion state.");
        }

        if (!IsCompleted && Schedule is OneShotSchedule once && NextRunAt != once.RunAt)
        {
            throw new InvalidDataException($"Task '{Name}' must run at its one-shot scheduled time.");
        }
    }
}
