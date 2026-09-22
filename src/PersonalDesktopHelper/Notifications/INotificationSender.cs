namespace PersonalDesktopHelper.Notifications;

public interface INotificationSender
{
    Task SendAsync(string title, string message, CancellationToken cancellationToken = default);
}
