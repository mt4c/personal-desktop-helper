using System.ComponentModel;

namespace PersonalDesktopHelper.Notifications;

public sealed class NotificationService(
    INotificationSender sender,
    bool isEnabled = true,
    Action<bool>? persistEnabled = null) : INotificationService, INotifyPropertyChanged
{
    private readonly INotificationSender _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    private readonly object _gate = new();
    private volatile bool _isEnabled = isEnabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            lock (_gate)
            {
                if (_isEnabled == value)
                {
                    return;
                }

                persistEnabled?.Invoke(value);
                _isEnabled = value;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public Task NotifyAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();

        return IsEnabled
            ? _sender.SendAsync(title, message, cancellationToken)
            : Task.CompletedTask;
    }
}
