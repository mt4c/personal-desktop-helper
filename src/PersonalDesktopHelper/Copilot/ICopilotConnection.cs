namespace PersonalDesktopHelper.Copilot;

public interface ICopilotConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    IReadOnlyList<string> Models { get; }
    Task<string> SignInAsync(CopilotSettings settings, Action<DeviceAuthorization> authorizationRequested, CancellationToken cancellationToken);
    Task SignOutAsync();
    Task<string> ConnectAsync(CopilotSettings settings, CancellationToken cancellationToken);
    Task<string> SendAsync(string prompt, Action<string> responseChanged, CancellationToken cancellationToken);
    Task NewChatAsync(CancellationToken cancellationToken);
    Task DisconnectAsync();
}
