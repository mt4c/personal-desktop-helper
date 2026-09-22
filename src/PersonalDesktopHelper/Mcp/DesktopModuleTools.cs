using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PersonalDesktopHelper.Notifications;
using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Mcp;

public sealed partial class DesktopModuleTools(INotificationService notifications, Scheduler scheduler)
{
    public IReadOnlyList<McpServerTool> CreateTools() => GetType().GetMethods()
        .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
        .Select(method => McpServerTool.Create(method, this))
        .OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal).ToArray();

    [McpServerTool(Name = "notifications_get_status", ReadOnly = true, OpenWorld = false)]
    [Description("Return whether user notifications are enabled. No changes are made.")]
    public Task<CallToolResult> GetNotificationStatus(CancellationToken cancellationToken) =>
        ExecuteAsync(() => Task.FromResult<object>(new { enabled = notifications.IsEnabled }), cancellationToken);

    [McpServerTool(Name = "notifications_set_enabled", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Enable or disable notifications and persist the user's setting.")]
    public Task<CallToolResult> SetNotificationsEnabled(bool enabled, CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            notifications.IsEnabled = enabled;
            return Task.FromResult<object>(new { enabled = notifications.IsEnabled });
        }, cancellationToken);

    [McpServerTool(Name = "notifications_send", Destructive = false, OpenWorld = false)]
    [Description("Send a desktop notification now. Respects the notification toggle; returns suppressed when disabled.")]
    public Task<CallToolResult> SendNotification(string title, string message, CancellationToken cancellationToken) =>
        ExecuteAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
            if (!await notifications.NotifyAsync(title, message, cancellationToken).ConfigureAwait(false))
            {
                return new { status = "suppressed", reason = "Notifications are disabled." };
            }

            return new { status = "submitted", reason = "Submitted for delivery; Windows may suppress display." };
        }, cancellationToken);

    [McpServerTool(Name = "scheduler_list_tasks", ReadOnly = true, OpenWorld = false)]
    [Description("List scheduled task IDs, handlers, parameters, schedules, status and next runs, plus current local/UTC time and local timezone. Use before relative-time scheduling or modifying tasks.")]
    public Task<CallToolResult> ListTasks(CancellationToken cancellationToken) =>
        ExecuteAsync(() => Task.FromResult<object>(new
        {
            nowLocal = DateTimeOffset.Now,
            nowUtc = DateTimeOffset.UtcNow,
            timeZoneId = TimeZoneInfo.Local.Id,
            tasks = scheduler.GetTasks().Select(task => new
            {
                task.Id, task.Name, task.Definition.HandlerId, task.Definition.Parameters,
                task.Schedule, task.Status, task.IsEnabled, task.IsCompleted, task.Definition.NextRunAt
            }).ToArray()
        }), cancellationToken);

    [McpServerTool(Name = "scheduler_list_handlers", ReadOnly = true, OpenWorld = false)]
    [Description("List registered handler IDs. 'notification' requires title and message string parameters; 'current-time-notification' requires no parameters. Only registered handlers can be scheduled.")]
    public Task<CallToolResult> ListHandlers(CancellationToken cancellationToken) =>
        ExecuteAsync(() => Task.FromResult<object>(new { handlerIds = scheduler.GetHandlerIds() }), cancellationToken);

    [McpServerTool(Name = "scheduler_create_task", Destructive = false, OpenWorld = false)]
    [Description("Persist a task for a registered handler. scheduleKind is once, interval, or cron. Supply only runAt (ISO-8601 with UTC offset, future whole minute), intervalMinutes (positive integer), or cronExpression (five fields), respectively. timeZoneId is optional for cron only. The notification handler requires parameters {title,message}. New tasks are enabled.")]
    public Task<CallToolResult> CreateTask(
        string name, string handlerId, string scheduleKind,
        CancellationToken cancellationToken,
        string? runAt = null, int? intervalMinutes = null, string? cronExpression = null,
        string? timeZoneId = null, Dictionary<string, string>? parameters = null) =>
        ExecuteAsync(() =>
        {
            var values = parameters ?? new Dictionary<string, string>();
            if (handlerId == NotificationScheduledTask.HandlerId)
            {
                NotificationScheduledTask.ValidateParameters(values);
            }
            else if (handlerId == "current-time-notification" && values.Count != 0)
            {
                throw new ArgumentException("The current-time-notification handler does not accept parameters.");
            }

            TaskSchedule schedule = scheduleKind switch
            {
                "once" when runAt is not null && intervalMinutes is null && cronExpression is null && timeZoneId is null =>
                    new OneShotSchedule(ParseTimestamp(runAt)),
                "interval" when intervalMinutes is not null && runAt is null && cronExpression is null && timeZoneId is null =>
                    new IntervalSchedule(TimeSpan.FromMinutes(intervalMinutes.Value)),
                "cron" when cronExpression is not null && runAt is null && intervalMinutes is null =>
                    new CronSchedule(cronExpression, timeZoneId is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)),
                _ => throw new ArgumentException("Provide exactly the fields required by the selected scheduleKind: once, interval, or cron.")
            };
            var id = scheduler.AddTask(name, handlerId, schedule, values);
            return Task.FromResult<object>(new { id, status = "created" });
        }, cancellationToken);

    [McpServerTool(Name = "scheduler_set_enabled", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Enable or disable an existing scheduled task by its exact ID. Disabling prevents future runs but lets an active run finish. Completed one-shot tasks cannot be enabled.")]
    public Task<CallToolResult> SetTaskEnabled(string taskId, bool enabled, CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            scheduler.SetEnabled(Guid.Parse(taskId), enabled);
            return Task.FromResult<object>(new { taskId, enabled });
        }, cancellationToken);

    [McpServerTool(Name = "scheduler_delete_task", Destructive = true, OpenWorld = false)]
    [Description("Delete a scheduled task by its exact ID and persist the deletion. An active run is allowed to finish; no future runs occur.")]
    public Task<CallToolResult> DeleteTask(string taskId, CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            scheduler.DeleteTask(Guid.Parse(taskId));
            return Task.FromResult<object>(new { taskId, status = "deleted" });
        }, cancellationToken);

    private static async Task<CallToolResult> ExecuteAsync(Func<Task<object>> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            return ToolResults.Success(await action().ConfigureAwait(false));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException
            or KeyNotFoundException or IOException or UnauthorizedAccessException
            or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            Trace.TraceWarning("MCP module action failed ({0}).", error.GetType().Name);
            return ToolResults.Error(error.Message);
        }
    }

    [GeneratedRegex(@"(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TimestampOffset();

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!value.Contains('T') || !TimestampOffset().IsMatch(value) ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
        {
            throw new ArgumentException("runAt must be an ISO-8601 timestamp with an explicit UTC offset.");
        }

        return result;
    }
}
