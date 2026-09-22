using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public CopilotHttpConnection(ICopilotCredentialStore credentials, HttpClient? http = null)
    {
        _credentials = credentials;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        _auth = new GitHubDeviceAuthClient(_http);
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

            var userMessage = new ApiMessage("user", prompt);
            var messages = new List<ApiMessage>
            {
                new("system", "You are a helpful assistant. This is a chat-only interface with no tools.")
            };
            messages.AddRange(_history);
            messages.Add(userMessage);
            using var response = await SendAuthorizedAsync(() =>
            {
                var request = ApiRequest(HttpMethod.Post, "https://api.githubcopilot.com/chat/completions");
                request.Content = JsonContent.Create(new { model = _model, messages, stream = false });
                return request;
            }, cancellationToken).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<Completion>(cancellationToken).ConfigureAwait(false);
            var content = result?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new CopilotException("Copilot returned no text. Try another message or model.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _history.Add(userMessage);
            _history.Add(new ApiMessage("assistant", content));
            responseChanged(content);
            return content;
        }, cancellationToken);

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
        [property: JsonPropertyName("content")] string Content);
    private sealed record ModelList([property: JsonPropertyName("data")] ModelInfo[]? Data);
    private sealed record ModelInfo(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("capabilities")] ModelCapabilities? Capabilities);
    private sealed record ModelCapabilities([property: JsonPropertyName("type")] string? Type);
    private sealed record Completion([property: JsonPropertyName("choices")] Choice[]? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] ApiMessage? Message);
}
