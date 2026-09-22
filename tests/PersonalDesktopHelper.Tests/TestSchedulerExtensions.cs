using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Tests;

internal static class TestSchedulerExtensions
{
    public static Task AddTask(this Scheduler scheduler, string name, TaskSchedule schedule, Func<CancellationToken, Task> action)
    {
        var handlerId = Guid.NewGuid().ToString();
        scheduler.RegisterHandler(handlerId, action);
        return scheduler.GetCompletion(scheduler.AddTask(name, handlerId, schedule));
    }
}
