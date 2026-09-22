using System.IO;
using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Persistence;

public sealed record ApplicationState
{
    public required int Version { get; init; }
    public required bool NotificationsEnabled { get; init; }
    public required IReadOnlyList<ScheduledTaskState> Tasks { get; init; }

    public static ApplicationState Default => new()
    {
        Version = 1,
        NotificationsEnabled = true,
        Tasks = []
    };

    public void Validate()
    {
        if (Version != 1)
        {
            throw new InvalidDataException($"Unsupported settings version: {Version}.");
        }

        if (Tasks is null || Tasks.Any(task => task is null))
        {
            throw new InvalidDataException("The scheduled tasks list is missing or contains a null entry.");
        }

        foreach (var task in Tasks)
        {
            task.Validate();
        }

        if (Tasks.Select(task => task.Id).Distinct().Count() != Tasks.Count)
        {
            throw new InvalidDataException("The scheduled tasks list contains duplicate task IDs.");
        }
    }
}
