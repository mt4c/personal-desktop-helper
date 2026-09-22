using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using PersonalDesktopHelper.Copilot;
using PersonalDesktopHelper.Mcp;
using PersonalDesktopHelper.Notifications;
using PersonalDesktopHelper.Scheduling;

namespace PersonalDesktopHelper.Tests;

public sealed class CopilotToolLoopTests
{
    [Fact]
    public async Task CopilotExecutesARealMcpTaskAndReceivesItsResult()
    {
        var notifications = new NotificationService(new TestNotificationSender());
        await using var scheduler = new Scheduler((_, error) => Assert.Fail(error.ToString()));
        scheduler.RegisterHandler(NotificationScheduledTask.HandlerId, new NotificationScheduledTask(notifications).RunAsync);
        await using var mcp = new LocalMcpConnection(new DesktopModuleTools(notifications, scheduler));
        await using var connection = CreateConnection(mcp, (request, turn, _) =>
        {
            Assert.Equal(SystemPromptDefinition.Compose(SystemPromptDefinition.BuildConstant(mcp.Tools), "Use brief replies."),
                request.GetProperty("messages")[0].GetProperty("content").GetString());
            Assert.Contains(request.GetProperty("tools").EnumerateArray(),
                tool => tool.GetProperty("function").GetProperty("name").GetString() == "scheduler_create_task");
            if (turn == 1)
            {
                return Task.FromResult(ToolCalls(("create-1", "scheduler_create_task",
                    """{"name":"MCP reminder","handlerId":"notification","scheduleKind":"interval","intervalMinutes":60,"parameters":{"title":"Reminder","message":"Take a break"}}""")));
            }

            var task = Assert.Single(scheduler.GetTasks());
            var result = request.GetProperty("messages").EnumerateArray().Last();
            Assert.Equal("tool", result.GetProperty("role").GetString());
            Assert.Equal("create-1", result.GetProperty("tool_call_id").GetString());
            Assert.Contains(task.Id.ToString(), result.GetProperty("content").GetString());
            return Task.FromResult(Reply("Your reminder was created."));
        });
        await connection.ConnectAsync(new CopilotSettings { AdditionalSystemPrompt = "Use brief replies." }, CancellationToken.None);

        var result = await connection.SendAsync("Remind me hourly", _ => { }, CancellationToken.None);

        Assert.Equal("Your reminder was created.", result);
        Assert.Equal("Take a break", Assert.Single(scheduler.GetTasks()).Definition.Parameters["message"]);
    }

    [Fact]
    public async Task UnknownToolsAndMalformedArgumentsAreReturnedAsErrorsWithoutExecution()
    {
        var tools = new FakeTools();
        await using var connection = CreateConnection(tools, (request, turn, _) =>
        {
            if (turn == 1)
            {
                return Task.FromResult(ToolCalls(("a", "unknown_tool", "{}"), ("b", "test_action", "{broken")));
            }

            var results = request.GetProperty("messages").EnumerateArray()
                .Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
            Assert.Equal(2, results.Length);
            Assert.All(results, message => Assert.Contains("\"isError\":true", message.GetProperty("content").GetString()));
            return Task.FromResult(Reply("Those actions were not executed."));
        });
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        await connection.SendAsync("Test", _ => { }, CancellationToken.None);

        Assert.Equal(0, tools.CallCount);
    }

    [Fact]
    public async Task RepeatedToolCallIdIsNotExecutedTwice()
    {
        var tools = new FakeTools();
        await using var connection = CreateConnection(tools, (_, turn, _) => Task.FromResult(
            turn <= 2 ? ToolCalls(("same-id", "test_action", "{}")) : Reply("Done")));
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        await connection.SendAsync("Do it", _ => { }, CancellationToken.None);

        Assert.Equal(1, tools.CallCount);
    }

    [Fact]
    public async Task ToolFailureIsPassedBackToCopilot()
    {
        var tools = new FakeTools
        {
            Handler = (_, _) => Task.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "The task does not exist." }]
            })
        };
        await using var connection = CreateConnection(tools, (request, turn, _) =>
        {
            if (turn == 1)
            {
                return Task.FromResult(ToolCalls(("a", "test_action", "{}")));
            }

            Assert.Contains("The task does not exist.", request.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString());
            return Task.FromResult(Reply("The task was not found."));
        });
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        Assert.Equal("The task was not found.", await connection.SendAsync("Delete it", _ => { }, CancellationToken.None));
    }

    [Fact]
    public async Task FollowupHttpFailurePreservesCompletedToolHistory()
    {
        var tools = new FakeTools();
        await using var connection = CreateConnection(tools, (request, turn, _) =>
        {
            if (turn == 1)
            {
                return Task.FromResult(ToolCalls(("a", "test_action", "{}")));
            }
            if (turn == 2)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            }

            Assert.Contains(request.GetProperty("messages").EnumerateArray(),
                message => message.GetProperty("role").GetString() == "tool");
            Assert.Contains("completed", request.GetRawText());
            return Task.FromResult(Reply("The prior action completed."));
        });
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);
        await Assert.ThrowsAsync<CopilotException>(() => connection.SendAsync("Do it", _ => { }, CancellationToken.None));

        await connection.SendAsync("What happened?", _ => { }, CancellationToken.None);

        Assert.Equal(1, tools.CallCount);
    }

    [Fact]
    public async Task CancellationPreservesUncertainOutcomeAndDoesNotRunRemainingCalls()
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new FakeTools
        {
            Handler = async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new CallToolResult();
            }
        };
        await using var connection = CreateConnection(tools, (request, turn, _) =>
        {
            if (turn == 1)
            {
                return Task.FromResult(ToolCalls(("active", "test_action", "{}"), ("not-started", "test_action", "{}")));
            }

            var history = request.GetRawText();
            Assert.Contains("may have completed", history);
            Assert.Contains("Not executed because", history);
            return Task.FromResult(Reply("Inspect the task state first."));
        });
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);
        var sending = connection.SendAsync("Do two actions", _ => { }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

        await connection.SendAsync("What happened?", _ => { }, CancellationToken.None);

        Assert.Equal(1, tools.CallCount);
    }

    [Fact]
    public async Task ToolLoopIsBoundedAndRequestsAFinalAnswerWithoutMoreTools()
    {
        var tools = new FakeTools();
        await using var connection = CreateConnection(tools, (request, turn, _) =>
        {
            if (turn == 9)
            {
                Assert.Equal("none", request.GetProperty("tool_choice").GetString());
            }
            return Task.FromResult(ToolCalls(($"call-{turn}", "test_action", "{}")));
        });
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        Assert.Contains("tool limit", (await Assert.ThrowsAsync<CopilotException>(() =>
            connection.SendAsync("Test", _ => { }, CancellationToken.None))).Message);
        Assert.Equal(8, tools.CallCount);
    }

    [Fact]
    public async Task DuplicateIdsInOneResponseAreRejectedBeforeAnyAction()
    {
        var tools = new FakeTools();
        await using var connection = CreateConnection(tools, (_, _, _) =>
            Task.FromResult(ToolCalls(("duplicate", "test_action", "{}"), ("duplicate", "test_action", "{}"))));
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        await Assert.ThrowsAsync<CopilotException>(() => connection.SendAsync("Test", _ => { }, CancellationToken.None));
        Assert.Equal(0, tools.CallCount);
    }

    private static CopilotHttpConnection CreateConnection(
        IModuleToolClient tools, Func<JsonElement, int, CancellationToken, Task<HttpResponseMessage>> reply)
    {
        var turn = 0;
        var http = new HttpClient(new Handler(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath == "/copilot_internal/v2/token")
            {
                return Json(new { token = "test-access", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds() });
            }
            if (request.RequestUri.AbsolutePath == "/models")
            {
                return Json(new { data = new[] { new { id = "test-model", capabilities = new { type = "chat" } } } });
            }
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            return await reply(json.RootElement, ++turn, token);
        }));
        return new CopilotHttpConnection(new Credentials(), http, tools);
    }

    private static HttpResponseMessage Reply(string text) =>
        Json(new { choices = new[] { new { message = new { role = "assistant", content = text } } } });

    private static HttpResponseMessage ToolCalls(params (string Id, string Name, string Arguments)[] calls) =>
        Json(new
        {
            choices = new[] { new { message = new
            {
                role = "assistant", content = (string?)null,
                tool_calls = calls.Select(call => new { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments } }).ToArray()
            } } }
        });

    private static HttpResponseMessage Json(object content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(content), Encoding.UTF8, "application/json")
    };

    private sealed class Credentials : ICopilotCredentialStore
    {
        public string Load() => "test-oauth";
        public void Save(string token) { }
        public void Clear() { }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class FakeTools : IModuleToolClient
    {
        public int CallCount { get; private set; }
        public IReadOnlyList<Tool> Tools { get; } =
        [
            new()
            {
                Name = "test_action", Description = "Test action",
                InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })
            }
        ];
        public Func<JsonElement, CancellationToken, Task<CallToolResult>> Handler { get; init; } =
            (_, _) => Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "completed" }] });
        public Task<CallToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            CallCount++;
            return Handler(arguments, cancellationToken);
        }
    }

    private sealed class TestNotificationSender : INotificationSender
    {
        public Task SendAsync(string title, string message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
