using System.Collections.Concurrent;
using PersonalDesktopHelper.Notifications;
using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Tests;

public sealed class NotificationTests
{
    [Fact]
    public async Task ToggleSuppressesMessagesAndAllowsReenabling()
    {
        var sender = new RecordingSender();
        var service = new NotificationService(sender);

        Assert.True(service.IsEnabled);
        Assert.True(await service.NotifyAsync("Title", "First"));
        service.IsEnabled = false;
        Assert.False(await service.NotifyAsync("Title", "Suppressed"));
        service.IsEnabled = true;
        await service.NotifyAsync("Title", "Last");

        Assert.Equal(["First", "Last"], sender.Messages.Select(message => message.Message));
    }

    [Fact]
    public async Task CancellationAndDeliveryFailuresAreNotSwallowed()
    {
        var sender = new RecordingSender();
        var service = new NotificationService(sender);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.NotifyAsync("Title", "Message", cancellation.Token));
        Assert.Empty(sender.Messages);

        var failure = new InvalidOperationException("Delivery failed");
        var failingService = new NotificationService(new FailingSender(failure));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            () => failingService.NotifyAsync("Title", "Message")));
    }

    [Fact]
    public async Task MinuteDemoUsesCurrentTimeAndRespectsNotificationToggle()
    {
        var clock = new ObservedTimeProvider();
        var sender = new RecordingSender();
        var service = new NotificationService(sender);
        var demo = new CurrentTimeNotificationTask(service, clock);
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()), clock);
        _ = scheduler.AddTask("Clock demo", new IntervalSchedule(TimeSpan.FromMinutes(1)), demo.RunAsync);

        await clock.WaitForDelayAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        await clock.WaitForDelayAsync();
        var first = Assert.Single(sender.Messages);
        Assert.Equal("Current time", first.Title);
        Assert.Contains("2026-09-22 12:01", first.Message);

        service.IsEnabled = false;
        clock.Advance(TimeSpan.FromMinutes(1));
        await clock.WaitForDelayAsync();
        Assert.Single(sender.Messages);

        service.IsEnabled = true;
        clock.Advance(TimeSpan.FromMinutes(1));
        await clock.WaitForDelayAsync();
        Assert.Equal(2, sender.Messages.Count);
        Assert.Contains("2026-09-22 12:03", sender.Messages.Last().Message);
    }

    private sealed class RecordingSender : INotificationSender
    {
        public ConcurrentQueue<(string Title, string Message)> Messages { get; } = new();

        public Task SendAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Enqueue((title, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FailingSender(Exception failure) : INotificationSender
    {
        public Task SendAsync(string title, string message, CancellationToken cancellationToken = default)
        {
            return Task.FromException(failure);
        }
    }
}
