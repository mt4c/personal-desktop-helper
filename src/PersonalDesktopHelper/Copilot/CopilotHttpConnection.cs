using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using PersonalDesktopHelper.Mcp;

namespace PersonalDesktopHelper.Copilot;

public sealed class CopilotHttpConnection : ICopilotConnection
{
    private readonly HttpClient _http;
    private readonly ICopilotCredentialStore _credentials;
    private readonly GitHubDeviceAuthClient _auth;
    private readonly List<ApiMessage> _history = [];
    private string? _oauthToken;
    private CopilotAccessToken? _access;
    private string? _model;
    private bool _disposed;
    private readonly IModuleToolClient? _moduleTools;
    private string _systemPrompt;
    private const int MaxToolRounds = 8;
    private const int MaxCallsPerRound = 8;

    public CopilotHttpConnection(ICopilotCredentialStore credentials, HttpClient? http = null, IModuleToolClient? moduleTools = null)
    {
        _credentials = credentials;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        _auth = new GitHubDeviceAuthClient(_http);
        _moduleTools = moduleTools;
        _systemPrompt = SystemPromptDefinition.BuildConstant(moduleTools?.Tools ?? []);
    }

    public bool IsConnected { get; private set; }
    public IReadOnlyList<string> Models { get; private set; } = [];

    public Task<string> SignInAsync(
        CopilotSettings settings, Action<DeviceAuthorization> authorizationRequested, CancellationToken cancellationToken) =>
        WithErrorsAsync(async () =>
        {
            settings.Validate();
            _history.Clear();
            var oauth = await _auth.SignInAsync(authorizationRequested, cancellationToken).ConfigureAwait(false);
            var access = await _auth.ExchangeAsync(oauth, cancellationToken).ConfigureAwait(false);
            _credentials.Save(oauth);
            _oauthToken = oauth;
            _access = access;
            _history.Clear();
            return await ConfigureAsync(settings, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<string> ConnectAsync(CopilotSettings settings, CancellationToken cancellationToken) =>
        WithErrorsAsync(async () =>
        {
            settings.Validate();
            _oauthToken ??= _credentials.Load()
                ?? throw new CopilotException("Sign in to GitHub in Options > Copilot first.");
            return await ConfigureAsync(settings, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<string> ConfigureAsync(CopilotSettings settings, CancellationToken token)
    {
        IsConnected = false;
        using var response = await SendAuthorizedAsync(() =>
            ApiRequest(HttpMethod.Get, "https://api.githubcopilot.com/models"), token).ConfigureAwait(false);
        var models = await response.Content.ReadFromJsonAsync<ModelList>(token).ConfigureAwait(false);
        Models = models?.Data?.Where(model => model is not null && model.Capabilities?.Type == "chat" && !string.IsNullOrWhiteSpace(model.Id))
            .Select(model => model.Id!).Distinct().Order(StringComparer.Ordinal).ToArray() ?? [];
        if (Models.Count == 0)
        {
            throw new CopilotException("No chat models are available for this Copilot account.");
        }

        _model = string.IsNullOrWhiteSpace(settings.Model)
            ? (Models.Contains("gpt-4o") ? "gpt-4o" : Models[0])
            : settings.Model;
        if (!Models.Contains(_model))
        {
            throw new CopilotException($"Model '{_model}' is unavailable. Choose one of the available models in Options.");
        }

        IsConnected = true;
        _systemPrompt = SystemPromptDefinition.Compose(
            SystemPromptDefinition.BuildConstant(_moduleTools?.Tools ?? []), settings.AdditionalSystemPrompt);
        return $"Connected to GitHub Copilot using {_model}.";
    }

    public Task<string> SendAsync(string prompt, Action<string> responseChanged, CancellationToken cancellationToken) =>
        WithErrorsAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
            if (!IsConnected || _model is null)
            {
                throw new CopilotException("Connect to Copilot before sending a message.");
            }

            return await RunTurnAsync(prompt, responseChanged, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<string> RunTurnAsync(string prompt, Action<string> responseChanged, CancellationToken token)
    {
        var turn = new List<ApiMessage> { new("user", prompt) };
        var hasToolCalls = false;
        var committed = false;
        var cachedCalls = new Dictionary<string, (FunctionCall Call, string Result)>(StringComparer.Ordinal);
        var tools = _moduleTools?.Tools ?? [];
        try
        {
            for (var round = 0; round <= MaxToolRounds; round++)
            {
                token.ThrowIfCancellationRequested();
                var messages = new List<ApiMessage> { new("system", _systemPrompt) };
                messages.AddRange(_history);
                messages.AddRange(turn);
                using var response = await SendAuthorizedAsync(() =>
                {
                    var request = ApiRequest(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions");
                    request.Content = JsonContent.Create(tools.Count == 0
                        ? (object)new { model = _model, messages, stream = false }
                        : new
                        {
                            model = _model, messages, stream = false,
                            tools = tools.Select(tool => new
                            {
                                type = "function",
                                function = new { name = tool.Name, description = tool.Description, parameters = tool.InputSchema }
                            }).ToArray(),
                            tool_choice = round == MaxToolRounds ? "none" : "auto",
                            parallel_tool_calls = false
                        });
                    return request;
                }, token).ConfigureAwait(false);
                var completion = await response.Content.ReadFromJsonAsync<Completion>(token).ConfigureAwait(false);
                var message = completion?.Choices?.FirstOrDefault()?.Message
                    ?? throw new CopilotException("Copilot returned no assistant message.");
                token.ThrowIfCancellationRequested();
                if (message.ToolCalls is not { Count: > 0 } calls)
                {
                    if (string.IsNullOrWhiteSpace(message.Content))
                    {
                        throw new CopilotException("Copilot returned no text. Try another message or model.");
                    }

                    turn.Add(new ApiMessage("assistant", message.Content));
                    _history.AddRange(turn);
                    committed = true;
                    responseChanged(message.Content);
                    return message.Content;
                }

                if (round == MaxToolRounds || calls.Count > MaxCallsPerRound)
                {
                    throw new CopilotException("The per-message tool limit was reached. Completed actions remain applied; inspect their state before continuing.");
                }

                if (calls.Any(call => call is null || string.IsNullOrWhiteSpace(call.Id) || call.Type != "function" ||
                    call.Function is null || string.IsNullOrWhiteSpace(call.Function.Name) || call.Function.Arguments is null) ||
                    calls.Select(call => call.Id).Distinct(StringComparer.Ordinal).Count() != calls.Count)
                {
                    throw new CopilotException("Copilot returned malformed or duplicate tool calls. No actions from that response were executed.");
                }

                hasToolCalls = true;
                turn.Add(new ApiMessage("assistant", message.Content, calls));
                var answered = new HashSet<string>(StringComparer.Ordinal);
                string? activeCall = null;
                try
                {
                    foreach (var call in calls)
                    {
                        token.ThrowIfCancellationRequested();
                        string output;
                        string? progress = null;
                        if (cachedCalls.TryGetValue(call.Id, out var cached))
                        {
                            output = cached.Call == call.Function ? cached.Result
                                : SerializeResult(ToolResults.Error("This tool-call ID was already used for a different action. No action was repeated."));
                        }
                        else
                        {
                            activeCall = call.Id;
                            var result = await InvokeToolAsync(call.Function, token).ConfigureAwait(false);
                            output = SerializeResult(result);
                            cachedCalls.Add(call.Id, (call.Function, output));
                            progress = $"Tool {call.Function.Name}: {(result.IsError == true ? "failed" : "completed")}.\nWaiting for Copilot's response...";
                        }

                        turn.Add(new ApiMessage("tool", output, ToolCallId: call.Id));
                        answered.Add(call.Id);
                        activeCall = null;
                        if (progress is not null)
                        {
                            responseChanged(progress);
                        }
                    }
                }
                finally
                {
                    foreach (var call in calls.Where(call => !answered.Contains(call.Id)))
                    {
                        var reason = call.Id == activeCall
                            ? "The tool did not return a result. Its action may have completed; inspect current state before retrying."
                            : "Not executed because this request ended before reaching this tool call.";
                        turn.Add(new ApiMessage("tool", SerializeResult(ToolResults.Error(reason)), ToolCallId: call.Id));
                    }
                }
            }

            throw new CopilotException("The tool-call limit was reached.");
        }
        finally
        {
            if (hasToolCalls && !committed)
            {
                // Keep tool outcomes even if the HTTP follow-up fails or Stop is clicked: side effects cannot be rolled back.
                turn.Add(new ApiMessage("assistant",
                    "This request ended before a final answer. Use the tool results above and inspect current state before repeating any actions."));
                _history.AddRange(turn);
            }
        }
    }

    private async Task<CallToolResult> InvokeToolAsync(FunctionCall call, CancellationToken token)
    {
        if (_moduleTools is null || !_moduleTools.Tools.Any(tool => tool.Name == call.Name))
        {
            return ToolResults.Error("The requested tool is not available. Use only advertised module tools.");
        }

        JsonDocument arguments;
        try
        {
            arguments = JsonDocument.Parse(call.Arguments);
        }
        catch (JsonException)
        {
            return ToolResults.Error("Tool arguments must be valid JSON.");
        }

        using (arguments)
        {
            return await _moduleTools.CallAsync(call.Name, arguments.RootElement, token).ConfigureAwait(false);
        }
    }

    private static string SerializeResult(CallToolResult result) => JsonSerializer.Serialize(new
    {
        isError = result.IsError == true,
        content = result.Content.OfType<TextContentBlock>().Select(content => content.Text).ToArray()
    });

    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken token)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_oauthToken is null)
            {
                throw new CopilotException("Sign in to GitHub in Options > Copilot first.");
            }

            if (_access is null || _access.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds())
            {
                _access = await _auth.ExchangeAsync(_oauthToken, token).ConfigureAwait(false);
            }

            using var request = requestFactory();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _access.Token);
            var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                _access = null;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                using (response)
                {
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        IsConnected = false;
                    }

                    GitHubDeviceAuthClient.EnsureSuccess(response);
                }
            }

            return response;
        }

        throw new CopilotException("Copilot authorization was rejected. Sign in again.");
    }

    private static HttpRequestMessage ApiRequest(HttpMethod method, string uri)
    {
        var request = GitHubDeviceAuthClient.Request(method, uri);
        request.Headers.Add("Editor-Version", "vscode/1.95.0");
        request.Headers.Add("Editor-Plugin-Version", "copilot/1.0.0");
        request.Headers.Add("Openai-Intent", "conversation-panel");
        request.Headers.Add("Copilot-Integration-Id", "vscode-chat");
        return request;
    }

    public Task NewChatAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _history.Clear();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        IsConnected = false;
        _oauthToken = null;
        _access = null;
        return Task.CompletedTask;
    }

    public async Task SignOutAsync()
    {
        _credentials.Clear();
        await DisconnectAsync().ConfigureAwait(false);
        _history.Clear();
        Models = [];
    }

    private async Task<T> WithErrorsAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new CopilotException("The Copilot request timed out. Try again.");
        }
        catch (HttpRequestException error)
        {
            throw new CopilotException("Could not reach GitHub Copilot. Check your network or proxy settings.", error);
        }
        catch (JsonException error)
        {
            throw new CopilotException("GitHub Copilot returned an unexpected response format.", error);
        }
        catch (CryptographicException error)
        {
            throw new CopilotException("Saved sign-in cannot be decrypted by this Windows user. Sign out and sign in again.", error);
        }
        catch (McpException error)
        {
            throw new CopilotException("The MCP tool call failed. An action may have completed; inspect the module state before retrying.", error);
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        IsConnected = false;
        _oauthToken = null;
        _access = null;
        _history.Clear();
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed record ApiMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string? Content,
        [property: JsonPropertyName("tool_calls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ToolCall>? ToolCalls = null,
        [property: JsonPropertyName("tool_call_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ToolCallId = null);
    private sealed record ToolCall(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] FunctionCall Function);
    private sealed record FunctionCall(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] string Arguments);
    private sealed record ModelList([property: JsonPropertyName("data")] ModelInfo[]? Data);
    private sealed record ModelInfo(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("capabilities")] ModelCapabilities? Capabilities);
    private sealed record ModelCapabilities([property: JsonPropertyName("type")] string? Type);
    private sealed record Completion([property: JsonPropertyName("choices")] Choice[]? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] ApiMessage? Message);
}
