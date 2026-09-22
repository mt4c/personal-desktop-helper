using System.Collections.Concurrent;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PersonalDesktopHelper.Mcp;
using PersonalDesktopHelper.Notifications;
using PersonalDesktopHelper.Persistence;
using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Tests;

public sealed class LocalMcpTests
{
    [Fact]
    public async Task RealMcpHandshakeListsModuleSchemasAndRunsNotificationTools()
    {
        var sender = new RecordingSender();
        var notifications = new NotificationService(sender);
        await using var scheduler = NewScheduler();
        await using var mcp = new LocalMcpConnection(new DesktopModuleTools(notifications, scheduler));

        var tools = await mcp.ListToolsAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(8, tools.Count);
        Assert.Equal(mcp.Tools.Select(tool => tool.Name).Order(), tools.Select(tool => tool.Name).Order());
        Assert.All(tools, tool => Assert.Equal(JsonValueKind.Object, tool.InputSchema.ValueKind));
        var sent = await Call(mcp, "notifications_send", new { title = "Test", message = "Hello" });
        Assert.NotEqual(true, sent.IsError);
        Assert.Equal(("Test", "Hello"), Assert.Single(sender.Messages));
        Assert.Contains("submitted", Text(sent));
        await Call(mcp, "notifications_set_enabled", new { enabled = false });
        var suppressed = await Call(mcp, "notifications_send", new { title = "Test", message = "Suppressed" });
        Assert.Contains("suppressed", Text(suppressed));
        Assert.Single(sender.Messages);
        Assert.Contains("false", Text(await Call(mcp, "notifications_get_status", new { })));
    }

    [Fact]
    public async Task ScheduledNotificationParametersPersistAndExecuteAfterReload()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonStateStore(directory.StatePath);
        var clock = new ObservedTimeProvider();
        var sender = new RecordingSender();
        var notifications = new NotificationService(sender);
        Guid id;
        await using (var scheduler = NewScheduler(clock, store.SetTasks))
        {
            scheduler.RegisterHandler(NotificationScheduledTask.HandlerId, new NotificationScheduledTask(notifications).RunAsync);
            await using var mcp = new LocalMcpConnection(new DesktopModuleTools(notifications, scheduler));
            var result = await Call(mcp, "scheduler_create_task", new
            {
                name = "Reminder", handlerId = "notification", scheduleKind = "interval", intervalMinutes = 1,
                parameters = new { title = "Reminder", message = "Persisted message" }
            });
            Assert.NotEqual(true, result.IsError);
            id = Assert.Single(scheduler.GetTasks()).Id;
            Assert.Equal("Persisted message", Assert.Single(store.State.Tasks).Parameters["message"]);
            await Call(mcp, "scheduler_set_enabled", new { taskId = id.ToString(), enabled = false });
            Assert.False(Assert.Single(store.State.Tasks).IsEnabled);
        }

        var restoredClock = new ObservedTimeProvider();
        var loaded = new JsonStateStore(directory.StatePath);
        await using var restored = NewScheduler(restoredClock, loaded.SetTasks);
        restored.RegisterHandler(NotificationScheduledTask.HandlerId, new NotificationScheduledTask(notifications).RunAsync);
        restored.RestoreTasks(loaded.State.Tasks);
        await using var restoredMcp = new LocalMcpConnection(new DesktopModuleTools(notifications, restored));
        await Call(restoredMcp, "scheduler_set_enabled", new { taskId = id.ToString(), enabled = true });
        await restoredClock.WaitForDelayAsync();
        restoredClock.Advance(TimeSpan.FromSeconds(30));
        await restoredClock.WaitForDelayAsync();
        Assert.Equal(("Reminder", "Persisted message"), Assert.Single(sender.Messages));
        var listing = Text(await Call(restoredMcp, "scheduler_list_tasks", new { }));
        Assert.Contains(id.ToString(), listing);
        Assert.Contains("nowLocal", listing);
        await Call(restoredMcp, "scheduler_delete_task", new { taskId = id.ToString() });
        Assert.Empty(restored.GetTasks());
        Assert.Empty(new JsonStateStore(directory.StatePath).State.Tasks);
    }

    [Theory]
    [InlineData("""{"name":"Bad","handlerId":"notification","scheduleKind":"interval","intervalMinutes":0,"parameters":{"title":"T","message":"M"}}""")]
    [InlineData("""{"name":"Bad","handlerId":"notification","scheduleKind":"interval","intervalMinutes":1}""")]
    [InlineData("""{"name":"Bad","handlerId":"missing","scheduleKind":"interval","intervalMinutes":1}""")]
    [InlineData("""{"name":"Bad","handlerId":"notification","scheduleKind":"once","runAt":"2030-01-01T12:00","parameters":{"title":"T","message":"M"}}""")]
    [InlineData("""{"name":"Bad","handlerId":"notification","scheduleKind":"once","runAt":"2030-01-01T12:00:01Z","parameters":{"title":"T","message":"M"}}""")]
    [InlineData("""{"name":"Bad","handlerId":"notification","scheduleKind":"cron","cronExpression":"* * * * * *","parameters":{"title":"T","message":"M"}}""")]
    [InlineData("""{"name":"Bad","handlerId":"current-time-notification","scheduleKind":"interval","intervalMinutes":1,"parameters":{"message":"Ignored"}}""")]
    public async Task InvalidTaskArgumentsReturnErrorsWithoutSideEffects(string arguments)
    {
        var notifications = new NotificationService(new RecordingSender());
        await using var scheduler = NewScheduler();
        scheduler.RegisterHandler(NotificationScheduledTask.HandlerId, new NotificationScheduledTask(notifications).RunAsync);
        scheduler.RegisterHandler("current-time-notification", new CurrentTimeNotificationTask(notifications).RunAsync);
        await using var mcp = new LocalMcpConnection(new DesktopModuleTools(notifications, scheduler));
        using var parsed = JsonDocument.Parse(arguments);

        var result = await mcp.CallAsync("scheduler_create_task", parsed.RootElement, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Empty(scheduler.GetTasks());
    }

    [Fact]
    public async Task UnknownToolsAndUnknownArgumentsCannotInvokeModuleActions()
    {
        var sender = new RecordingSender();
        await using var scheduler = NewScheduler();
        await using var mcp = new LocalMcpConnection(new DesktopModuleTools(new NotificationService(sender), scheduler));

        Assert.True((await Call(mcp, "run_shell", new { command = "anything" })).IsError);
        Assert.True((await Call(mcp, "notifications_send", new { title = "T", message = "M", extra = true })).IsError);
        Assert.Empty(sender.Messages);
    }

    [Theory]
    [InlineData("once")]
    [InlineData("cron")]
    public async Task CreatesMinutePrecisionSchedules(string scheduleKind)
    {
        var notifications = new NotificationService(new RecordingSender());
        await using var scheduler = NewScheduler();
        scheduler.RegisterHandler(NotificationScheduledTask.HandlerId, new NotificationScheduledTask(notifications).RunAsync);
        await using var mcp = new LocalMcpConnection(new DesktopModuleTools(notifications, scheduler));

        var result = await Call(mcp, "scheduler_create_task", new
        {
            name = "Reminder", handlerId = "notification", scheduleKind,
            runAt = scheduleKind == "once" ? DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd'T'HH:mm':00Z'") : null,
            cronExpression = scheduleKind == "cron" ? "0 9 * * 1-5" : null,
            timeZoneId = scheduleKind == "cron" ? "UTC" : null,
            parameters = new { title = "T", message = "M" }
        });

        Assert.NotEqual(true, result.IsError);
        var task = Assert.Single(scheduler.GetTasks());
        Assert.NotNull(task.Definition.NextRunAt);
        Assert.Equal(0, task.Definition.NextRunAt.Value.Ticks % TimeSpan.TicksPerMinute);
        if (scheduleKind == "once")
        {
            Assert.IsType<OneShotSchedule>(task.Definition.Schedule);
        }
        else
        {
            Assert.IsType<CronSchedule>(task.Definition.Schedule);
        }
    }

    [Fact]
    public async Task CancellingAnActiveMcpCallCancelsTheHandlerAndLeavesTheConnectionUsable()
    {
        var sender = new CancellableSender();
        await using var scheduler = NewScheduler();
        await using var mcp = new LocalMcpConnection(new DesktopModuleTools(new NotificationService(sender), scheduler));
        using var cancellation = new CancellationTokenSource();
        var request = mcp.CallAsync("notifications_send",
            JsonSerializer.SerializeToElement(new { title = "T", message = "M" }), cancellation.Token);
        await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(10)));
        await sender.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(true, (await Call(mcp, "notifications_get_status", new { })).IsError);
    }

    [Fact]
    public async Task ConnectionCanBeInitializedAgainAfterCancellation()
    {
        await using var scheduler = NewScheduler();
        await using var mcp = new LocalMcpConnection(new DesktopModuleTools(new NotificationService(new RecordingSender()), scheduler));
        using var cancellation = new CancellationTokenSource();
        var listing = mcp.ListToolsAsync(cancellation.Token);
        cancellation.Cancel();
        try
        {
            await listing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Initialization or listing may finish before the cancellation reaches it.
        }

        Assert.Equal(8, (await mcp.ListToolsAsync().WaitAsync(TimeSpan.FromSeconds(10))).Count);
    }

    [Fact]
    public async Task CancelledToolCallDoesNotStartAnAction()
    {
        var sender = new RecordingSender();
        await using var scheduler = NewScheduler();
        await using var mcp = new LocalMcpConnection(new DesktopModuleTools(new NotificationService(sender), scheduler));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mcp.CallAsync(
            "notifications_send", JsonSerializer.SerializeToElement(new { title = "T", message = "M" }), cancelled.Token));
        Assert.Empty(sender.Messages);
    }

    private static Scheduler NewScheduler(TimeProvider? clock = null, Action<IReadOnlyList<ScheduledTaskState>>? save = null) =>
        new((_, error) => Assert.Fail(error.ToString()), clock, save);

    private static Task<CallToolResult> Call(LocalMcpConnection mcp, string name, object arguments) =>
        mcp.CallAsync(name, JsonSerializer.SerializeToElement(arguments), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

    private static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(item => item.Text));

    private sealed class RecordingSender : INotificationSender
    {
        public ConcurrentQueue<(string, string)> Messages { get; } = new();
        public Task SendAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Enqueue((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class CancellableSender : INotificationSender
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Cancelled.SetResult();
                throw;
            }
        }
    }
}
