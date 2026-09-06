using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

const string Model = "GigaChat-2";
const string AuthUrl = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";
const string ChatUrl = "https://api.giga.chat/v1/chat/completions";

var temperatures = new[] { 0.0, 0.7, 1.2 };
var authorizationKey = Environment.GetEnvironmentVariable("GIGACHAT_AUTH_KEY")
                       ?? ReadSecret("Ключ авторизации GigaChat: ");
var scope = Environment.GetEnvironmentVariable("GIGACHAT_SCOPE") ?? "GIGACHAT_API_PERS";

if (string.IsNullOrWhiteSpace(authorizationKey))
{
    Console.WriteLine("Ключ авторизации не введён.");
    return;
}

Console.WriteLine($"Модель: {Model}");
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

try
{
    for (var index = 0; index < temperatures.Length; index++)
    {
        var temperature = temperatures[index];
        var answer = await SendPromptAsync(prompt, temperature);

        Console.WriteLine($"\n===== Ответ {index + 1}: temperature = {temperature.ToString("0.0", CultureInfo.InvariantCulture)} =====");
        Console.WriteLine(answer);
    }
}
catch (HttpRequestException exception)
{
    Console.WriteLine($"Ошибка подключения: {exception.Message}");
    Console.WriteLine("Последовательность остановлена. Проверьте подключение и доступ к GigaChat API.");
}
catch (JsonException exception)
{
    Console.WriteLine($"Не удалось разобрать ответ API: {exception.Message}");
    Console.WriteLine("Последовательность остановлена.");
}

async Task<string> SendPromptAsync(string userPrompt, double temperature)
{
    if (accessToken is null || tokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        (accessToken, tokenExpiresAt) = await GetAccessTokenAsync(client, authorizationKey, scope);

    using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
    {
        Content = JsonContent.Create(new
        {
            model = Model,
            messages = new[] { new { role = "user", content = userPrompt } },
            temperature
        })
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    using var response = await client.SendAsync(request);
    var json = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"GigaChat API ({(int)response.StatusCode}): {json}");

    using var document = JsonDocument.Parse(json);
    return document.RootElement
               .GetProperty("choices")[0]
               .GetProperty("message")
               .GetProperty("content")
               .GetString()
           ?? throw new JsonException("В ответе модели отсутствует текст.");
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
