using PersonalDesktopHelper.Copilot;

namespace PersonalDesktopHelper.Tests;

public sealed class ChatViewModelTests
{
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

    private sealed class FakeConnection : ICopilotConnection
    {
        public bool IsConnected { get; private set; }
        public IReadOnlyList<string> Models => ["test-model"];
        public bool IsDisposed { get; private set; }
        public int ConnectCount { get; private set; }
        public int NewChatCount { get; private set; }
        public CopilotException? ConnectError { get; init; }
        public Func<string, Action<string>, CancellationToken, Task<string>> SendHandler { get; set; } =
            (_, _, _) => Task.FromResult("Reply");

        public Task<string> ConnectAsync(CopilotSettings settings, CancellationToken cancellationToken)
        {
            if (ConnectError is not null)
            {
                throw ConnectError;
            }

            ConnectCount++;
            IsConnected = true;
            return Task.FromResult("Connected.");
        }

        public Task<string> SignInAsync(CopilotSettings settings, Action<DeviceAuthorization> authorizationRequested, CancellationToken cancellationToken) =>
            ConnectAsync(settings, cancellationToken);

        public Task SignOutAsync() => DisconnectAsync();

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
