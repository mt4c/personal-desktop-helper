using System.Reflection;
using System.Windows;
using System.Windows.Threading;
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
}
