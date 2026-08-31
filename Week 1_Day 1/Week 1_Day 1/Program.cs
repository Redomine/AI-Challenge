using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var apiKey = Environment.GetEnvironmentVariable("MISTRAL_API_KEY") ?? ReadSecret("Mistral API-ключ: ");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("API-ключ не введён.");
    return;
}

using var client = new HttpClient();
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

Console.WriteLine("Введите exit, чтобы завершить работу.");

while (true)
{
    Console.Write("\nВаш вопрос: ");
    var prompt = Console.ReadLine();

    if (string.Equals(prompt, "exit", StringComparison.OrdinalIgnoreCase)) break;
    if (string.IsNullOrWhiteSpace(prompt)) continue;

    var response = await client.PostAsJsonAsync(
        "https://api.mistral.ai/v1/chat/completions",
        new
        {
            model = "mistral-small-latest",
            messages = new[] { new { role = "user", content = prompt } }
        });

    var json = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"Ошибка API ({(int)response.StatusCode}): {json}");
        continue;
    }

    using var document = JsonDocument.Parse(json);
    var text = document.RootElement
        .GetProperty("choices")[0]
        .GetProperty("message")
        .GetProperty("content")
        .GetString();

    Console.WriteLine($"Ответ: {text}");
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
