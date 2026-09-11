using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DesignerAssistant.Configuration;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public sealed class GigaChatClient : ILlmClient
{
    private const string AuthUrl = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";
    private const string ChatUrl = "https://api.giga.chat/v1/chat/completions";

    private readonly HttpClient _httpClient;
    private readonly AppOptions _options;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public GigaChatClient(HttpClient httpClient, AppOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<string> GenerateAsync(
        string instructions,
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (_accessToken is null ||
            _tokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            (_accessToken, _tokenExpiresAt) = await GetAccessTokenAsync(cancellationToken);
        }

        var requestMessages = new[] { new ChatMessage("system", instructions) }
            .Concat(messages)
            .Select(message => new
            {
                role = message.Role,
                content = message.Content
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
        {
            Content = JsonContent.Create(new
            {
                model = _options.Model,
                messages = requestMessages,
                max_tokens = _options.MaxOutputTokens
            })
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GigaChat API вернуло {(int)response.StatusCode} {response.ReasonPhrase}: " +
                TryGetErrorMessage(responseBody));
        }

        using var document = JsonDocument.Parse(responseBody);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return string.IsNullOrWhiteSpace(content)
            ? throw new InvalidOperationException("В ответе GigaChat не найден текст модели.")
            : content.Trim();
    }

    private async Task<(string AccessToken, DateTimeOffset ExpiresAt)> GetAccessTokenAsync(
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["scope"] = _options.Scope
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("RqUID", Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            _options.AuthorizationKey.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                ? _options.AuthorizationKey[6..].Trim()
                : _options.AuthorizationKey.Trim());

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OAuth GigaChat вернул {(int)response.StatusCode} {response.ReasonPhrase}: " +
                TryGetErrorMessage(responseBody));
        }

        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new JsonException("В ответе OAuth отсутствует access_token.");
        var expiresAtValue = root.GetProperty("expires_at").GetInt64();
        var expiresAt = expiresAtValue > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAtValue)
            : DateTimeOffset.FromUnixTimeSeconds(expiresAtValue);

        return (accessToken, expiresAt);
    }

    private static string TryGetErrorMessage(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;

            if (root.TryGetProperty("message", out var message))
            {
                return message.GetString() ?? responseBody;
            }

            if (root.TryGetProperty("error", out var error))
            {
                return error.ValueKind == JsonValueKind.String
                    ? error.GetString() ?? responseBody
                    : error.ToString();
            }
        }
        catch (JsonException)
        {
        }

        return responseBody;
    }
}
