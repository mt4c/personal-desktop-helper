# personal-desktop-helper

A Windows desktop application built with WPF and .NET 10.

## Requirements

- Windows
- .NET 10 SDK

## Build and run

From the repository root:

```powershell
dotnet build PersonalDesktopHelper.slnx
dotnet run --project PersonalDesktopHelper.csproj
```

## Desktop behavior

The application starts with a main window and a notification-area (tray) icon.
Closing all windows leaves the application running in the tray.

- Left-click the tray icon to open or activate the main window.
- Right-click the tray icon and select **Options** to open or activate the
  options window. Options are not configured yet.
- Select **Quit** from the tray menu to exit the application.

Each window has at most one open instance. Minimized windows are restored when
opened from the tray. Windows may place the tray icon in its hidden-icons area.

Window layouts are defined in `MainWindow.xaml` and `OptionsWindow.xaml`.
Application lifetime and tray behavior are managed in `App.xaml.cs`.