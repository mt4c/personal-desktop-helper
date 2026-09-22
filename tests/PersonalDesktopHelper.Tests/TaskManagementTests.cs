using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Tests;

public sealed class TaskManagementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisableOrDeleteLetsActiveRunFinishWithoutStartingMore(bool delete)
    {
        var clock = new ObservedTimeProvider();
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken runningToken = default;
        scheduler.RegisterHandler("handler", async token =>
        {
            Interlocked.Increment(ref calls);
            runningToken = token;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            finished.TrySetResult();
        });
        var id = scheduler.AddTask("Managed", "handler", new IntervalSchedule(TimeSpan.FromMinutes(1)));
        var completion = scheduler.GetCompletion(id);
        await clock.WaitForDelayAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        if (delete)
        {
            scheduler.DeleteTask(id);
            Assert.Empty(scheduler.GetTasks());
        }
        else
        {
            scheduler.SetEnabled(id, false);
            Assert.Equal("Running (disabled)", Assert.Single(scheduler.GetTasks()).Status);
        }

        Assert.False(runningToken.IsCancellationRequested);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, Volatile.Read(ref calls));
        release.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (delete)
        {
            await completion.WaitAsync(TimeSpan.FromSeconds(5));
        }

        await scheduler.DisposeAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisabledTaskCanBeReenabledAtItsNextFutureMinute()
    {
        var clock = new ObservedTimeProvider();
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock);
        var calls = 0;
        scheduler.RegisterHandler("handler", _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });
        var state = PersistenceTests.CreateState(
            new IntervalSchedule(TimeSpan.FromMinutes(1)), clock.GetUtcNow().AddSeconds(30)) with { IsEnabled = false };
        scheduler.RestoreTasks([state]);
        clock.Advance(TimeSpan.FromMinutes(10));
        scheduler.SetEnabled(state.Id, true);
        await clock.WaitForDelayAsync();
        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromSeconds(30));
        await clock.WaitForDelayAsync();
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task FailedPersistenceDoesNotDeleteOrDisableTask()
    {
        var clock = new ObservedTimeProvider();
        var fail = false;
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock, _ =>
        {
            if (fail)
            {
                throw new IOException("Disk unavailable");
            }
        });
        scheduler.RegisterHandler("handler", _ => Task.CompletedTask);
        var id = scheduler.AddTask("Managed", "handler", new IntervalSchedule(TimeSpan.FromMinutes(1)));
        fail = true;
        Assert.Throws<IOException>(() => scheduler.SetEnabled(id, false));
        Assert.Throws<IOException>(() => scheduler.DeleteTask(id));
        Assert.True(Assert.Single(scheduler.GetTasks()).IsEnabled);
    }
}
