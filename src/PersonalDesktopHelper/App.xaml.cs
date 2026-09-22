using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using PersonalDesktopHelper.Logging;
using PersonalDesktopHelper.Notifications;
using PersonalDesktopHelper.Persistence;
using PersonalDesktopHelper.Scheduling;
using PersonalDesktopHelper.Views;
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
    private DailyFileTraceListener? _fileLog;
    private readonly CancellationTokenSource _cleanupCancellation = new();
    private Task? _logCleanup;

    public App() : this(Path.Combine(AppContext.BaseDirectory, "state.json"))
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
            InitializeLogging();
            var skipped = InitializeServices();
            ShowMainWindow();
            await SkippedTaskNotification.SendAsync(Notifications, skipped);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            System.Windows.MessageBox.Show(
                $"Unable to initialize the application using {_statePath}.\n\n{error.Message}",
                "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void InitializeLogging()
    {
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_statePath))!, "logs");
        _fileLog = new DailyFileTraceListener(directory, error =>
        {
            if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.BeginInvoke(() => System.Windows.MessageBox.Show(
                    $"Unable to write application logs in {directory}.\n\n{error.Message}",
                    "Logging error", MessageBoxButton.OK, MessageBoxImage.Error));
            }
        });
        Trace.Listeners.Add(_fileLog);
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        Trace.TraceInformation("Application starting.");
        _logCleanup = CleanupLogsAsync(directory, _cleanupCancellation.Token);
    }

    private static async Task CleanupLogsAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            var result = await LogRetention.CleanupAsync(
                directory, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            Trace.TraceInformation("Startup log cleanup removed {0} file(s).", result.DeletedCount);
            foreach (var failure in result.Failures)
            {
                Trace.TraceWarning("Could not remove expired log '{0}': {1}", failure.Path, failure.Error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Trace.TraceInformation("Startup log cleanup cancelled during shutdown.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("Startup log cleanup failed: {0}", error);
        }
    }

    private void OnDispatcherException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.TraceError("Unhandled UI exception: {0}", e.Exception);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Trace.TraceError("Unhandled application exception: {0}", e.ExceptionObject);
    }

    private IReadOnlyList<ScheduledTaskState> InitializeServices()
    {
        var store = new JsonStateStore(_statePath);
        Trace.TraceInformation("Loaded application state. New profile: {0}; saved tasks: {1}.", store.IsNew, store.State.Tasks.Count);
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
        foreach (var task in skipped)
        {
            Trace.TraceWarning("Skipped missed run for task '{0}' ({1}).", task.Name, task.Id);
        }
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
            Trace.TraceError("Could not save notification setting: {0}", error);
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
        Trace.TraceInformation("Quit requested.");
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

            _cleanupCancellation.Cancel();
            if (_logCleanup is not null)
            {
                await _logCleanup;
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
        _cleanupCancellation.Cancel();
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
        Trace.TraceInformation("Application stopped.");
        DispatcherUnhandledException -= OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        if (_fileLog is not null)
        {
            Trace.Listeners.Remove(_fileLog);
            _fileLog.Dispose();
        }

        _cleanupCancellation.Dispose();
        base.OnExit(e);
    }
}
