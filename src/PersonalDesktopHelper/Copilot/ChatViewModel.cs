using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace PersonalDesktopHelper.Copilot;

public sealed class ChatViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly ICopilotConnection _connection;
    private readonly Action<CopilotSettings> _persistSettings;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _activeCancellation;
    private Task<bool>? _activeOperation;
    private bool _disposed;
    private bool _isBusy;
    private string _prompt = "";
    private string _status = "Not connected. Configure Copilot in Options, or send a message to connect.";
    private string _errorMessage = "";
    private long _sendVersion;
    private long _authorizationVersion;
    private DeviceAuthorization? _authorization;

    public ChatViewModel(
        ICopilotConnection connection, CopilotSettings settings,
        Action<CopilotSettings> persistSettings, Action<Action> dispatch, string? constantSystemPrompt = null)
    {
        _connection = connection;
        Settings = settings;
        _persistSettings = persistSettings;
        _dispatch = dispatch;
        ConstantSystemPrompt = constantSystemPrompt ?? SystemPromptDefinition.BuildConstant([]);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? TranscriptChanged;
    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public CopilotSettings Settings { get; private set; }
    public string ConstantSystemPrompt { get; }
    public string EffectiveSystemPrompt => SystemPromptDefinition.Compose(ConstantSystemPrompt, Settings.AdditionalSystemPrompt);
    public bool IsConnected => _connection.IsConnected;
    public IReadOnlyList<string> Models => _connection.Models;
    public string UserCode => _authorization?.UserCode ?? "";
    public Uri? VerificationUri => _authorization?.VerificationUri;
    public bool HasDeviceCode => _authorization is not null && _isBusy;
    public bool IsBusy => _isBusy;
    public bool CanConfigure => !_isBusy && !_disposed;
    public bool CanDisconnect => CanConfigure && IsConnected;
    public bool CanSend => CanConfigure && !string.IsNullOrWhiteSpace(Prompt);
    public string Status => _status;
    public string ErrorMessage => _errorMessage;

    public string Prompt
    {
        get => _prompt;
        set
        {
            _prompt = value;
            Changed(nameof(Prompt));
            Changed(nameof(CanSend));
        }
    }

    public Task<bool> ConnectAsync() => ExecuteAsync(async token =>
    {
        SetStatus("Connecting to GitHub Copilot...");
        SetStatus(await _connection.ConnectAsync(Settings, token));
    });

    public Task<bool> SignInAsync() => ExecuteAsync(async token =>
    {
        var version = ++_authorizationVersion;
        Messages.Clear();
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
        SetStatus("Requesting a GitHub sign-in code...");
        try
        {
            var status = await _connection.SignInAsync(Settings, authorization => _dispatch(() =>
            {
                if (version == _authorizationVersion && !token.IsCancellationRequested)
                {
                    _authorization = authorization;
                    SetStatus("Open the sign-in page and enter the displayed code.");
                    NotifyState();
                }
            }), token);
            SetStatus(status);
        }
        finally
        {
            ++_authorizationVersion;
            _authorization = null;
        }
    });

    public Task<bool> SignOutAsync() => ExecuteAsync(async _ =>
    {
        await _connection.SignOutAsync();
        Messages.Clear();
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
        SetStatus("Signed out. The saved GitHub credential has been removed.");
    });

    public Task<bool> DisconnectAsync() => ExecuteAsync(async _ =>
    {
        await _connection.DisconnectAsync();
        SetStatus("Disconnected.");
    });

    public Task<bool> ApplySettingsAsync(CopilotSettings settings) => ExecuteAsync(async _ =>
    {
        settings.Validate();
        if (settings == Settings)
        {
            return;
        }

        _persistSettings(settings);
        Settings = settings;
        Changed(nameof(Settings));
        Changed(nameof(EffectiveSystemPrompt));
        await _connection.DisconnectAsync();
        await _connection.NewChatAsync(CancellationToken.None);
        Messages.Clear();
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
        SetStatus("Copilot settings saved. Send a message or connect to start a new chat.");
    });

    public Task<bool> NewChatAsync() => ExecuteAsync(async token =>
    {
        await _connection.NewChatAsync(token);
        Messages.Clear();
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
        SetStatus(IsConnected ? "Ready for a new chat." : "New chat. Send a message to connect.");
    });

    public Task<bool> SendAsync()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Prompt);
        var prompt = Prompt.Trim();
        return ExecuteAsync(async token =>
        {
            if (!IsConnected)
            {
                SetStatus("Connecting to GitHub Copilot...");
                await _connection.ConnectAsync(Settings, token);
            }

            Messages.Add(new ChatMessage("You", prompt));
            var reply = new ChatMessage("Copilot", "");
            Messages.Add(reply);
            Prompt = "";
            TranscriptChanged?.Invoke(this, EventArgs.Empty);
            SetStatus("Copilot is responding...");
            var version = ++_sendVersion;
            try
            {
                var content = await _connection.SendAsync(prompt, text => _dispatch(() =>
                {
                    if (version == _sendVersion && !token.IsCancellationRequested)
                    {
                        reply.Content = text;
                        TranscriptChanged?.Invoke(this, EventArgs.Empty);
                    }
                }), token);
                reply.Content = string.IsNullOrWhiteSpace(content) ? "(Copilot returned no text.)" : content;
                SetStatus("Ready.");
            }
            finally
            {
                ++_sendVersion;
                if (reply.Content.Length == 0)
                {
                    reply.Content = token.IsCancellationRequested ? "(Stopped.)" : "(No response received.)";
                }

                TranscriptChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public void RequestStop()
    {
        if (_activeCancellation is not null)
        {
            SetStatus("Stopping...");
            _activeCancellation.Cancel();
        }
    }

    private Task<bool> ExecuteAsync(Func<CancellationToken, Task> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_isBusy)
        {
            throw new InvalidOperationException("Wait for the current Copilot operation or stop it first.");
        }

        _isBusy = true;
        _errorMessage = "";
        _activeCancellation = new CancellationTokenSource();
        NotifyState();
        _activeOperation = RunOperationAsync(operation, _activeCancellation);
        return _activeOperation;
    }

    private async Task<bool> RunOperationAsync(
        Func<CancellationToken, Task> operation, CancellationTokenSource cancellation)
    {
        try
        {
            await operation(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetStatus("Stopped.");
            return false;
        }
        catch (Exception error) when (error is CopilotException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or TimeoutException)
        {
            _errorMessage = error.Message;
            SetStatus(IsConnected ? "Copilot operation failed." : "Not connected.");
            // Transport exceptions may contain request data; never log their messages or payloads.
            Trace.TraceWarning("Copilot operation failed ({0}).", error.GetType().Name);
            return false;
        }
        finally
        {
            _isBusy = false;
            _activeCancellation = null;
            cancellation.Dispose();
            NotifyState();
        }
    }

    private void SetStatus(string status)
    {
        _status = status;
        Changed(nameof(Status));
    }

    private void NotifyState()
    {
        foreach (var name in new[] { nameof(IsBusy), nameof(CanConfigure), nameof(CanSend),
            nameof(IsConnected), nameof(CanDisconnect), nameof(ErrorMessage), nameof(Models),
            nameof(UserCode), nameof(VerificationUri), nameof(HasDeviceCode) })
        {
            Changed(name);
        }
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        RequestStop();
        if (_activeOperation is not null)
        {
            await _activeOperation;
        }

        await _connection.DisposeAsync();
        NotifyState();
    }
}
