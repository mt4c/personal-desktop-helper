using PersonalDesktopHelper.Notifications;

namespace PersonalDesktopHelper.Scheduling;

public static class SkippedTaskNotification
{
    public static Task SendAsync(INotificationService notifications, IReadOnlyList<ScheduledTaskState> skipped)
    {
        if (skipped.Count == 0)
        {
            return Task.CompletedTask;
        }

        var names = string.Join(", ", skipped.Take(3).Select(task =>
            task.Name.Length > 45 ? task.Name[..42] + "..." : task.Name));
        var more = skipped.Count > 3 ? $" (+{skipped.Count - 3} more)" : "";
        return notifications.NotifyAsync(
            "Skipped scheduled tasks",
            $"Missed runs for {skipped.Count} task(s) were skipped: {names}{more}. See Options > Scheduler.");
    }
}
