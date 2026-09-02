using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

const string RestrictedMode = "--restricted";
const string UnrestrictedMode = "--unrestricted";
const string Model = "GigaChat-2";
const string AuthUrl = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";
const string ChatUrl = "https://api.giga.chat/v1/chat/completions";

if (args.Length != 1 || (args[0] != RestrictedMode && args[0] != UnrestrictedMode))
{
    Console.WriteLine("Укажите режим запуска:");
    Console.WriteLine("  dotnet run -- --unrestricted");
    Console.WriteLine("  dotnet run -- --restricted");
    return;
}

var isRestricted = args[0] == RestrictedMode;
var authorizationKey = Environment.GetEnvironmentVariable("GIGACHAT_AUTH_KEY")
                       ?? ReadSecret("Ключ авторизации GigaChat: ");
var scope = Environment.GetEnvironmentVariable("GIGACHAT_SCOPE") ?? "GIGACHAT_API_PERS";

if (string.IsNullOrWhiteSpace(authorizationKey))
{
    Console.WriteLine("Ключ авторизации не введён.");
    return;
}

using var client = new HttpClient();
string? accessToken = null;
var tokenExpiresAt = DateTimeOffset.MinValue;

Console.WriteLine($"Модель: {Model} (GigaChat Lite)");
Console.WriteLine($"Режим: {(isRestricted ? "с ограничениями" : "без ограничений")}");
Console.WriteLine("Опишите блюдо и при необходимости укажите вес порции. Введите exit для выхода.");

while (true)
{
    Console.Write("\nБлюдо: ");
    var prompt = Console.ReadLine();

    if (string.Equals(prompt, "exit", StringComparison.OrdinalIgnoreCase)) break;
    if (string.IsNullOrWhiteSpace(prompt)) continue;

    try
    {
        if (accessToken is null || tokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            (accessToken, tokenExpiresAt) = await GetAccessTokenAsync(client, authorizationKey, scope);

        object requestBody = isRestricted
            ? CreateRestrictedRequest(prompt)
            : CreateUnrestrictedRequest(prompt);

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
        {
            Content = JsonContent.Create(requestBody)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Ошибка GigaChat API ({(int)response.StatusCode}): {json}");
            continue;
        }

        using var document = JsonDocument.Parse(json);
        var choice = document.RootElement.GetProperty("choices")[0];
        var text = choice.GetProperty("message").GetProperty("content").GetString();
        var finishReason = choice.GetProperty("finish_reason").GetString();

        Console.WriteLine($"Ответ:\n{text}");
        Console.WriteLine($"Причина завершения: {finishReason}");
    }
    catch (HttpRequestException exception)
    {
        Console.WriteLine($"Ошибка подключения: {exception.Message}");
        Console.WriteLine("Проверьте интернет-соединение и установку сертификатов НУЦ Минцифры.");
    }
    catch (JsonException exception)
    {
        Console.WriteLine($"Не удалось разобрать ответ API: {exception.Message}");
    }
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
    var accessToken = root.GetProperty("access_token").GetString()
                      ?? throw new JsonException("В ответе OAuth отсутствует access_token.");
    var expiresAtValue = root.GetProperty("expires_at").GetInt64();
    var expiresAt = expiresAtValue > 10_000_000_000
        ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAtValue)
        : DateTimeOffset.FromUnixTimeSeconds(expiresAtValue);

    return (accessToken, expiresAt);
}

static object CreateUnrestrictedRequest(string prompt) => new
{
    model = Model,
    messages = new[] { new { role = "user", content = prompt } }
};

static object CreateRestrictedRequest(string prompt) => new
{
    model = Model,
    messages = new[]
    {
        new
        {
            role = "system",
            content = "Рассчитай примерное КБЖУ блюда. Верни только JSON по заданной схеме. " +
                      "Все пищевые значения указывай в граммах. " +
                      "Если вес порции не указан, верни null для portionWeightG и portionKcal. " +
                      "Заверши ответ сразу после закрывающей фигурной скобки и не добавляй пояснений."
        },
        new { role = "user", content = prompt }
    },
    max_tokens = 160,
    response_format = new
    {
        type = "json_schema",
        schema = new
        {
            type = "object",
            properties = new
            {
                kcalPer100g = new { type = "number", description = "Ккал на 100 г блюда" },
                proteinG = new { type = "number", description = "Белки на 100 г" },
                fatG = new { type = "number", description = "Жиры на 100 г" },
                carbsG = new { type = "number", description = "Углеводы на 100 г" },
                portionWeightG = new { type = new[] { "number", "null" }, description = "Вес порции" },
                portionKcal = new { type = new[] { "number", "null" }, description = "Ккал на всю порцию" }
            },
            required = new[]
            {
                "kcalPer100g", "proteinG", "fatG", "carbsG", "portionWeightG", "portionKcal"
            },
            additionalProperties = false
        },
        strict = true
    }
};

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
