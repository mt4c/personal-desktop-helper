# personal-desktop-helper

A Windows desktop application built with WPF and .NET 10.

## Requirements

- Windows
- .NET 10 SDK

## Build and run

From the repository root:

```powershell
dotnet build PersonalDesktopHelper.slnx
dotnet run --project .\src\PersonalDesktopHelper\PersonalDesktopHelper.csproj
```

## Project structure

```text
PersonalDesktopHelper.slnx
src\
  PersonalDesktopHelper\
    App.xaml / App.xaml.cs       Application startup, tray and lifetime
    Assets\                      Application icon
    Views\                       Main and options windows
    Notifications\               Notification service and Windows delivery
    Scheduling\                  Schedules, task management and demo task
    Persistence\                 JSON state model and atomic file storage
    Logging\                     Daily file logger and retention cleanup
    Properties\                  Assembly metadata
tests\
  PersonalDesktopHelper.Tests\
    Desktop\                     Window and tray integration coverage
    Logging\                     Log output and retention coverage
    Notifications\               Notification behavior
    Persistence\                 State loading and saving
    Scheduling\                  Schedule timing and task management
    TestSupport\                 Shared deterministic clock and test helpers
```

## Desktop behavior

The application starts with a main window and a notification-area (tray) icon.
Closing all windows leaves the application running in the tray.

- Left-click the tray icon to open or activate the main window.
- Right-click the tray icon and select **Options** to open or activate the
  options window. Its **Notifications** tab controls notification delivery.
  Its **Scheduler** tab lists tasks, schedules, status and next-run times, with
  controls to enable, disable or delete the selected task.
- Toggle **Notifications** in the tray menu to enable or disable notifications.
  This stays synchronized with the checkbox in Options.
- Select **Quit** from the tray menu to exit the application.

Each window has at most one open instance. Minimized windows are restored when
opened from the tray. Windows may place the tray icon in its hidden-icons area.

Window layouts are defined in `src\PersonalDesktopHelper\Views`.
Application lifetime and tray behavior are managed in
`src\PersonalDesktopHelper\App.xaml.cs`.

## Notifications

Modules use `INotificationService.NotifyAsync(title, message, cancellationToken)`.
`NotificationService` applies the enable/disable setting and delegates delivery to
`INotificationSender`. The current sender uses Windows tray balloon notifications;
another sender can be substituted without changing callers or scheduled tasks.
Windows notification settings and Do Not Disturb may suppress their display.
Disabling notifications drops new messages rather than queuing them for later.

The demo task sends the current local date and time at each minute boundary.
It continues to run when all windows are closed. It is created on first launch
only; disabling or deleting it is preserved across restarts.

## Saved state

Options and scheduled task definitions are saved automatically beside the executable:

```text
<executable directory>\state.json
```

Storage is based on `AppContext.BaseDirectory`, not the working directory. The
executable directory must be writable. During development, this is the app's
build output directory. To move an existing profile, copy its `state.json` and
optional `logs` folder beside the executable while the application is closed.

The file contains the notification setting, task IDs, handler IDs, schedule types,
enabled/completed flags and next-run times. Changes use a temporary file followed
by atomic replacement. Invalid files are reported rather than silently reset.
Failed saves are surfaced without applying the requested setting or task change.

On startup, missed occurrences are skipped, recurring tasks advance to their next
future occurrence, and expired one-shot tasks remain completed. A notification
summarizes skipped tasks when notifications are enabled. Disabled recurring tasks
do not execute or produce missed-run notifications. Enabling a disabled task
resumes at its next future occurrence.

## Scheduling

Modules register asynchronous callbacks with stable handler IDs using
`Scheduler.RegisterHandler` on every boot, before saved tasks are restored.
Task definitions reference these IDs, since executable delegates cannot be stored
in JSON. A saved task whose module is unavailable remains visible as
**Handler unavailable** until that handler is registered.

The running application exposes `App.Scheduler` and `App.Notifications`; these
services can also be passed to modules directly. Create definitions only when
requested, not on every boot, so saved or deleted tasks are not duplicated:

```csharp
app.Scheduler.RegisterHandler("reminder",
    token => app.Notifications.NotifyAsync("Reminder", "Scheduled reminder.", token));
app.Scheduler.RegisterHandler("periodic-work", token => DoWorkAsync(token));

var reminderId = app.Scheduler.AddTask("Reminder", "reminder",
    new OneShotSchedule(DateTimeOffset.Now.Date.AddDays(1).AddHours(9)));
app.Scheduler.AddTask("Periodic work", "periodic-work",
    new IntervalSchedule(TimeSpan.FromMinutes(5)));
app.Scheduler.AddTask("Weekday reminder", "reminder",
    new CronSchedule("0 9 * * 1-5"));

app.Scheduler.SetEnabled(reminderId, false);
// app.Scheduler.DeleteTask(reminderId);
```

- One-shot timestamps must be in the future and aligned to a whole minute.
- Intervals must be positive whole minutes. Their first run is the registration
  minute plus the interval; later runs retain that cadence.
- Cron expressions have five fields: minute, hour, day of month, month, day of
  week. Cron uses the local time zone by default, or an explicit `TimeZoneInfo`.
  Parsing and daylight-saving transitions use Cronos. When both day-of-month and
  day-of-week are specified, both must match (Cronos AND semantics).
- Tasks run independently off the UI thread. A task never overlaps itself.
  Missed occurrences during sleep or long-running callbacks are not replayed:
  an overdue task runs once, then advances to its next future occurrence.
- Task failures are passed to the scheduler's required failure callback. The app
  reports them through `Trace.TraceError`; recurring tasks continue after callback
  failures. Persistence failures pause the task and appear in its status.
- Disabling or deleting a task prevents future runs but lets a current run finish.
  Completed one-shot tasks cannot be enabled again; they can be deleted.
- `AddTask` returns a stable task ID. `GetTasks` returns display snapshots, and
  `GetCompletion(id)` returns the registration's lifetime task. **Quit** cancels
  pending waits and running callbacks, and awaits their completion before removing
  the tray icon. Callbacks must honor their cancellation token.
- Tasks must have the app running to execute. Occurrences are saved as consumed
  before invoking callbacks to prevent completed one-shots from replaying after
  a restart; this does not guarantee execution if the process crashes mid-run.

## Logging

The application writes timestamped UTC logs to:

```text
<executable directory>\logs\desktop-helper-yyyyMMdd-processId.log
```

Files roll over daily (UTC) and are flushed after each entry. Startup, shutdown,
notification setting changes and delivery requests, scheduler activity, skipped
tasks, persistence errors and unhandled exceptions are recorded. Notification
bodies are not logged. Modules can use the standard `System.Diagnostics.Trace`
information, warning and error methods.

An asynchronous cleanup task starts on every boot without blocking the UI. It
deletes only matching application log files whose last-write time is strictly
older than seven days. Recent logs, unrelated files, subdirectories and links are
left alone. Cleanup failures are logged and do not prevent other expired files
from being processed; logging write failures are surfaced in a dialog.

Run automated tests with:

```powershell
dotnet test .\tests\PersonalDesktopHelper.Tests\PersonalDesktopHelper.Tests.csproj
```