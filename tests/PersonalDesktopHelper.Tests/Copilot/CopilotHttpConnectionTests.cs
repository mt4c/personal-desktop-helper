using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PersonalDesktopHelper.Copilot;

namespace PersonalDesktopHelper.Tests;

public sealed class CopilotHttpConnectionTests
{
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

        await connection.SignInAsync(new CopilotSettings(), value => challenge = value, CancellationToken.None);

        Assert.Equal("TEST-CODE", challenge!.UserCode);
        Assert.Equal("github.com", challenge.VerificationUri.Host);
        Assert.Equal("test-oauth", credential.Token);
        Assert.Equal(2, polls);
        Assert.True(connection.IsConnected);
        await connection.SignOutAsync();
        Assert.Null(credential.Token);
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

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class MemoryCredentials : ICopilotCredentialStore
    {
        public string? Token { get; set; }
        public string? Load() => Token;
        public void Save(string token) => Token = token;
        public void Clear() => Token = null;
    }
}
