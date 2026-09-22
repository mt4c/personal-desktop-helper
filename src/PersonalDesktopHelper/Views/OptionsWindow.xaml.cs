using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PersonalDesktopHelper.Notifications;
using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Views;

public partial class OptionsWindow : Window
{
    private readonly NotificationService _notifications;
    private readonly Scheduler _scheduler;
    private bool _isClosed;

    public OptionsWindow(NotificationService notifications, Scheduler scheduler, string statePath)
    {
        _notifications = notifications;
        _scheduler = scheduler;
        InitializeComponent();
        StoragePathText.Text = $"Changes are saved automatically to {statePath}";
        _notifications.PropertyChanged += OnNotificationChanged;
        _scheduler.TasksChanged += OnTasksChanged;
        Closed += (_, _) =>
        {
            _isClosed = true;
            _notifications.PropertyChanged -= OnNotificationChanged;
            _scheduler.TasksChanged -= OnTasksChanged;
        };
        RefreshControls();
    }

    private void RefreshControls()
    {
        if (_isClosed)
        {
            return;
        }

        NotificationsCheckBox.IsChecked = _notifications.IsEnabled;
        var selectedId = (TasksGrid.SelectedItem as ScheduledTaskInfo)?.Id;
        var tasks = _scheduler.GetTasks();
        TasksGrid.ItemsSource = tasks;
        TasksGrid.SelectedItem = tasks.FirstOrDefault(task => task.Id == selectedId);
        UpdateButtons();
    }

    private void QueueRefresh()
    {
        if (!_isClosed && !Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(RefreshControls);
        }
    }

    private void OnNotificationChanged(object? sender, PropertyChangedEventArgs e) => QueueRefresh();
    private void OnTasksChanged(object? sender, EventArgs e) => QueueRefresh();

    private void OnNotificationsClick(object sender, RoutedEventArgs e)
    {
        ApplyChange(() => _notifications.IsEnabled = NotificationsCheckBox.IsChecked == true);
    }

    private void OnTaskSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        if (ToggleTaskButton is null || DeleteTaskButton is null)
        {
            return;
        }

        var task = TasksGrid.SelectedItem as ScheduledTaskInfo;
        ToggleTaskButton.IsEnabled = task is { IsCompleted: false };
        ToggleTaskButton.Content = task?.IsEnabled == false ? "Enable" : "Disable";
        DeleteTaskButton.IsEnabled = task is not null;
    }

    private void OnToggleTaskClick(object sender, RoutedEventArgs e)
    {
        if (TasksGrid.SelectedItem is ScheduledTaskInfo task)
        {
            ApplyChange(() => _scheduler.SetEnabled(task.Id, !task.IsEnabled));
        }
    }

    private void OnDeleteTaskClick(object sender, RoutedEventArgs e)
    {
        if (TasksGrid.SelectedItem is ScheduledTaskInfo task &&
            System.Windows.MessageBox.Show(this, $"Delete '{task.Name}'?", "Delete scheduled task",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            ApplyChange(() => _scheduler.DeleteTask(task.Id));
        }
    }

    private void ApplyChange(Action change)
    {
        try
        {
            change();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidOperationException or KeyNotFoundException)
        {
            Trace.TraceError("Could not apply options change: {0}", error);
            System.Windows.MessageBox.Show(this, $"The change could not be saved.\n\n{error.Message}",
                "Options error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            RefreshControls();
        }
    }
}
