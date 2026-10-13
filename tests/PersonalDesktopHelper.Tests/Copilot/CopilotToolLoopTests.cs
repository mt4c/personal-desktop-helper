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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopilotExecutesARealMcpTaskAndReceivesItsResult(bool responses)
    {
        var displayedReplies = new List<string>();
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

            Assert.Empty(displayedReplies);
            var task = Assert.Single(scheduler.GetTasks());
            var result = request.GetProperty("messages").EnumerateArray().Last();
            Assert.Equal("tool", result.GetProperty("role").GetString());
            Assert.Equal("create-1", result.GetProperty("tool_call_id").GetString());
            Assert.Contains(task.Id.ToString(), result.GetProperty("content").GetString());
            return Task.FromResult(Reply("Your reminder was created."));
        }, responses);
        await connection.ConnectAsync(new CopilotSettings { AdditionalSystemPrompt = "Use brief replies." }, CancellationToken.None);

        var result = await connection.SendAsync("Remind me hourly", displayedReplies.Add, CancellationToken.None);

        Assert.Equal("Your reminder was created.", result);
        Assert.Equal(["Your reminder was created."], displayedReplies);
        Assert.Equal("Take a break", Assert.Single(scheduler.GetTasks()).Definition.Parameters["message"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownToolsAndMalformedArgumentsAreReturnedAsErrorsWithoutExecution(bool responses)
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
        }, responses);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        await connection.SendAsync("Test", _ => { }, CancellationToken.None);

        Assert.Equal(0, tools.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedToolCallIdIsNotExecutedTwice(bool responses)
    {
        var tools = new FakeTools();
        await using var connection = CreateConnection(tools, (_, turn, _) => Task.FromResult(
            turn <= 2 ? ToolCalls(("same-id", "test_action", "{}")) : Reply("Done")), responses);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        await connection.SendAsync("Do it", _ => { }, CancellationToken.None);

        Assert.Equal(1, tools.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolFailureIsPassedBackToCopilot(bool responses)
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
        }, responses);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        Assert.Equal("The task was not found.", await connection.SendAsync("Delete it", _ => { }, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowupHttpFailurePreservesCompletedToolHistory(bool responses)
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
        }, responses);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);
        await Assert.ThrowsAsync<CopilotException>(() => connection.SendAsync("Do it", _ => { }, CancellationToken.None));

        await connection.SendAsync("What happened?", _ => { }, CancellationToken.None);

        Assert.Equal(1, tools.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPreservesUncertainOutcomeAndDoesNotRunRemainingCalls(bool responses)
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
        }, responses);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);
        var sending = connection.SendAsync("Do two actions", _ => { }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

        await connection.SendAsync("What happened?", _ => { }, CancellationToken.None);

        Assert.Equal(1, tools.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FollowupHttpCancellationPreservesCompletedToolHistory(bool responses)
    {
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tools = new FakeTools();
        await using var connection = CreateConnection(tools, async (request, turn, token) =>
        {
            if (turn == 1)
            {
                return ToolCalls(("completed-call", "test_action", "{}"));
            }
            if (turn == 2)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            var result = Assert.Single(request.GetProperty("messages").EnumerateArray(),
                message => message.GetProperty("role").GetString() == "tool");
            Assert.Equal("completed-call", result.GetProperty("tool_call_id").GetString());
            Assert.Contains("completed", result.GetProperty("content").GetString());
            return Reply("The action already completed.");
        }, responses);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);
        var sending = connection.SendAsync("Do it", _ => { }, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sending);

        Assert.Equal("The action already completed.",
            await connection.SendAsync("What happened?", _ => { }, CancellationToken.None));
        Assert.Equal(1, tools.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToolLoopIsBoundedAndRequestsAFinalAnswerWithoutMoreTools(bool responses)
    {
        var tools = new FakeTools();
        await using var connection = CreateConnection(tools, (request, turn, _) =>
        {
            if (turn == 9)
            {
                Assert.Equal("none", request.GetProperty("tool_choice").GetString());
            }
            return Task.FromResult(ToolCalls(($"call-{turn}", "test_action", "{}")));
        }, responses);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        Assert.Contains("tool limit", (await Assert.ThrowsAsync<CopilotException>(() =>
            connection.SendAsync("Test", _ => { }, CancellationToken.None))).Message);
        Assert.Equal(8, tools.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateIdsInOneResponseAreRejectedBeforeAnyAction(bool responses)
    {
        var tools = new FakeTools();
        await using var connection = CreateConnection(tools, (_, _, _) =>
            Task.FromResult(ToolCalls(("duplicate", "test_action", "{}"), ("duplicate", "test_action", "{}"))), responses);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        await Assert.ThrowsAsync<CopilotException>(() => connection.SendAsync("Test", _ => { }, CancellationToken.None));
        Assert.Equal(0, tools.CallCount);
    }

    [Fact]
    public async Task ResponsesReplaysReasoningAndFunctionItemsAndMapsResultsByCallId()
    {
        var tools = new FakeTools();
        var output = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "reasoning", id = "reason-1", encrypted_content = "opaque-reasoning", summary = Array.Empty<object>() },
            new { type = "function_call", id = "item-1", call_id = "call-1", name = "test_action", arguments = "{}" },
            new { type = "function_call", id = "item-2", call_id = "call-2", name = "test_action", arguments = "{}" }
        });
        await using var connection = CreateConnection(tools, (request, turn, _) =>
        {
            var tool = Assert.Single(request.GetProperty("tools").EnumerateArray());
            Assert.Equal("function", tool.GetProperty("type").GetString());
            Assert.Equal("test_action", tool.GetProperty("name").GetString());
            Assert.Equal("Test action", tool.GetProperty("description").GetString());
            Assert.Equal(tools.Tools[0].InputSchema.GetRawText(), tool.GetProperty("parameters").GetRawText());
            Assert.False(tool.GetProperty("strict").GetBoolean());
            Assert.False(tool.TryGetProperty("function", out var nestedFunction));
            Assert.Equal("auto", request.GetProperty("tool_choice").GetString());
            Assert.False(request.GetProperty("parallel_tool_calls").GetBoolean());
            if (turn == 1)
            {
                return Task.FromResult(Json(new { status = "completed", output }));
            }

            var input = request.GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal(6, input.Length);
            for (var index = 0; index < output.GetArrayLength(); index++)
            {
                Assert.Equal(output[index].GetRawText(), input[index + 1].GetRawText());
            }
            for (var index = 0; index < 2; index++)
            {
                var result = input[index + 4];
                Assert.Equal("function_call_output", result.GetProperty("type").GetString());
                Assert.Equal($"call-{index + 1}", result.GetProperty("call_id").GetString());
                using var payload = JsonDocument.Parse(result.GetProperty("output").GetString()!);
                Assert.Contains("completed", payload.RootElement.GetRawText());
            }
            return Task.FromResult(Json(new
            {
                status = "completed",
                output = new[] { new
                {
                    type = "message", id = "msg-1", role = "assistant",
                    content = new[] { new { type = "output_text", text = "Both actions completed." } }
                } }
            }));
        }, responses: true, normalizeResponses: false);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        Assert.Equal("Both actions completed.", await connection.SendAsync("Do both", _ => { }, CancellationToken.None));
        Assert.Equal(2, tools.CallCount);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    [InlineData("completed")]
    public async Task InvalidResponsesAreRejectedBeforeActionsAndDoNotLeakProviderData(string status)
    {
        var tools = new FakeTools();
        var output = new List<object>
        {
            new { type = "function_call", id = "item-1", call_id = "call-1", name = "test_action", arguments = "{}" }
        };
        if (status == "completed")
        {
            output.Add(new { type = "function_call", id = "item-2", name = "sensitive provider details", arguments = "{}" });
        }
        await using var connection = CreateConnection(tools, (_, _, _) =>
            Task.FromResult(Json(new
            {
                status,
                error = status == "completed" ? null : new { message = "sensitive provider details" },
                output
            })), responses: true, normalizeResponses: false);
        await connection.ConnectAsync(new CopilotSettings(), CancellationToken.None);

        var error = await Assert.ThrowsAsync<CopilotException>(() =>
            connection.SendAsync("Do both", _ => { }, CancellationToken.None));

        Assert.DoesNotContain("sensitive provider details", error.Message);
        Assert.Equal(0, tools.CallCount);
    }

    private static CopilotHttpConnection CreateConnection(
        IModuleToolClient tools, Func<JsonElement, int, CancellationToken, Task<HttpResponseMessage>> reply,
        bool responses = false, bool normalizeResponses = true)
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
                return Json(new { data = new[] { new
                {
                    id = "test-model", capabilities = new { type = "chat" },
                    supported_endpoints = new[] { responses ? "/responses" : "/chat/completions" }
                } } });
            }
            Assert.Equal(responses ? "/responses" : "/chat/completions", request.RequestUri.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var result = await reply(responses && normalizeResponses ? NormalizeResponseRequest(json.RootElement) : json.RootElement, ++turn, token);
            if (!responses || !normalizeResponses || !result.IsSuccessStatusCode)
            {
                return result;
            }

            using var completion = JsonDocument.Parse(await result.Content.ReadAsStringAsync(token));
            var message = completion.RootElement.GetProperty("choices")[0].GetProperty("message");
            var output = new List<object>();
            if (message.TryGetProperty("tool_calls", out var calls))
            {
                output.AddRange(calls.EnumerateArray().Select(call => (object)new
                {
                    type = "function_call", id = $"fc-{turn}-{call.GetProperty("id").GetString()}",
                    call_id = call.GetProperty("id").GetString(),
                    name = call.GetProperty("function").GetProperty("name").GetString(),
                    arguments = call.GetProperty("function").GetProperty("arguments").GetString()
                }));
            }
            else
            {
                output.Add(new
                {
                    type = "message", id = $"msg-{turn}", role = "assistant",
                    content = new[] { new { type = "output_text", text = message.GetProperty("content").GetString() } }
                });
            }
            result.Dispose();
            return Json(new { status = "completed", output });
        }));
        return new CopilotHttpConnection(new Credentials(), http, tools);
    }

    private static JsonElement NormalizeResponseRequest(JsonElement request)
    {
        var messages = new List<object>
        {
            new { role = "system", content = request.GetProperty("instructions").GetString() }
        };
        foreach (var item in request.GetProperty("input").EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var itemType) ? itemType.GetString() : "message";
            switch (type)
            {
                case "function_call_output":
                    messages.Add(new
                    {
                        role = "tool", tool_call_id = item.GetProperty("call_id").GetString(),
                        content = item.GetProperty("output").GetString()
                    });
                    break;
                case "message":
                    var content = item.GetProperty("content");
                    messages.Add(new
                    {
                        role = item.GetProperty("role").GetString(),
                        content = content.ValueKind == JsonValueKind.String ? content.GetString() :
                            string.Concat(content.EnumerateArray().Select(part => part.GetProperty("text").GetString()))
                    });
                    break;
            }
        }
        return JsonSerializer.SerializeToElement(new
        {
            messages,
            tools = request.GetProperty("tools").EnumerateArray().Select(tool => new { type = "function", function = tool }).ToArray(),
            tool_choice = request.GetProperty("tool_choice").GetString()
        });
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
        public bool HasCredential => true;
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
