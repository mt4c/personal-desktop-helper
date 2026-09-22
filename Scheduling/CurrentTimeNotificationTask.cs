using PersonalDesktopHelper.Notifications;

namespace PersonalDesktopHelper.Scheduling;

public sealed class CurrentTimeNotificationTask(
    INotificationService notifications,
    TimeProvider? timeProvider = null)
{
    private readonly INotificationService _notifications = notifications
        ?? throw new ArgumentNullException(nameof(notifications));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task RunAsync(CancellationToken cancellationToken)
    {
        return _notifications.NotifyAsync(
            "Current time",
            $"The current time is {_timeProvider.GetLocalNow():yyyy-MM-dd HH:mm}.",
            cancellationToken);
    }
}
