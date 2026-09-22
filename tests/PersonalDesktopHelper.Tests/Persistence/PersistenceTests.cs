using PersonalDesktopHelper.Notifications;
using PersonalDesktopHelper.Persistence;
using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public void JsonRoundTripsOptionsAndAllScheduleTypes()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonStateStore(directory.StatePath);
        Assert.True(store.IsNew);
        var due = new DateTimeOffset(2026, 9, 22, 13, 0, 0, TimeSpan.Zero);
        TaskSchedule[] schedules =
        [
            new OneShotSchedule(due),
            new IntervalSchedule(TimeSpan.FromMinutes(5)),
            new CronSchedule("0 9 * * 1-5", TimeZoneInfo.Utc)
        ];
        store.SetTasks(schedules.Select(schedule => CreateState(schedule, due)).ToArray());
        store.SetNotificationsEnabled(false);

        var loaded = new JsonStateStore(directory.StatePath);
        Assert.False(loaded.IsNew);
        Assert.False(loaded.State.NotificationsEnabled);
        Assert.Equal(3, loaded.State.Tasks.Count);
        Assert.Equal(due, Assert.IsType<OneShotSchedule>(loaded.State.Tasks[0].Schedule).RunAt);
        Assert.Equal(TimeSpan.FromMinutes(5), Assert.IsType<IntervalSchedule>(loaded.State.Tasks[1].Schedule).Interval);
        Assert.Equal("0 9 * * 1-5", Assert.IsType<CronSchedule>(loaded.State.Tasks[2].Schedule).Expression);
        Assert.Equal(TimeZoneInfo.Utc.Id, Assert.IsType<CronSchedule>(loaded.State.Tasks[2].Schedule).TimeZoneId);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"notificationsEnabled\":true,\"tasks\":[]}")]
    [InlineData("{\"version\":1,\"notificationsEnabled\":true,\"tasks\":null}")]
    public void InvalidJsonIsReportedWithoutOverwritingIt(string json)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(directory.StatePath, json);
        Assert.Throws<InvalidDataException>(() => new JsonStateStore(directory.StatePath));
        Assert.Equal(json, File.ReadAllText(directory.StatePath));
    }

    [Fact]
    public void FailedSaveDoesNotChangeTheNotificationOptionOrState()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonStateStore(directory.StatePath);
        store.SetNotificationsEnabled(true);
        var original = File.ReadAllText(directory.StatePath);
        var service = new NotificationService(new RecordingSender(), true, store.SetNotificationsEnabled);
        using (var locked = new FileStream(directory.StatePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Record.Exception(() => service.IsEnabled = false);
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.True(service.IsEnabled);
            Assert.True(store.State.NotificationsEnabled);
        }

        Assert.Equal(original, File.ReadAllText(directory.StatePath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Fact]
    public async Task ReloadSkipsOverdueTasksAndPersistsCompletedOneShots()
    {
        using var directory = new TemporaryDirectory();
        var clock = new ObservedTimeProvider();
        var past = clock.GetUtcNow().AddSeconds(-30);
        var store = new JsonStateStore(directory.StatePath);
        store.SetTasks([
            CreateState(new OneShotSchedule(past), past),
            CreateState(new IntervalSchedule(TimeSpan.FromMinutes(5)), past),
            CreateState(new CronSchedule("* * * * *", TimeZoneInfo.Utc), past)
        ]);
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock, store.SetTasks);
        var calls = 0;
        scheduler.RegisterHandler("handler", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        var skipped = scheduler.RestoreTasks(store.State.Tasks);

        Assert.Equal(3, skipped.Count);
        var tasks = scheduler.GetTasks();
        Assert.True(tasks[0].IsCompleted);
        Assert.Null(tasks[0].Definition.NextRunAt);
        Assert.All(tasks.Skip(1), task => Assert.True(task.Definition.NextRunAt > clock.GetUtcNow()));
        Assert.Equal(0, calls);
        var persisted = new JsonStateStore(directory.StatePath).State.Tasks;
        Assert.True(persisted[0].IsCompleted);
        Assert.Equal(tasks[1].Definition.NextRunAt, persisted[1].NextRunAt);

        var sender = new RecordingSender();
        await SkippedTaskNotification.SendAsync(new NotificationService(sender), skipped);
        Assert.Contains("3 task(s)", Assert.Single(sender.Messages));
    }

    [Fact]
    public async Task DeletedAndDisabledTasksSurviveRestartWithoutReseeding()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonStateStore(directory.StatePath);
        var clock = new ObservedTimeProvider();
        Guid disabledId;
        await using (var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock, store.SetTasks))
        {
            scheduler.RegisterHandler("handler", _ => Task.CompletedTask);
            disabledId = scheduler.AddTask("Disabled", "handler", new IntervalSchedule(TimeSpan.FromMinutes(1)));
            var deletedId = scheduler.AddTask("Deleted", "handler", new IntervalSchedule(TimeSpan.FromMinutes(1)));
            scheduler.SetEnabled(disabledId, false);
            scheduler.DeleteTask(deletedId);
        }

        var loaded = new JsonStateStore(directory.StatePath);
        Assert.False(loaded.IsNew);
        await using var restored = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock, loaded.SetTasks);
        restored.RegisterHandler("handler", _ => Task.CompletedTask);
        restored.RestoreTasks(loaded.State.Tasks);
        var remaining = Assert.Single(restored.GetTasks());
        Assert.Equal(disabledId, remaining.Id);
        Assert.False(remaining.IsEnabled);
        restored.DeleteTask(disabledId);
        Assert.Empty(new JsonStateStore(directory.StatePath).State.Tasks);
        Assert.False(new JsonStateStore(directory.StatePath).IsNew);
    }

    [Fact]
    public async Task MissingHandlersAreVisibleAndDoNotLoseTheirSavedDefinition()
    {
        var clock = new ObservedTimeProvider();
        var state = CreateState(new IntervalSchedule(TimeSpan.FromMinutes(1)), clock.GetUtcNow().AddSeconds(30));
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock);
        scheduler.RestoreTasks([state]);
        Assert.Equal("Handler unavailable", Assert.Single(scheduler.GetTasks()).Status);
        scheduler.RegisterHandler("handler", _ => Task.CompletedTask);
        Assert.True(Assert.Single(scheduler.GetTasks()).HandlerAvailable);
        Assert.Equal(state.Id, Assert.Single(scheduler.GetTasks()).Id);
    }

    internal static ScheduledTaskState CreateState(TaskSchedule schedule, DateTimeOffset due) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Saved task",
        HandlerId = "handler",
        Schedule = schedule,
        IsEnabled = true,
        IsCompleted = false,
        NextRunAt = due
    };

    private sealed class RecordingSender : INotificationSender
    {
        public List<string> Messages { get; } = [];
        public Task SendAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }
}
