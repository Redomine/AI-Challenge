using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using Week_1_Day_3;

const string Model = "GigaChat-2";
const string AuthUrl = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";
const string ChatUrl = "https://api.giga.chat/v1/chat/completions";

var authorizationKey = Environment.GetEnvironmentVariable("GIGACHAT_AUTH_KEY")
                       ?? ReadSecret("Ключ авторизации GigaChat: ");
var scope = Environment.GetEnvironmentVariable("GIGACHAT_SCOPE") ?? "GIGACHAT_API_PERS";

if (string.IsNullOrWhiteSpace(authorizationKey))
{
    Console.WriteLine("Ключ авторизации не введён.");
    return;
}

Console.WriteLine($"Модель: {Model}");
Console.WriteLine("Введите данные человека одной строкой.");
Console.WriteLine("Нужны: вес, рост, возраст, пол и уровень физической активности.");
Console.Write("Данные: ");
var personData = Console.ReadLine();

if (string.IsNullOrWhiteSpace(personData))
{
    Console.WriteLine("Данные человека не введены.");
    return;
}

using var client = new HttpClient();
string? accessToken = null;
var tokenExpiresAt = DateTimeOffset.MinValue;

try
{
    var directAnswer = await SendPromptAsync(Prompts.Direct(personData));
    PrintAnswer(1, "Прямой ответ", directAnswer);

    var stepByStepAnswer = await SendPromptAsync(Prompts.StepByStep(personData));
    PrintAnswer(2, "Пошаговое решение", stepByStepAnswer);

    var generatedPrompt = await SendPromptAsync(Prompts.CreateSolutionPrompt(personData));
    PrintAnswer(3, "Промпт, составленный моделью", generatedPrompt);

    var promptForSolution = ExtractGeneratedPrompt(generatedPrompt.Content);
    var generatedPromptAnswer = await SendPromptAsync(Prompts.UseGeneratedPrompt(promptForSolution));
    PrintAnswer(4, "Решение по промпту модели", generatedPromptAnswer);

    var expertAnswer = await SendPromptAsync(Prompts.ExpertGroup(personData));
    PrintAnswer(5, "Группа экспертов", expertAnswer);

    PrintComparison(
        ("Прямой ответ", directAnswer),
        ("Пошаговое решение", stepByStepAnswer),
        ("Создание промпта", generatedPrompt),
        ("Решение по промпту модели", generatedPromptAnswer),
        ("Группа экспертов", expertAnswer));
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

async Task<ChatAnswer> SendPromptAsync(string prompt)
{
    if (accessToken is null || tokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
        (accessToken, tokenExpiresAt) = await GetAccessTokenAsync(client, authorizationKey, scope);

    using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
    {
        Content = JsonContent.Create(new
        {
            model = Model,
            messages = new[] { new { role = "user", content = prompt } }
        })
    };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    using var response = await client.SendAsync(request);
    var json = await response.Content.ReadAsStringAsync();

    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"GigaChat API ({(int)response.StatusCode}): {json}");

    using var document = JsonDocument.Parse(json);
    var choice = document.RootElement.GetProperty("choices")[0];
    var content = choice.GetProperty("message").GetProperty("content").GetString()
                  ?? throw new JsonException("В ответе модели отсутствует текст.");
    var finishReason = choice.GetProperty("finish_reason").GetString() ?? "не указана";

    return new ChatAnswer(content, finishReason);
}

static void PrintAnswer(int number, string title, ChatAnswer answer)
{
    Console.WriteLine($"\n===== Запрос {number}: {title} =====");
    Console.WriteLine(answer.Content);
    Console.WriteLine($"Причина завершения: {answer.FinishReason}");
}

static string ExtractGeneratedPrompt(string content)
{
    const string startMarker = "PROMPT_START";
    const string endMarker = "PROMPT_END";
    var start = content.IndexOf(startMarker, StringComparison.Ordinal);
    var end = content.IndexOf(endMarker, StringComparison.Ordinal);

    if (start < 0 || end <= start)
        throw new JsonException("Модель не выделила созданный промпт маркерами PROMPT_START/PROMPT_END.");

    start += startMarker.Length;
    return content[start..end].Trim();
}

static void PrintComparison(params (string Title, ChatAnswer Answer)[] results)
{
    Console.WriteLine("\n===== Сравнение итоговых значений =====");

    for (var index = 0; index < results.Length; index++)
    {
        var value = ExtractTdee(results[index].Answer.Content);
        var displayValue = value is null
            ? "не распознано"
            : $"{value.Value.ToString("0.#", CultureInfo.InvariantCulture)} ккал/сутки";
        Console.WriteLine($"{index + 1}. {results[index].Title}: {displayValue}");
    }
}

static double? ExtractTdee(string content)
{
    var matches = Regex.Matches(
        content,
        @"TDEE_RESULT:\s*(\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    if (matches.Count == 0)
        return null;

    var value = matches[^1].Groups[1].Value.Replace(',', '.');
    return double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var result)
        ? result
        : null;
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

internal sealed record ChatAnswer(string Content, string FinishReason);
