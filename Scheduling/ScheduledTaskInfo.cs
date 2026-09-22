namespace PersonalDesktopHelper.Scheduling;

public sealed record ScheduledTaskInfo(
    ScheduledTaskState Definition,
    bool IsRunning,
    bool HandlerAvailable,
    string? Error)
{
    public Guid Id => Definition.Id;
    public string Name => Definition.Name;
    public bool IsEnabled => Definition.IsEnabled;
    public bool IsCompleted => Definition.IsCompleted;
    public string Schedule => Definition.Schedule.ToString() ?? Definition.Schedule.GetType().Name;
    public string NextRun => Definition.NextRunAt?.ToLocalTime().ToString("g") ?? "-";

    public string Status => IsRunning ? (IsEnabled ? "Running" : "Running (disabled)")
        : Error is not null ? $"Error: {Error}"
        : IsCompleted ? "Completed"
        : !IsEnabled ? "Disabled"
        : !HandlerAvailable ? "Handler unavailable"
        : "Scheduled";
}
