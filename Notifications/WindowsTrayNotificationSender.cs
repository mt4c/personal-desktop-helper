using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PersonalDesktopHelper.Notifications;

public sealed class WindowsTrayNotificationSender : INotificationSender
{
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Dispatcher _dispatcher;

    public WindowsTrayNotificationSender(Forms.NotifyIcon trayIcon, Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(trayIcon);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _trayIcon = trayIcon;
        _dispatcher = dispatcher;
    }

    public Task SendAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        cancellationToken.ThrowIfCancellationRequested();

        return _dispatcher.InvokeAsync(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _trayIcon.ShowBalloonTip(5000, title, message, Forms.ToolTipIcon.Info);
            },
            DispatcherPriority.Normal,
            cancellationToken).Task;
    }
}
