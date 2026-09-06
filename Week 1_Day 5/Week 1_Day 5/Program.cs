using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

const string AuthUrl = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";
const string ChatUrl = "https://api.giga.chat/v1/chat/completions";

var models = new[]
{
    new ModelInfo("Lite", "GigaChat-2", 0.065m),
    new ModelInfo("Pro", "GigaChat-2-Pro", 0.5m),
    new ModelInfo("Max", "GigaChat-2-Max", 0.65m),
    new ModelInfo("Ultra", "GigaChat-3-Ultra", null)
};

var authorizationKey = Environment.GetEnvironmentVariable("GIGACHAT_AUTH_KEY")
                       ?? ReadSecret("Ключ авторизации GigaChat: ");
var scope = Environment.GetEnvironmentVariable("GIGACHAT_SCOPE") ?? "GIGACHAT_API_PERS";

if (string.IsNullOrWhiteSpace(authorizationKey))
{
    Console.WriteLine("Ключ авторизации не введён.");
    return;
}

Console.Write("Введите запрос: ");
var prompt = Console.ReadLine();

if (string.IsNullOrWhiteSpace(prompt))
{
    Console.WriteLine("Запрос не введён.");
    return;
}

using var client = new HttpClient();
string? accessToken = null;
var tokenExpiresAt = DateTimeOffset.MinValue;

foreach (var model in models)
{
    Console.WriteLine($"\n===== {model.DisplayName}: {model.ApiName} =====");

    try
    {
        var result = await SendPromptAsync(prompt, model.ApiName);
        Console.WriteLine(result.Content);
        Console.WriteLine("\n----- Метрики -----");
        Console.WriteLine($"Время ответа: {result.Elapsed.TotalSeconds:F2} с");
        Console.WriteLine($"Токены запроса: {result.PromptTokens}");
        Console.WriteLine($"Токены ответа: {result.CompletionTokens}");
        Console.WriteLine($"Всего токенов: {result.TotalTokens}");
        PrintCost(model, result.TotalTokens);
    }
    catch (HttpRequestException exception)
    {
        Console.WriteLine($"Модель недоступна: {exception.Message}");
    }
    catch (JsonException exception)
    {
        Console.WriteLine($"Не удалось разобрать ответ модели: {exception.Message}");
    }
}

async Task<ChatResult> SendPromptAsync(string userPrompt, string modelName)
{
    if (accessToken is null || tokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        (accessToken, tokenExpiresAt) = await GetAccessTokenAsync(client, authorizationKey, scope);

    using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
    {
        Content = JsonContent.Create(new
        {
            model = modelName,
            messages = new[] { new { role = "user", content = userPrompt } }
        })
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    var stopwatch = Stopwatch.StartNew();
    using var response = await client.SendAsync(request);
    var json = await response.Content.ReadAsStringAsync();
    stopwatch.Stop();

    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"GigaChat API ({(int)response.StatusCode}): {json}");

    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    var content = root.GetProperty("choices")[0]
                      .GetProperty("message")
                      .GetProperty("content")
                      .GetString()
                  ?? throw new JsonException("В ответе модели отсутствует текст.");
    var usage = root.GetProperty("usage");

    return new ChatResult(
        content,
        stopwatch.Elapsed,
        usage.GetProperty("prompt_tokens").GetInt32(),
        usage.GetProperty("completion_tokens").GetInt32(),
        usage.GetProperty("total_tokens").GetInt32());
}

static void PrintCost(ModelInfo model, int totalTokens)
{
    if (model.RublesPerThousandTokens is null)
    {
        Console.WriteLine("Стоимость: 0 ₽ в доступном для Ultra freemium-режиме");
        return;
    }

    var estimatedCost = totalTokens / 1000m * model.RublesPerThousandTokens.Value;
    Console.WriteLine(
        $"Оценочная стоимость: {estimatedCost.ToString("0.0000", CultureInfo.InvariantCulture)} ₽ " +
        $"({model.RublesPerThousandTokens.Value.ToString("0.###", CultureInfo.InvariantCulture)} ₽ за 1000 токенов)");
}

static async Task<(string AccessToken, DateTimeOffset ExpiresAt)> GetAccessTokenAsync(
    HttpClient client,
    string authorizationKey,
    string scope)
{
    using var request = new HttpRequestMessage(HttpMethod.Post, AuthUrl)
    {
        Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["scope"] = scope })
    };
    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    request.Headers.Add("RqUID", Guid.NewGuid().ToString());
    request.Headers.Authorization = new AuthenticationHeaderValue(
        "Basic",
        authorizationKey.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
            ? authorizationKey[6..].Trim()
            : authorizationKey.Trim());

    using var response = await client.SendAsync(request);
    var json = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"OAuth GigaChat ({(int)response.StatusCode}): {json}");

    using var document = JsonDocument.Parse(json);
    var root = document.RootElement;
    var token = root.GetProperty("access_token").GetString()
                ?? throw new JsonException("В ответе OAuth отсутствует access_token.");
    var expiresAtValue = root.GetProperty("expires_at").GetInt64();
    var expiresAt = expiresAtValue > 10_000_000_000
        ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAtValue)
        : DateTimeOffset.FromUnixTimeSeconds(expiresAtValue);

    return (token, expiresAt);
}

static string ReadSecret(string prompt)
{
    Console.Write(prompt);
    var secret = new System.Text.StringBuilder();

    while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && secret.Length > 0)
            secret.Length--;
        else if (!char.IsControl(key.KeyChar))
            secret.Append(key.KeyChar);
    }

    Console.WriteLine();
    return secret.ToString();
}

internal sealed record ModelInfo(string DisplayName, string ApiName, decimal? RublesPerThousandTokens);

internal sealed record ChatResult(
    string Content,
    TimeSpan Elapsed,
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);
