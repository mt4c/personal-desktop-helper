namespace PersonalDesktopHelper.Notifications;

public interface INotificationService
{
    bool IsEnabled { get; set; }

    Task<bool> NotifyAsync(string title, string message, CancellationToken cancellationToken = default);
}
