namespace PersonalDesktopHelper.Notifications;

public interface INotificationService
{
    bool IsEnabled { get; set; }

    Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default);
}
