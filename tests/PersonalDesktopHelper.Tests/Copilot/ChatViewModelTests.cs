using PersonalDesktopHelper.Copilot;

namespace PersonalDesktopHelper.Tests;

public sealed class ChatViewModelTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task StartupConnectsOnlyWithSavedSignInAndUsesPersistedModel(bool signedIn, int expectedConnections)
    {
        using var directory = new TemporaryDirectory();
        var store = new Persistence.JsonStateStore(directory.StatePath);
        store.SetCopilotSettings(new CopilotSettings { Model = "chosen-model" });
        var loaded = new Persistence.JsonStateStore(directory.StatePath);
        var connection = new FakeConnection { IsSignedIn = signedIn };
        await using var chat = new ChatViewModel(
            connection, loaded.State.Copilot, loaded.SetCopilotSettings, action => action());

        await chat.ConnectOnStartupAsync();

        Assert.Equal(expectedConnections, connection.ConnectCount);
        Assert.Equal(signedIn, chat.IsConnected);
        Assert.Equal("", chat.ErrorMessage);
        if (signedIn)
        {
            Assert.Equal("chosen-model", connection.LastConnectSettings!.Model);
            await chat.ConnectOnStartupAsync();
            Assert.Equal(1, connection.ConnectCount);
        }
        else
        {
            Assert.True(chat.CanSignIn);
        }
    }

    [Fact]
    public async Task StartupConnectionFailureIsVisibleAndCanBeRetried()
    {
        var connection = new FakeConnection { ConnectError = new CopilotException("Network unavailable.") };
        await using var chat = CreateChat(connection);

        await chat.ConnectOnStartupAsync();

        Assert.Equal("Network unavailable.", chat.ErrorMessage);
        Assert.False(chat.IsConnected);
        Assert.True(chat.CanConnect);
        Assert.False(chat.IsBusy);
    }

    [Fact]
    public async Task AuthenticationAndConnectionActionsFollowStateAndNotifyBindings()
    {
        var connection = new FakeConnection { IsSignedIn = false };
        await using var chat = CreateChat(connection);
        var changed = new HashSet<string?>();
        chat.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        chat.Prompt = "Draft";
        Assert.True(chat.CanSignIn);
        Assert.False(chat.CanSignOut);
        Assert.False(chat.CanConnect);
        Assert.False(chat.CanDisconnect);
        Assert.False(chat.CanSend);
        Assert.Equal("Not signed in", chat.SignInStatus);
        Assert.Equal("Not connected", chat.ConnectionStatus);
        Assert.Equal("Sign in", chat.SetupActionText);

        Assert.True(await chat.SignInAsync());
        Assert.True(chat.IsSignedIn);
        Assert.True(chat.IsConnected);
        Assert.False(chat.CanSignIn);
        Assert.True(chat.CanSignOut);
        Assert.False(chat.CanConnect);
        Assert.True(chat.CanDisconnect);
        Assert.True(chat.CanSend);
        Assert.False(chat.CanSetUp);
        Assert.Equal("Connected", chat.ConnectionStatus);

        Assert.True(await chat.DisconnectAsync());
        Assert.True(chat.IsSignedIn);
        Assert.True(chat.CanConnect);
        Assert.False(chat.CanDisconnect);
        Assert.Equal("Connect", chat.SetupActionText);
        Assert.Contains("saved sign-in", chat.SetupGuidance);
        Assert.True(await chat.ConnectAsync());

        Assert.True(await chat.SignOutAsync());
        Assert.False(chat.IsSignedIn);
        Assert.False(chat.IsConnected);
        Assert.True(chat.CanSignIn);
        Assert.False(chat.CanSend);
        Assert.Equal("Draft", chat.Prompt);
        Assert.Contains(nameof(chat.SignInStatus), changed);
        Assert.Contains(nameof(chat.ConnectionStatus), changed);
        Assert.Contains(nameof(chat.CanSignIn), changed);
        Assert.Contains(nameof(chat.CanSignOut), changed);
        Assert.Contains(nameof(chat.CanConnect), changed);
        Assert.Contains(nameof(chat.SetupActionText), changed);
    }

    [Fact]
    public async Task CancelledSignInClearsDeviceCodeAndRestoresSignInAction()
    {
        var connection = new FakeConnection
        {
            IsSignedIn = false,
            SignInHandler = async (authorization, token) =>
            {
                authorization(new DeviceAuthorization("TEST", new Uri("https://github.com/login/device")));
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        await using var chat = CreateChat(connection);
        var signingIn = chat.SignInAsync();
        Assert.Equal("Signing in...", chat.SignInStatus);
        Assert.True(chat.HasDeviceCode);
        Assert.False(chat.CanSignIn);
        Assert.False(chat.CanSignOut);
        Assert.False(chat.CanConnect);
        Assert.False(chat.CanDisconnect);
        Assert.False(chat.CanSetUp);

        chat.RequestStop();

        Assert.False(await signingIn);
        Assert.False(chat.HasDeviceCode);
        Assert.False(chat.IsSignedIn);
        Assert.True(chat.CanSignIn);
        Assert.Equal("Not connected", chat.ConnectionStatus);
    }

    [Fact]
    public async Task FailedConnectionPreservesSavedSignInAndAllowsRetry()
    {
        var connection = new FakeConnection
        {
            ConnectHandler = async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        await using var chat = CreateChat(connection);
        var connecting = chat.ConnectAsync();
        Assert.Equal("Connecting...", chat.ConnectionStatus);
        Assert.Contains("Signed in", chat.SignInStatus);
        Assert.False(chat.CanConnect);
        Assert.False(chat.CanSignOut);
        chat.RequestStop();
        Assert.False(await connecting);
        Assert.True(chat.CanConnect);
        Assert.True(chat.CanSignOut);

        var failing = new FakeConnection { ConnectError = new CopilotException("Network unavailable.") };
        await using var failedChat = CreateChat(failing);
        Assert.False(await failedChat.ConnectAsync());
        Assert.True(failedChat.IsSignedIn);
        Assert.False(failedChat.IsConnected);
        Assert.True(failedChat.CanConnect);
        Assert.True(failedChat.CanSignOut);
        Assert.Equal("Not connected", failedChat.ConnectionStatus);
        Assert.Equal("Network unavailable.", failedChat.ErrorMessage);
    }

    [Fact]
    public async Task SendConnectsStreamsAndPreservesConversation()
    {
        var connection = new FakeConnection();
        await using var chat = CreateChat(connection);
        connection.SendHandler = (prompt, update, _) =>
        {
            update("Partial");
            Assert.Equal("Partial", chat.Messages.Last().Content);
            return Task.FromResult($"Reply to {prompt}");
        };
        chat.Prompt = "Hello";
        Assert.True(await chat.SendAsync());
        Assert.Equal(2, chat.Messages.Count);
        Assert.Equal("Hello", chat.Messages[0].Content);
        Assert.Equal("Reply to Hello", chat.Messages[1].Content);
        Assert.Equal("", chat.Prompt);

        chat.Prompt = "Follow up";
        Assert.True(await chat.SendAsync());
        Assert.Equal(1, connection.ConnectCount);
        Assert.Equal(4, chat.Messages.Count);
        Assert.False(chat.IsBusy);
    }

    [Fact]
    public async Task ConnectionFailureKeepsDraftAndShowsError()
    {
        var connection = new FakeConnection { ConnectError = new CopilotException("Please sign in to GitHub.") };
        await using var chat = CreateChat(connection);
        chat.Prompt = "Unsent draft";

        Assert.False(await chat.SendAsync());

        Assert.Equal("Unsent draft", chat.Prompt);
        Assert.Empty(chat.Messages);
        Assert.Contains("sign in", chat.ErrorMessage);
        Assert.True(chat.CanSend);
    }

    [Fact]
    public async Task StopCancelsSendAndLeavesTheUiUsable()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeConnection
        {
            SendHandler = async (_, update, token) =>
            {
                update("Partial response");
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "";
            }
        };
        await using var chat = CreateChat(connection);
        chat.Prompt = "Hello";
        var sending = chat.SendAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(chat.IsBusy);
        Assert.False(chat.CanConfigure);

        chat.RequestStop();

        Assert.False(await sending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("Partial response", chat.Messages.Last().Content);
        Assert.Equal("Stopped.", chat.Status);
        Assert.True(chat.CanConfigure);
    }

    [Fact]
    public async Task QueuedStreamingUpdatesCannotOverwriteTheFinalReply()
    {
        var queued = new List<Action>();
        var connection = new FakeConnection
        {
            SendHandler = (_, update, _) =>
            {
                update("Old partial");
                return Task.FromResult("Final response");
            }
        };
        await using var chat = new ChatViewModel(connection, new CopilotSettings(), _ => { }, queued.Add);
        chat.Prompt = "Hello";
        await chat.SendAsync();
        foreach (var update in queued)
        {
            update();
        }

        Assert.Equal("Final response", chat.Messages.Last().Content);
    }

    [Fact]
    public async Task SavingSettingsDisconnectsAndNewChatClearsTranscript()
    {
        var connection = new FakeConnection();
        CopilotSettings? saved = null;
        await using var chat = new ChatViewModel(connection, new CopilotSettings(), value => saved = value, action => action());
        chat.Prompt = "Hello";
        await chat.SendAsync();
        await chat.NewChatAsync();
        Assert.Empty(chat.Messages);
        Assert.Equal(1, connection.NewChatCount);

        var settings = new CopilotSettings { Model = "chosen-model" };
        Assert.True(await chat.ApplySettingsAsync(settings));
        Assert.Equal(settings, saved);
        Assert.Equal(settings, chat.Settings);
        Assert.False(chat.IsConnected);
    }

    [Fact]
    public async Task FailedSaveDoesNotApplySettingsOrDisconnect()
    {
        var connection = new FakeConnection();
        await using var chat = new ChatViewModel(
            connection, new CopilotSettings(), _ => throw new IOException("Disk unavailable"), action => action());
        await chat.ConnectAsync();

        Assert.False(await chat.ApplySettingsAsync(new CopilotSettings { Model = "new-model" }));

        Assert.Equal(new CopilotSettings(), chat.Settings);
        Assert.True(chat.IsConnected);
        Assert.Contains("Disk unavailable", chat.ErrorMessage);
    }

    [Fact]
    public async Task DisposalStopsTheActiveRequestAndDisposesTheConnection()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new FakeConnection
        {
            SendHandler = async (_, _, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "";
            }
        };
        var chat = CreateChat(connection);
        chat.Prompt = "Hello";
        var send = chat.SendAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await chat.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(connection.IsDisposed);
        Assert.False(await send);
        Assert.False(chat.CanConfigure);
    }

    private static ChatViewModel CreateChat(FakeConnection connection) =>
        new(connection, new CopilotSettings(), _ => { }, action => action());

    internal sealed class FakeConnection : ICopilotConnection
    {
        public bool IsSignedIn { get; set; } = true;
        public bool IsConnected { get; private set; }
        public IReadOnlyList<string> Models { get; init; } = ["test-model"];
        public CopilotSettings? LastConnectSettings { get; private set; }
        public bool IsDisposed { get; private set; }
        public int ConnectCount { get; private set; }
        public int NewChatCount { get; private set; }
        public CopilotException? ConnectError { get; init; }
        public Func<CancellationToken, Task>? ConnectHandler { get; init; }
        public Func<Action<DeviceAuthorization>, CancellationToken, Task>? SignInHandler { get; init; }
        public Func<string, Action<string>, CancellationToken, Task<string>> SendHandler { get; set; } =
            (_, _, _) => Task.FromResult("Reply");

        public async Task<string> ConnectAsync(CopilotSettings settings, CancellationToken cancellationToken)
        {
            LastConnectSettings = settings;
            if (ConnectError is not null)
            {
                throw ConnectError;
            }

            ConnectCount++;
            if (ConnectHandler is not null)
            {
                await ConnectHandler(cancellationToken);
            }
            IsConnected = true;
            return "Connected.";
        }

        public async Task<string> SignInAsync(CopilotSettings settings, Action<DeviceAuthorization> authorizationRequested, CancellationToken cancellationToken)
        {
            if (SignInHandler is not null)
            {
                await SignInHandler(authorizationRequested, cancellationToken);
            }
            IsSignedIn = true;
            return await ConnectAsync(settings, cancellationToken);
        }

        public Task SignOutAsync()
        {
            IsSignedIn = false;
            return DisconnectAsync();
        }

        public Task<string> SendAsync(string prompt, Action<string> responseChanged, CancellationToken cancellationToken) =>
            SendHandler(prompt, responseChanged, cancellationToken);

        public Task NewChatAsync(CancellationToken cancellationToken)
        {
            NewChatCount++;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync()
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }
}
