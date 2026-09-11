using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DesignerAssistant.Configuration;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public sealed class GigaChatClient : ILlmClient
{
    private const string AuthUrl = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";
    private const string ChatUrl = "https://api.giga.chat/v1/chat/completions";
    private const string TokenCountUrl = "https://api.giga.chat/v1/tokens/count";

    private readonly HttpClient _httpClient;
    private readonly AppOptions _options;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private bool _tokenCountApiUnavailable;

    public GigaChatClient(HttpClient httpClient, AppOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<LlmResponse> GenerateAsync(
        string instructions,
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessTokenAsync(cancellationToken);

        var textsToCount = new[] { instructions }
            .Concat(messages.Select(message => message.Content))
            .ToArray();
        var tokenCountResult = await CountTokensAsync(textsToCount, cancellationToken);
        var tokenCounts = tokenCountResult.Counts;
        var currentRequestTokens = tokenCounts[^1];
        var historyBeforeResponseTokens = tokenCounts.Skip(1).Sum();
        var contextTokens = tokenCounts.Sum();

        if (contextTokens + _options.MaxOutputTokens > _options.ContextLimitTokens)
        {
            throw new ContextWindowExceededException(
                contextTokens,
                _options.MaxOutputTokens,
                _options.ContextLimitTokens,
                tokenCountResult.IsEstimated);
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
        var root = document.RootElement;
        var choice = root.GetProperty("choices")[0];
        var content = choice
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement)
            ? finishReasonElement.GetString() ?? "не указана"
            : "не указана";

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("В ответе GigaChat не найден текст модели.");
        }

        var usage = root.GetProperty("usage");
        var promptTokens = GetInt32(usage, "prompt_tokens");
        var completionTokens = GetInt32(usage, "completion_tokens");
        var cachedPromptTokens = GetInt32(usage, "precached_prompt_tokens");
        var billedTokens = GetInt32(usage, "total_tokens");

        return new LlmResponse(
            content.Trim(),
            finishReason,
            new TokenUsage(
                currentRequestTokens,
                historyBeforeResponseTokens + completionTokens,
                contextTokens,
                promptTokens,
                cachedPromptTokens,
                completionTokens,
                billedTokens,
                tokenCountResult.IsEstimated));
    }

    private async Task<TokenCountResult> CountTokensAsync(
        IReadOnlyCollection<string> input,
        CancellationToken cancellationToken)
    {
        if (_tokenCountApiUnavailable)
        {
            return EstimateTokenCounts(input);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenCountUrl)
        {
            Content = JsonContent.Create(new
            {
                model = _options.TokenizerModel,
                input
            })
        };
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", _accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _tokenCountApiUnavailable = true;
            return EstimateTokenCounts(input);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"GigaChat tokens/count вернул {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}: {TryGetErrorMessage(responseBody)}");
        }

        using var document = JsonDocument.Parse(responseBody);
        var counts = document.RootElement
            .EnumerateArray()
            .Select(item => item.GetProperty("tokens").GetInt32())
            .ToArray();

        if (counts.Length != input.Count)
        {
            throw new JsonException("GigaChat вернул неполный результат подсчёта токенов.");
        }

        return new TokenCountResult(counts, false);
    }

    private static TokenCountResult EstimateTokenCounts(IEnumerable<string> input)
    {
        var counts = input
            .Select(text => (int)Math.Ceiling(text.EnumerateRunes().Count() / 3.5d))
            .ToArray();
        return new TokenCountResult(counts, true);
    }

    private async Task EnsureAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is null ||
            _tokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            (_accessToken, _tokenExpiresAt) = await GetAccessTokenAsync(cancellationToken);
        }
    }

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : 0;

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

    private sealed record TokenCountResult(int[] Counts, bool IsEstimated);
}
