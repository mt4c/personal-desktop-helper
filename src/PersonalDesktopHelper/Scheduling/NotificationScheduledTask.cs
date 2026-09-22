using PersonalDesktopHelper.Notifications;

namespace PersonalDesktopHelper.Scheduling;

public sealed class NotificationScheduledTask(INotificationService notifications)
{
    public const string HandlerId = "notification";

    public static void ValidateParameters(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title) ||
            !parameters.TryGetValue("message", out var message) || string.IsNullOrWhiteSpace(message) ||
            parameters.Keys.Any(key => key is not ("title" or "message")))
        {
            throw new ArgumentException("The notification handler requires only non-empty 'title' and 'message' parameters.");
        }
    }

    public Task RunAsync(IReadOnlyDictionary<string, string> parameters, CancellationToken token)
    {
        ValidateParameters(parameters);
        return notifications.NotifyAsync(parameters["title"], parameters["message"], token);
    }
}
