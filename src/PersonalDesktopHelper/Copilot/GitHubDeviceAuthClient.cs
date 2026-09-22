using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PersonalDesktopHelper.Copilot;

internal sealed class GitHubDeviceAuthClient(HttpClient http)
{
    // The same public Copilot OAuth client used by the CopilotEverywhere integration.
    private const string ClientId = "01ab8ac9400c4e429b23";

    public async Task<string> SignInAsync(Action<DeviceAuthorization> challenge, CancellationToken token)
    {
        using var deviceRequest = Request(HttpMethod.Post, "https://github.com/login/device/code");
        deviceRequest.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["scope"] = "read:user"
        });
        using var deviceResponse = await http.SendAsync(deviceRequest, token).ConfigureAwait(false);
        EnsureSuccess(deviceResponse);
        var device = await deviceResponse.Content.ReadFromJsonAsync<DeviceCode>(token).ConfigureAwait(false)
            ?? throw new CopilotException("GitHub returned an empty sign-in response.");
        if (string.IsNullOrWhiteSpace(device.Code) || string.IsNullOrWhiteSpace(device.UserCode) ||
            device.ExpiresIn <= 0 || device.Interval < 0 ||
            !Uri.TryCreate(device.VerificationUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/login/device")
        {
            throw new CopilotException("GitHub returned an invalid sign-in response.");
        }

        challenge(new DeviceAuthorization(device.UserCode, uri));
        using var expiration = CancellationTokenSource.CreateLinkedTokenSource(token);
        expiration.CancelAfter(TimeSpan.FromSeconds(device.ExpiresIn));
        var interval = Math.Max(1, device.Interval);
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), expiration.Token).ConfigureAwait(false);
                using var request = Request(HttpMethod.Post, "https://github.com/login/oauth/access_token");
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = ClientId,
                    ["device_code"] = device.Code,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                });
                using var response = await http.SendAsync(request, expiration.Token).ConfigureAwait(false);
                EnsureSuccess(response);
                var result = await response.Content.ReadFromJsonAsync<OAuthResult>(expiration.Token).ConfigureAwait(false)
                    ?? throw new CopilotException("GitHub returned an empty authorization response.");
                if (!string.IsNullOrWhiteSpace(result.AccessToken))
                {
                    return result.AccessToken;
                }

                switch (result.Error)
                {
                    case "authorization_pending":
                        break;
                    case "slow_down":
                        interval = checked(interval + 5);
                        break;
                    case "access_denied":
                        throw new CopilotException("GitHub sign-in was denied.");
                    case "expired_token":
                        throw new CopilotException("The sign-in code expired. Start sign-in again.");
                    default:
                        throw new CopilotException("GitHub could not authorize this sign-in. Start sign-in again.");
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && expiration.IsCancellationRequested)
        {
            throw new CopilotException("The sign-in code expired. Start sign-in again.");
        }
    }

    public async Task<CopilotAccessToken> ExchangeAsync(string oauthToken, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(oauthToken) || oauthToken.Any(char.IsControl))
        {
            throw new CopilotException("Saved GitHub sign-in is invalid. Sign out and sign in again.");
        }

        using var request = Request(HttpMethod.Get, "https://api.github.com/copilot_internal/v2/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("token", oauthToken);
        using var response = await http.SendAsync(request, token).ConfigureAwait(false);
        EnsureSuccess(response);
        var result = await response.Content.ReadFromJsonAsync<CopilotAccessToken>(token).ConfigureAwait(false)
            ?? throw new CopilotException("GitHub returned no Copilot access token.");
        if (string.IsNullOrWhiteSpace(result.Token) || result.Token.Any(char.IsControl) ||
            result.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            throw new CopilotException("GitHub returned an invalid or expired Copilot access token.");
        }

        return result;
    }

    internal static HttpRequestMessage Request(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd("PersonalDesktopHelper/1.0");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    internal static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new CopilotException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "GitHub authorization expired or was rejected. Sign in again.",
                System.Net.HttpStatusCode.Forbidden => "Copilot access was denied. Check your subscription and organization policies.",
                System.Net.HttpStatusCode.TooManyRequests => "GitHub Copilot is rate limiting requests. Wait before trying again.",
                _ => $"GitHub Copilot returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
            });
        }
    }

    private sealed record DeviceCode(
        [property: JsonPropertyName("device_code")] string Code,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

    private sealed record OAuthResult(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("error")] string? Error);
}

internal sealed record CopilotAccessToken(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expires_at")] long ExpiresAt);
