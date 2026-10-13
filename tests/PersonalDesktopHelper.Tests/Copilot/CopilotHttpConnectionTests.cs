using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PersonalDesktopHelper.Copilot;

namespace PersonalDesktopHelper.Tests;

public sealed class CopilotHttpConnectionTests
{
    [Theory]
    [InlineData(null, "/chat/completions")]
    [InlineData("""["/chat/completions"]""", "/chat/completions")]
    [InlineData("""["/responses","/chat/completions"]""", "/chat/completions")]
    [InlineData("""["/responses"]""", "/responses")]
    public async Task ModelEndpointMetadataSelectsTheSupportedProtocol(string? endpoints, string expectedPath)
    {
        using var http = CreateHttp((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/copilot_internal/v2/token")
            {
                return Task.FromResult(AccessToken());
            }
            if (request.RequestUri.AbsolutePath == "/models")
            {
                var metadata = endpoints is null ? "" : $""","supported_endpoints":{endpoints}""";
                return Task.FromResult(Json(
                    $$"""{"data":[{"id":"test-model","capabilities":{"type":"chat"}{{metadata}}},{"id":"unsupported","capabilities":{"type":"chat"},"supported_endpoints":["/embeddings"]}]}"""));
            }

            Assert.Equal(expectedPath, request.RequestUri.AbsolutePath);
            return Task.FromResult(expectedPath == "/responses" ? ResponseReply() : Completion("Reply"));
        });
        await using var connection = new CopilotHttpConnection(new MemoryCredentials { Token = "test" }, http);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        Assert.Equal(["test-model"], connection.Models);
        Assert.Equal("Reply", await connection.SendAsync("Hello", _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task ModelWithOnlyUnsupportedEndpointsCannotConnect()
    {
        using var http = CreateHttp((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/copilot_internal/v2/token" ? AccessToken() :
            Json("""{"data":[{"id":"unsupported","capabilities":{"type":"chat"},"supported_endpoints":["/embeddings"]}]}""")));
        await using var connection = new CopilotHttpConnection(new MemoryCredentials { Token = "test" }, http);

        await Assert.ThrowsAsync<CopilotException>(() =>
            connection.ConnectAsync(new CopilotSettings { Model = "unsupported" }, CancellationToken.None));

        Assert.False(connection.IsConnected);
        Assert.Empty(connection.Models);
    }

    [Fact]
    public async Task ResponsesConversationReplaysOriginalOutputAndNewChatClearsHistory()
    {
        const string output = """[{"type":"reasoning","id":"reason-1","encrypted_content":"opaque-reasoning","summary":[]},{"type":"message","role":"assistant","id":"msg-1","content":[{"type":"output_text","text":"Reply","annotations":[]}]}]""";
        var requests = new List<JsonElement>();
        using var http = CreateHttp(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/copilot_internal/v2/token")
            {
                return AccessToken();
            }
            if (request.RequestUri.AbsolutePath == "/models")
            {
                return Json("""{"data":[{"id":"test-model","capabilities":{"type":"chat"},"supported_endpoints":["/responses"]}]}""");
            }

            Assert.Equal("/responses", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            requests.Add(body.RootElement.Clone());
            return Json($$"""{"status":"completed","output":{{output}}}""");
        });
        await using var connection = new CopilotHttpConnection(new MemoryCredentials { Token = "test" }, http);
        await connection.ConnectAsync(new CopilotSettings { AdditionalSystemPrompt = "Use brief replies." }, CancellationToken.None);

        Assert.Equal("Reply", await connection.SendAsync("Hello", _ => { }, CancellationToken.None));
        await connection.SendAsync("Follow up", _ => { }, CancellationToken.None);
        await connection.NewChatAsync(CancellationToken.None);
        await connection.SendAsync("Fresh chat", _ => { }, CancellationToken.None);

        var first = requests[0];
        Assert.Equal("test-model", first.GetProperty("model").GetString());
        Assert.Equal(SystemPromptDefinition.Compose(SystemPromptDefinition.BuildConstant([]), "Use brief replies."),
            first.GetProperty("instructions").GetString());
        Assert.False(first.GetProperty("stream").GetBoolean());
        Assert.False(first.GetProperty("store").GetBoolean());
        Assert.Equal(["reasoning.encrypted_content"], first.GetProperty("include").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("Hello", Assert.Single(first.GetProperty("input").EnumerateArray()).GetProperty("content").GetString());
        var history = requests[1].GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(4, history.Length);
        Assert.Equal("user", history[0].GetProperty("role").GetString());
        using var original = JsonDocument.Parse(output);
        Assert.Equal(original.RootElement[0].GetRawText(), history[1].GetRawText());
        Assert.Equal(original.RootElement[1].GetRawText(), history[2].GetRawText());
        Assert.Equal("Follow up", history[3].GetProperty("content").GetString());
        Assert.Equal("Fresh chat", Assert.Single(requests[2].GetProperty("input").EnumerateArray()).GetProperty("content").GetString());
    }

    [Fact]
    public async Task ConnectExchangesCredentialFiltersModelsAndSendsConversationHistory()
    {
        var credential = new MemoryCredentials { Token = "test-oauth" };
        var requests = new List<JsonElement>();
        using var http = CreateHttp(async (request, token) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/copilot_internal/v2/token":
                    Assert.Equal("token", request.Headers.Authorization!.Scheme);
                    Assert.Equal("test-oauth", request.Headers.Authorization.Parameter);
                    return AccessToken();
                case "/models":
                    Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                    return Models();
                default:
                    Assert.Equal("/chat/completions", request.RequestUri.AbsolutePath);
                    Assert.Equal("vscode-chat", request.Headers.GetValues("Copilot-Integration-Id").Single());
                    using (var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)))
                    {
                        requests.Add(body.RootElement.Clone());
                    }
                    return Completion("A reply");
            }
        });
        await using var connection = new CopilotHttpConnection(credential, http);
        Assert.True(connection.IsSignedIn);
        Assert.False(connection.IsConnected);
        Assert.Contains("test-model", await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None));
        Assert.Equal(["test-model"], connection.Models);
        Assert.Equal("A reply", await connection.SendAsync("Hello", _ => { }, CancellationToken.None));
        await connection.SendAsync("Follow up", _ => { }, CancellationToken.None);

        Assert.Equal(2, requests[0].GetProperty("messages").GetArrayLength());
        Assert.Equal(4, requests[1].GetProperty("messages").GetArrayLength());
        Assert.Equal("assistant", requests[1].GetProperty("messages")[2].GetProperty("role").GetString());
        Assert.False(requests[0].GetProperty("stream").GetBoolean());
        await connection.NewChatAsync(CancellationToken.None);
        await connection.SendAsync("Fresh chat", _ => { }, CancellationToken.None);
        Assert.Equal(2, requests[2].GetProperty("messages").GetArrayLength());
        await connection.DisconnectAsync();
        Assert.True(connection.IsSignedIn);
        Assert.False(connection.IsConnected);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);
        Assert.True(connection.IsConnected);
        await connection.SignOutAsync();
        Assert.False(connection.IsSignedIn);
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task UnauthorizedApiRequestRefreshesTokenOnlyOnce()
    {
        var exchanges = 0;
        var models = 0;
        using var http = CreateHttp((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/copilot_internal/v2/token")
            {
                exchanges++;
                return Task.FromResult(AccessToken());
            }

            models++;
            return Task.FromResult(models == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Models());
        });
        await using var connection = new CopilotHttpConnection(new MemoryCredentials { Token = "test" }, http);

        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        Assert.Equal(2, exchanges);
        Assert.Equal(2, models);
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task ExpiringTokenIsRefreshedBeforeSending()
    {
        var exchanges = 0;
        using var http = CreateHttp((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/copilot_internal/v2/token" => AccessToken(++exchanges == 1 ? 60 : 3600),
            "/models" => Models(),
            _ => Completion("Reply")
        }));
        await using var connection = new CopilotHttpConnection(new MemoryCredentials { Token = "test" }, http);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        await connection.SendAsync("Hello", _ => { }, CancellationToken.None);

        Assert.Equal(2, exchanges);
    }

    [Fact]
    public async Task DeviceSignInHandlesPendingAndPersistsOnlyOAuthCredential()
    {
        var polls = 0;
        var credential = new MemoryCredentials();
        DeviceAuthorization? challenge = null;
        using var http = CreateHttp((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/login/device/code" => Json("""{"device_code":"device","user_code":"TEST-CODE","verification_uri":"https://github.com/login/device","interval":1,"expires_in":60}"""),
            "/login/oauth/access_token" => ++polls == 1
                ? Json("""{"error":"authorization_pending"}""")
                : Json("""{"access_token":"test-oauth"}"""),
            "/copilot_internal/v2/token" => AccessToken(),
            _ => Models()
        }));
        await using var connection = new CopilotHttpConnection(credential, http);
        Assert.False(connection.IsSignedIn);

        await connection.SignInAsync(new CopilotSettings(), value => challenge = value, CancellationToken.None);

        Assert.Equal("TEST-CODE", challenge!.UserCode);
        Assert.Equal("github.com", challenge.VerificationUri.Host);
        Assert.Equal("test-oauth", credential.Token);
        Assert.Equal(2, polls);
        Assert.True(connection.IsSignedIn);
        Assert.True(connection.IsConnected);
        await connection.SignOutAsync();
        Assert.Null(credential.Token);
        Assert.False(connection.IsSignedIn);
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task DeviceFlowCancellationDoesNotSaveCredentials()
    {
        var credential = new MemoryCredentials();
        using var cancellation = new CancellationTokenSource();
        using var http = CreateHttp((_, _) => Task.FromResult(Json(
            """{"device_code":"device","user_code":"TEST-CODE","verification_uri":"https://github.com/login/device","interval":5,"expires_in":60}""")));
        await using var connection = new CopilotHttpConnection(credential, http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.SignInAsync(
            new CopilotSettings(), _ => cancellation.Cancel(), cancellation.Token));

        Assert.Null(credential.Token);
        Assert.False(connection.IsSignedIn);
    }

    [Fact]
    public async Task UntrustedDeviceVerificationUrlIsRejected()
    {
        using var http = CreateHttp((_, _) => Task.FromResult(Json(
            """{"device_code":"device","user_code":"TEST","verification_uri":"https://example.com/login","interval":1,"expires_in":60}""")));
        await using var connection = new CopilotHttpConnection(new MemoryCredentials(), http);

        await Assert.ThrowsAsync<CopilotException>(() =>
            connection.SignInAsync(new CopilotSettings(), _ => Assert.Fail("Must not open untrusted URL"), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationReachesHttpAndDoesNotAddFailedTurnToHistory()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var http = CreateHttp(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/copilot_internal/v2/token")
            {
                return AccessToken();
            }
            if (request.RequestUri.AbsolutePath == "/models")
            {
                return Models();
            }
            if (++calls == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal(2, body.RootElement.GetProperty("messages").GetArrayLength());
            return Completion("Reply");
        });
        await using var connection = new CopilotHttpConnection(new MemoryCredentials { Token = "test" }, http);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);
        var sending = connection.SendAsync("Cancelled", _ => { }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);
        await connection.SendAsync("New request", _ => { }, CancellationToken.None);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "access was denied")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limiting")]
    public async Task ApiErrorsAreActionableAndDoNotExposeResponseBody(HttpStatusCode status, string message)
    {
        using var http = CreateHttp((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("sensitive response body")
        }));
        await using var connection = new CopilotHttpConnection(new MemoryCredentials { Token = "test" }, http);

        var error = await Assert.ThrowsAsync<CopilotException>(() =>
            connection.ConnectAsync(new CopilotSettings(), CancellationToken.None));

        Assert.Contains(message, error.Message);
        Assert.DoesNotContain("sensitive", error.Message);
    }

    [Fact]
    public async Task MissingCredentialsAndUnavailableModelsAreExplicitErrors()
    {
        using var http = CreateHttp((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath == "/models" ? Models() : AccessToken()));
        var credentials = new MemoryCredentials();
        await using var connection = new CopilotHttpConnection(credentials, http);
        Assert.Contains("Sign in", (await Assert.ThrowsAsync<CopilotException>(() =>
            connection.ConnectAsync(new CopilotSettings(), CancellationToken.None))).Message);
        credentials.Token = "test";
        Assert.Contains("unavailable", (await Assert.ThrowsAsync<CopilotException>(() =>
            connection.ConnectAsync(new CopilotSettings { Model = "not-available" }, CancellationToken.None))).Message);
        Assert.False(connection.IsConnected);
        Assert.Equal(["test-model"], connection.Models);
    }

    private static HttpClient CreateHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new StubHandler(respond));

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage AccessToken(int lifetimeSeconds = 3600) => Json(JsonSerializer.Serialize(new
    {
        token = "test-copilot-access",
        expires_at = DateTimeOffset.UtcNow.AddSeconds(lifetimeSeconds).ToUnixTimeSeconds()
    }));

    private static HttpResponseMessage Models() => Json(
        """{"data":[{"id":"test-model","capabilities":{"type":"chat"}},{"id":"embedding-model","capabilities":{"type":"embedding"}}]}""");

    private static HttpResponseMessage Completion(string content) =>
        Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } }));

    private static HttpResponseMessage ResponseReply() => Json(
        """{"status":"completed","output":[{"type":"message","role":"assistant","id":"msg-1","content":[{"type":"output_text","text":"Reply"}]}]}""");

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class MemoryCredentials : ICopilotCredentialStore
    {
        public string? Token { get; set; }
        public bool HasCredential => Token is not null;
        public string? Load() => Token;
        public void Save(string token) => Token = token;
        public void Clear() => Token = null;
    }
}
