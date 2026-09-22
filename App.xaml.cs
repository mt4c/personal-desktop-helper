using System.Windows;
using Forms = System.Windows.Forms;

namespace PersonalDesktopHelper;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _applicationIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private MainWindow? _mainWindow;
    private OptionsWindow? _optionsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("Options", null, (_, _) => ShowOptionsWindow());
        _trayMenu.Items.Add("Quit", null, (_, _) => Shutdown());

        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/FIRE.ICO")).Stream;
        using var icon = new System.Drawing.Icon(iconStream);
        _applicationIcon = (System.Drawing.Icon)icon.Clone();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "Personal Desktop Helper",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
            {
                ShowMainWindow();
            }
        };

        ShowMainWindow();
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
            _optionsWindow = new OptionsWindow();
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
