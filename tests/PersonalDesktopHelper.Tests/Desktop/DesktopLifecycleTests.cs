using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PersonalDesktopHelper.Copilot;
using PersonalDesktopHelper.Notifications;
using PersonalDesktopHelper.Scheduling;
using PersonalDesktopHelper.Views;
using Forms = System.Windows.Forms;

namespace PersonalDesktopHelper.Tests;

public sealed class DesktopLifecycleTests
{
    [Fact]
    public async Task TrayToggleWindowsAndQuitAreWiredToTheServices()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var stateDirectory = Path.Combine(Path.GetTempPath(), "PersonalDesktopHelper.Tests", Guid.NewGuid().ToString("N"));
            try
            {
                var app = new App(Path.Combine(stateDirectory, "state.json"));
                app.InitializeComponent();
                Exception? failure = null;
                Forms.NotifyIcon? tray = null;
                Task? pending = null;
                app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    try
                    {
                        tray = (Forms.NotifyIcon)typeof(App)
                            .GetField("_trayIcon", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(app)!;
                        var menu = tray.ContextMenuStrip!;
                        Assert.Equal(["Options", "Notifications", "Quit"],
                            menu.Items.Cast<Forms.ToolStripItem>().Select(item => item.Text));
                        Assert.True(tray.Visible);
                        Assert.True(app.MainWindow.IsVisible);
                        Assert.Same(app.Chat, app.MainWindow.DataContext);
                        Assert.Empty(app.Scheduler.GetTasks());
                        app.Scheduler.AddTask(
                            "Test time notification", "current-time-notification",
                            new IntervalSchedule(TimeSpan.FromHours(1)));
                        VerifyCopilotControls(app);
                        app.Chat.Prompt = "Draft retained while the main window is closed";
                        var toggle = Assert.IsType<Forms.ToolStripMenuItem>(menu.Items[1]);
                        Assert.True(toggle.Checked);
                        toggle.PerformClick();
                        Assert.False(app.Notifications.IsEnabled);
                        toggle.PerformClick();
                        Assert.True(app.Notifications.IsEnabled);

                        menu.Items[0].PerformClick();
                        menu.Items[0].PerformClick();
                        var options = Assert.Single(app.Windows.OfType<OptionsWindow>());
                        options.SelectCopilotTab();
                        var model = Assert.IsType<System.Windows.Controls.ComboBox>(options.FindName("CopilotModelComboBox"));
                        model.Text = "test-model";
                        var saveCopilot = Assert.IsType<System.Windows.Controls.Button>(options.FindName("SaveCopilotSettingsButton"));
                        saveCopilot.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Assert.Equal("test-model", app.Chat.Settings.Model);
                        Assert.Equal("test-model", new Persistence.JsonStateStore(Path.Combine(stateDirectory, "state.json")).State.Copilot.Model);
                        var constantPrompt = Assert.IsType<System.Windows.Controls.TextBox>(options.FindName("ConstantPromptTextBox"));
                        Assert.True(constantPrompt.IsReadOnly);
                        Assert.Contains("scheduler_create_task", constantPrompt.Text);
                        var editablePrompt = Assert.IsType<System.Windows.Controls.TextBox>(options.FindName("EditablePromptTextBox"));
                        editablePrompt.Text = "Use brief replies.";
                        var savePrompt = Assert.IsType<System.Windows.Controls.Button>(options.FindName("SaveSystemPromptButton"));
                        savePrompt.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Assert.Equal("Use brief replies.", app.Chat.Settings.AdditionalSystemPrompt);
                        var combinedPrompt = Assert.IsType<System.Windows.Controls.TextBox>(options.FindName("CombinedPromptTextBox"));
                        Assert.Equal(app.Chat.EffectiveSystemPrompt, combinedPrompt.Text);
                        Assert.True(combinedPrompt.IsReadOnly);
                        model.Text = "another-model";
                        saveCopilot.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Assert.Equal("Use brief replies.", new Persistence.JsonStateStore(Path.Combine(stateDirectory, "state.json")).State.Copilot.AdditionalSystemPrompt);
                        var checkbox = Assert.IsType<System.Windows.Controls.CheckBox>(options.FindName("NotificationsCheckBox"));
                        checkbox.IsChecked = false;
                        checkbox.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Assert.False(app.Notifications.IsEnabled);
                        Assert.False(new Persistence.JsonStateStore(Path.Combine(stateDirectory, "state.json")).State.NotificationsEnabled);
                        app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                        Assert.False(toggle.Checked);
                        var grid = Assert.IsType<System.Windows.Controls.DataGrid>(options.FindName("TasksGrid"));
                        grid.SelectedIndex = 0;
                        var disable = Assert.IsType<System.Windows.Controls.Button>(options.FindName("ToggleTaskButton"));
                        disable.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                        Assert.False(Assert.Single(app.Scheduler.GetTasks()).IsEnabled);
                        options.Close();
                        app.MainWindow.Close();
                        Assert.Empty(app.Windows.Cast<Window>());
                        Assert.False(app.Dispatcher.HasShutdownStarted);

                        typeof(Forms.NotifyIcon)
                            .GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(tray, [new Forms.MouseEventArgs(Forms.MouseButtons.Left, 1, 0, 0, 0)]);
                        Assert.True(app.MainWindow.IsVisible);
                        Assert.Same(app.Chat, app.MainWindow.DataContext);
                        Assert.Equal("Draft retained while the main window is closed", app.Chat.Prompt);
                        pending = app.Scheduler.AddTask(
                            "Shutdown probe",
                            new IntervalSchedule(TimeSpan.FromHours(1)),
                            _ => Task.CompletedTask);
                        menu.Items[2].PerformClick();
                    }
                    catch (Exception error)
                    {
                        failure = error;
                        app.Shutdown();
                    }
                }));

                app.Run();
                if (failure is not null)
                {
                    throw failure;
                }

                Assert.NotNull(tray);
                Assert.False(tray.Visible);
                Assert.NotNull(pending);
                Assert.True(pending.IsCompletedSuccessfully);
                var log = File.ReadAllText(Assert.Single(Directory.GetFiles(Path.Combine(stateDirectory, "logs"), "*.log")));
                Assert.Contains("Application starting.", log);
                Assert.Contains("Application stopped.", log);
                Assert.Contains("Startup log cleanup", log);
                completed.SetResult();
            }
            catch (Exception error)
            {
                completed.SetException(error);
            }
            finally
            {
                if (Directory.Exists(stateDirectory))
                {
                    Directory.Delete(stateDirectory, recursive: true);
                }
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static void VerifyCopilotControls(App app)
    {
        using var directory = new TemporaryDirectory();
        var store = new Persistence.JsonStateStore(directory.StatePath);
        var connection = new ChatViewModelTests.FakeConnection
        {
            IsSignedIn = false,
            Models = ["test-model", "other-model"]
        };
        var failModelSave = false;
        var chat = new ChatViewModel(connection, store.State.Copilot, settings =>
        {
            if (failModelSave)
            {
                throw new IOException("Disk unavailable.");
            }
            store.SetCopilotSettings(settings);
        }, action => action());
        var optionsOpened = 0;
        var main = new MainWindow(chat, () => optionsOpened++);
        var options = new OptionsWindow((NotificationService)app.Notifications, app.Scheduler, "test-state.json", chat);
        try
        {
            main.Show();
            options.Show();
            options.SelectCopilotTab();
            var setup = Assert.IsType<System.Windows.Controls.Button>(main.FindName("SetUpCopilotButton"));
            var banner = Assert.IsType<System.Windows.Controls.Border>(main.FindName("CopilotSetupBanner"));
            var signIn = Button("SignInCopilotButton");
            var signOut = Button("SignOutCopilotButton");
            var connect = Button("ConnectCopilotButton");
            var disconnect = Button("DisconnectCopilotButton");

            CheckActions(true, false, false, false);
            Assert.Equal("Sign in", setup.Content);
            Assert.Equal(Visibility.Visible, banner.Visibility);
            Assert.Contains("Not signed in", MainText("SignInStatusText"));
            Assert.Contains("Not signed in", OptionsText("CopilotSignInStatusText"));
            Click(setup);
            Assert.Equal(1, optionsOpened);

            Click(signIn);
            CheckActions(false, true, false, true);
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
            Assert.Contains("Signed in", MainText("SignInStatusText"));
            Assert.Equal("Copilot: Connected", MainText("ConnectionStatusText"));
            Assert.Equal("Copilot: Connected", OptionsText("CopilotConnectionStatusText"));

            Click(disconnect);
            CheckActions(false, true, true, false);
            Assert.Equal("Connect", setup.Content);
            Assert.True(setup.IsEnabled);
            Assert.Equal(Visibility.Visible, banner.Visibility);
            Assert.Equal("Copilot: Not connected", MainText("ConnectionStatusText"));
            Click(setup);
            CheckActions(false, true, false, true);
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
            Assert.Equal(2, connection.ConnectCount);

            VerifyReplyWaitingState("reply");
            VerifyReplyWaitingState("stop");
            VerifyReplyWaitingState("failure");

            var model = Assert.IsType<System.Windows.Controls.ComboBox>(options.FindName("CopilotModelComboBox"));
            model.SelectedItem = "test-model";
            CheckActions(false, true, false, true);
            Assert.Equal("test-model", new Persistence.JsonStateStore(directory.StatePath).State.Copilot.Model);
            Assert.Equal("test-model", connection.LastConnectSettings!.Model);
            Assert.Equal(3, connection.ConnectCount);

            failModelSave = true;
            model.SelectedItem = "other-model";
            CheckActions(false, true, false, true);
            Assert.Equal("test-model", model.Text);
            Assert.Equal("test-model", new Persistence.JsonStateStore(directory.StatePath).State.Copilot.Model);
            Assert.Equal("Disk unavailable.", chat.ErrorMessage);
            Assert.Equal(3, connection.ConnectCount);
            failModelSave = false;

            var reopened = new OptionsWindow((NotificationService)app.Notifications, app.Scheduler, directory.StatePath, chat);
            try
            {
                Assert.Equal("test-model",
                    Assert.IsType<System.Windows.Controls.ComboBox>(reopened.FindName("CopilotModelComboBox")).Text);
                Assert.Equal(3, connection.ConnectCount);
            }
            finally
            {
                reopened.Close();
            }

            Click(signOut);
            CheckActions(true, false, false, false);
            Assert.Equal("Sign in", setup.Content);
            Assert.Contains("Not signed in", OptionsText("CopilotSignInStatusText"));

            main.Width = main.MinWidth;
            main.Height = main.MinHeight;
            main.UpdateLayout();
            AssertFits(main, setup);
            AssertFits(main, Assert.IsType<System.Windows.Controls.TextBox>(main.FindName("PromptTextBox")));
            options.Width = options.MinWidth;
            options.Height = options.MinHeight;
            var tabs = Assert.IsType<System.Windows.Controls.TabControl>(options.FindName("OptionsTabs"));
            foreach (System.Windows.Controls.TabItem tab in tabs.Items)
            {
                tabs.SelectedItem = tab;
                tab.Focus();
                options.UpdateLayout();
                var surface = Assert.IsType<System.Windows.Controls.Border>(tab.Template.FindName("TabSurface", tab));
                var panel = Assert.IsType<TabPanel>(VisualTreeHelper.GetParent(tab));
                var bounds = surface.TransformToAncestor(panel).TransformBounds(new Rect(surface.RenderSize));
                var slot = LayoutInformation.GetLayoutSlot(tab);
                var clip = VisualTreeHelper.GetClip(tab);
                if (clip is not null)
                {
                    var headerBounds = surface.TransformToAncestor(tab).TransformBounds(new Rect(surface.RenderSize));
                    Assert.True(clip.Bounds.Contains(headerBounds),
                        $"The '{tab.Header}' tab clips its outline: surface {headerBounds}, clip {clip.Bounds}.");
                }
                Assert.True(bounds.Left >= slot.Left && bounds.Top >= slot.Top &&
                    bounds.Right <= Math.Min(slot.Right, panel.ActualWidth) &&
                    bounds.Bottom <= Math.Min(slot.Bottom, panel.ActualHeight),
                    $"The '{tab.Header}' tab outline is clipped: surface {bounds}, slot {slot}, panel {panel.RenderSize}.");
            }
            tabs.SelectedIndex = 1;
            options.UpdateLayout();
            var tasksGrid = Assert.IsType<System.Windows.Controls.DataGrid>(options.FindName("TasksGrid"));
            Assert.True(tasksGrid.ActualHeight >= 160, $"Scheduler grid height is {tasksGrid.ActualHeight}.");
            tabs.SelectedItem =
                options.FindName("SystemPromptTab");
            options.UpdateLayout();
            AssertFits(options, Assert.IsType<System.Windows.Controls.TextBox>(options.FindName("EditablePromptTextBox")));
            AssertFits(options, Assert.IsType<System.Windows.Controls.Button>(options.FindName("SaveSystemPromptButton")));
            var editablePrompt = Assert.IsType<System.Windows.Controls.TextBox>(options.FindName("EditablePromptTextBox"));
            var combinedPrompt = Assert.IsType<System.Windows.Controls.TextBox>(options.FindName("CombinedPromptTextBox"));
            Assert.True(editablePrompt.ActualHeight >= 40, $"Instruction editor height is {editablePrompt.ActualHeight}.");
            Assert.True(combinedPrompt.ActualHeight >= 40, $"Prompt preview height is {combinedPrompt.ActualHeight}.");
        }
        finally
        {
            options.Close();
            main.Close();
            chat.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        System.Windows.Controls.Button Button(string name) =>
            Assert.IsType<System.Windows.Controls.Button>(options.FindName(name));
        string MainText(string name) => Assert.IsType<System.Windows.Controls.TextBlock>(main.FindName(name)).Text;
        string OptionsText(string name) => Assert.IsType<System.Windows.Controls.TextBlock>(options.FindName(name)).Text;
        static void Click(System.Windows.Controls.Button button) =>
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        static void AssertFits(Window window, FrameworkElement element)
        {
            Assert.True(element.ActualWidth > 0);
            Assert.True(element.ActualHeight >= 24);
            var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
            Assert.True(bounds.Left >= 0 && bounds.Top >= 0 &&
                bounds.Right <= window.ActualWidth && bounds.Bottom <= window.ActualHeight,
                $"'{element.Name}' is outside the window at its minimum size.");
        }
        void VerifyReplyWaitingState(string outcome)
        {
            var response = new TaskCompletionSource<string>();
            connection.SendHandler = (_, _, token) => response.Task.WaitAsync(token);
            chat.Prompt = "Hello";
            var sending = chat.SendAsync();
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            main.UpdateLayout();
            var messages = Assert.IsType<System.Windows.Controls.ItemsControl>(main.FindName("TranscriptMessages"));
            var presenter = Assert.IsType<System.Windows.Controls.ContentPresenter>(
                messages.ItemContainerGenerator.ContainerFromIndex(chat.Messages.Count - 1));
            var waiting = Assert.IsType<System.Windows.Controls.StackPanel>(
                messages.ItemTemplate.FindName("ResponseWaitingIndicator", presenter));
            var progress = Assert.IsType<System.Windows.Controls.ProgressBar>(
                messages.ItemTemplate.FindName("ResponseWaitingProgress", presenter));
            var content = Assert.IsType<System.Windows.Controls.TextBox>(
                messages.ItemTemplate.FindName("ReplyContentText", presenter));
            Assert.True(waiting.IsVisible, "The pending reply should display the waiting indicator.");
            Assert.True(progress.IsIndeterminate);
            Assert.True(progress.ActualWidth > 0 && progress.ActualHeight > 0);
            Assert.False(content.IsVisible, "The pending reply should not show an empty text box.");

            switch (outcome)
            {
                case "reply":
                    response.SetResult("Here is your reply.");
                    break;
                case "stop":
                    chat.RequestStop();
                    break;
                case "failure":
                    response.SetException(new CopilotException("Network unavailable."));
                    break;
            }
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            main.UpdateLayout();
            Assert.True(sending.IsCompleted, "The test response should finish before checking the indicator.");
            Assert.Equal(outcome == "reply", sending.GetAwaiter().GetResult());
            Assert.Equal(Visibility.Collapsed, waiting.Visibility);
            Assert.False(progress.IsIndeterminate);
            Assert.True(content.IsVisible);
            Assert.Equal(outcome switch
            {
                "reply" => "Here is your reply.",
                "stop" => "(Stopped.)",
                _ => "(No response received.)"
            }, content.Text);
        }
        void CheckActions(bool canSignIn, bool canSignOut, bool canConnect, bool canDisconnect)
        {
            app.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Assert.Equal(canSignIn, Button("SignInCopilotButton").IsEnabled);
            Assert.Equal(canSignOut, Button("SignOutCopilotButton").IsEnabled);
            Assert.Equal(canConnect, Button("ConnectCopilotButton").IsEnabled);
            Assert.Equal(canDisconnect, Button("DisconnectCopilotButton").IsEnabled);
        }
    }
}
