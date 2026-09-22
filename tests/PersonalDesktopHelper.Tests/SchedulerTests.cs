using System.Collections.Concurrent;
using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task OneShotRunsOnceAndNeverEarly()
    {
        var clock = new ObservedTimeProvider();
        var failures = new ConcurrentQueue<Exception>();
        await using var scheduler = new Scheduler((_, error) => failures.Enqueue(error), clock);
        var calls = 0;
        var completion = scheduler.AddTask(
            "One shot",
            new OneShotSchedule(clock.GetUtcNow().AddSeconds(30)),
            _ =>
            {
                Interlocked.Increment(ref calls);
                return Task.CompletedTask;
            });

        Assert.Equal(TimeSpan.FromSeconds(30), await clock.WaitForDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, Volatile.Read(ref calls));
        clock.Advance(TimeSpan.FromSeconds(1));
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, calls);
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntervalAndCronRepeatAtMinuteBoundaries(bool useCron)
    {
        var clock = new ObservedTimeProvider();
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock);
        TaskSchedule schedule = useCron
            ? new CronSchedule("* * * * *", TimeZoneInfo.Utc)
            : new IntervalSchedule(TimeSpan.FromMinutes(1));
        var calls = 0;
        _ = scheduler.AddTask("Recurring", schedule, _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        Assert.Equal(TimeSpan.FromSeconds(30), await clock.WaitForDelayAsync());
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromMinutes(1), await clock.WaitForDelayAsync());
        Assert.Equal(1, Volatile.Read(ref calls));
        clock.Advance(TimeSpan.FromMinutes(1));
        await clock.WaitForDelayAsync();
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task FailureIsReportedAndRecurringTaskContinues()
    {
        var clock = new ObservedTimeProvider();
        var failures = new ConcurrentQueue<(string Name, Exception Error)>();
        await using var scheduler = new Scheduler((name, error) => failures.Enqueue((name, error)), clock);
        var expected = new InvalidOperationException("Task failure");
        var calls = 0;
        _ = scheduler.AddTask("Failing task", new IntervalSchedule(TimeSpan.FromMinutes(1)), _ =>
        {
            return Interlocked.Increment(ref calls) == 1 ? Task.FromException(expected) : Task.CompletedTask;
        });

        await clock.WaitForDelayAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await clock.WaitForDelayAsync();
        Assert.Equal(("Failing task", expected), Assert.Single(failures));
        clock.Advance(TimeSpan.FromMinutes(1));
        await clock.WaitForDelayAsync();
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task SlowTaskDoesNotOverlapOrBlockOtherTasksAndSkipsMissedRuns()
    {
        var clock = new ObservedTimeProvider();
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        _ = scheduler.AddTask("Slow task", new IntervalSchedule(TimeSpan.FromMinutes(1)), async token =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var independent = scheduler.AddTask(
            "Independent",
            new OneShotSchedule(clock.GetUtcNow().AddSeconds(30)),
            _ => Task.CompletedTask);

        await clock.WaitForDelayAsync();
        await clock.WaitForDelayAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await independent.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        Assert.Equal(TimeSpan.FromMinutes(1), await clock.WaitForDelayAsync());
        Assert.Equal(1, Volatile.Read(ref calls));
        clock.Advance(TimeSpan.FromMinutes(1));
        await clock.WaitForDelayAsync();
        Assert.Equal(2, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task OverdueTaskRunsOnceWithoutReplayingBacklog()
    {
        var clock = new ObservedTimeProvider();
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock);
        var calls = 0;
        _ = scheduler.AddTask("Overdue", new IntervalSchedule(TimeSpan.FromMinutes(5)), _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        await clock.WaitForDelayAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await clock.WaitForDelayAsync();
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task ShutdownCancelsRunningAndPendingWorkAndRejectsNewTasks()
    {
        var clock = new ObservedTimeProvider();
        var failures = new ConcurrentQueue<Exception>();
        await using var scheduler = new Scheduler((_, error) => failures.Enqueue(error), clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = scheduler.AddTask("Running", new IntervalSchedule(TimeSpan.FromMinutes(1)), async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var pendingCalls = 0;
        var pending = scheduler.AddTask("Pending", new IntervalSchedule(TimeSpan.FromHours(1)), _ =>
        {
            Interlocked.Increment(ref pendingCalls);
            return Task.CompletedTask;
        });
        await clock.WaitForDelayAsync();
        await clock.WaitForDelayAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await scheduler.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(running.IsCompletedSuccessfully);
        Assert.True(pending.IsCompletedSuccessfully);
        Assert.Equal(0, pendingCalls);
        Assert.Empty(failures);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = scheduler.AddTask("Late", new IntervalSchedule(TimeSpan.FromMinutes(1)), _ => Task.CompletedTask);
        });
        scheduler.RequestStop();
    }

    [Fact]
    public async Task PastOneShotIsRejectedAtRegistration()
    {
        var clock = new ObservedTimeProvider();
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock);
        Assert.Throws<ArgumentException>(() =>
        {
            _ = scheduler.AddTask(
                "Past",
                new OneShotSchedule(clock.GetUtcNow().AddSeconds(-30)),
                _ => Task.CompletedTask);
        });
    }
}
