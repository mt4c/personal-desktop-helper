using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using PersonalDesktopHelper.Notifications;
using PersonalDesktopHelper.Scheduling;
using PersonalDesktopHelper.Copilot;

namespace PersonalDesktopHelper.Views;

public partial class OptionsWindow : Window
{
    private readonly NotificationService _notifications;
    private readonly Scheduler _scheduler;
    private readonly ChatViewModel _chat;
    private bool _isClosed;

    public OptionsWindow(NotificationService notifications, Scheduler scheduler, string statePath, ChatViewModel chat)
    {
        _notifications = notifications;
        _scheduler = scheduler;
        _chat = chat;
        InitializeComponent();
        CopilotPanel.DataContext = chat;
        SystemPromptPanel.DataContext = chat;
        EditablePromptTextBox.Text = chat.Settings.AdditionalSystemPrompt;
        UpdateSystemPromptPreview();
        CopilotModelComboBox.Text = chat.Settings.Model;
        StoragePathText.Text = $"Settings and tasks are stored in {statePath}";
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

    public void SelectCopilotTab() => OptionsTabs.SelectedItem = CopilotTab;

    private CopilotSettings ReadCopilotSettings() => _chat.Settings with
    {
        Model = CopilotModelComboBox.Text.Trim()
    };

    private void OnSystemPromptTextChanged(object sender, TextChangedEventArgs e) => UpdateSystemPromptPreview();

    private void UpdateSystemPromptPreview()
    {
        if (CombinedPromptTextBox is not null && EditablePromptTextBox is not null)
        {
            CombinedPromptTextBox.Text = SystemPromptDefinition.Compose(_chat.ConstantSystemPrompt, EditablePromptTextBox.Text);
        }
    }

    private async void OnSaveSystemPromptClick(object sender, RoutedEventArgs e)
    {
        await _chat.ApplySettingsAsync(_chat.Settings with { AdditionalSystemPrompt = EditablePromptTextBox.Text });
    }

    private async void OnSignInCopilotClick(object sender, RoutedEventArgs e)
    {
        var settings = ReadCopilotSettings();
        if (settings != _chat.Settings && !await _chat.ApplySettingsAsync(settings))
        {
            return;
        }

        await _chat.SignInAsync();
    }

    private async void OnSignOutCopilotClick(object sender, RoutedEventArgs e) => await _chat.SignOutAsync();

    private void OnOpenSignInPageClick(object sender, RoutedEventArgs e)
    {
        if (_chat.VerificationUri is not { } uri)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            System.Windows.MessageBox.Show(this,
                $"Could not open your browser. Open {uri} manually and enter the displayed code.\n\n{error.Message}",
                "GitHub sign-in", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnSaveCopilotClick(object sender, RoutedEventArgs e)
    {
        await _chat.ApplySettingsAsync(ReadCopilotSettings());
    }

    private async void OnConnectCopilotClick(object sender, RoutedEventArgs e)
    {
        var settings = ReadCopilotSettings();
        if (settings != _chat.Settings && !await _chat.ApplySettingsAsync(settings))
        {
            return;
        }

        await _chat.ConnectAsync();
    }

    private async void OnDisconnectCopilotClick(object sender, RoutedEventArgs e) => await _chat.DisconnectAsync();
    private void OnStopCopilotClick(object sender, RoutedEventArgs e) => _chat.RequestStop();

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
