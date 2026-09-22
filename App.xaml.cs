using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using PersonalDesktopHelper.Notifications;
using PersonalDesktopHelper.Persistence;
using PersonalDesktopHelper.Scheduling;
using Forms = System.Windows.Forms;

namespace PersonalDesktopHelper;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _applicationIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private MainWindow? _mainWindow;
    private OptionsWindow? _optionsWindow;
    private NotificationService? _notifications;
    private Scheduler? _scheduler;
    private bool _isQuitting;
    private readonly string _statePath;
    private Forms.ToolStripMenuItem? _notificationsItem;

    public App() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalDesktopHelper", "state.json"))
    {
    }

    public App(string statePath)
    {
        _statePath = statePath;
    }

    public INotificationService Notifications => _notifications
        ?? throw new InvalidOperationException("The application has not started.");

    public Scheduler Scheduler => _scheduler
        ?? throw new InvalidOperationException("The application has not started.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var skipped = InitializeServices();
            ShowMainWindow();
            await SkippedTaskNotification.SendAsync(Notifications, skipped);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Windows.MessageBox.Show(
                $"Unable to load or save application settings at {_statePath}.\n\n{error.Message}",
                "Settings error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private IReadOnlyList<ScheduledTaskState> InitializeServices()
    {
        var store = new JsonStateStore(_statePath);
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Options", null, (_, _) => ShowOptionsWindow());
        _notificationsItem = new Forms.ToolStripMenuItem("Notifications")
        {
            CheckOnClick = true,
            Checked = store.State.NotificationsEnabled
        };
        _notificationsItem.Click += OnNotificationsClick;
        _trayMenu.Items.Add(_notificationsItem);
        _trayMenu.Items.Add("Quit", null, async (_, _) => await QuitAsync());

        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/PersonalDesktopHelper;component/Assets/FIRE.ICO")).Stream;
        using var icon = new System.Drawing.Icon(iconStream);
        _applicationIcon = (System.Drawing.Icon)icon.Clone();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "Personal Desktop Helper",
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                ShowMainWindow();
            }
        };

        _notifications = new NotificationService(
            new WindowsTrayNotificationSender(_trayIcon, Dispatcher),
            store.State.NotificationsEnabled, store.SetNotificationsEnabled);
        _notifications.PropertyChanged += OnNotificationStateChanged;
        _scheduler = new Scheduler((name, exception) =>
            Trace.TraceError("Scheduled task '{0}' failed: {1}", name, exception),
            persistTasks: store.SetTasks);
        var currentTimeTask = new CurrentTimeNotificationTask(Notifications);
        Scheduler.RegisterHandler("current-time-notification", currentTimeTask.RunAsync);
        var skipped = Scheduler.RestoreTasks(store.State.Tasks);
        if (store.IsNew)
        {
            Scheduler.AddTask(
                "Current time notification", "current-time-notification",
                new IntervalSchedule(TimeSpan.FromMinutes(1)));
        }

        _trayIcon.Visible = true;
        return skipped;
    }

    private void OnNotificationStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_isQuitting && _notificationsItem is not null)
                {
                    _notificationsItem.Checked = Notifications.IsEnabled;
                }
            });
        }
    }

    private void OnNotificationsClick(object? sender, EventArgs e)
    {
        try
        {
            Notifications.IsEnabled = _notificationsItem!.Checked;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _notificationsItem!.Checked = Notifications.IsEnabled;
            System.Windows.MessageBox.Show(
                $"The notification setting could not be saved.\n\n{error.Message}",
                "Settings error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task QuitAsync()
    {
        if (_isQuitting)
        {
            return;
        }

        _isQuitting = true;
        if (_trayMenu is not null)
        {
            _trayMenu.Enabled = false;
        }

        try
        {
            if (_scheduler is not null)
            {
                await _scheduler.DisposeAsync();
            }
        }
        finally
        {
            Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Closed += (_, _) => _mainWindow = null;
            MainWindow = _mainWindow;
        }

        ShowAndActivate(_mainWindow);
    }

    private void ShowOptionsWindow()
    {
        if (_optionsWindow is null)
        {
            _optionsWindow = new OptionsWindow(_notifications!, Scheduler, _statePath);
            _optionsWindow.Closed += (_, _) => _optionsWindow = null;
        }

        ShowAndActivate(_optionsWindow);
    }

    private static void ShowAndActivate(Window window)
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _scheduler?.RequestStop();
        if (_notifications is not null)
        {
            _notifications.PropertyChanged -= OnNotificationStateChanged;
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _trayMenu?.Dispose();
        _applicationIcon?.Dispose();
        base.OnExit(e);
    }
}
