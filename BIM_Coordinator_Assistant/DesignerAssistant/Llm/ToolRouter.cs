using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DesignerAssistant.Configuration;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public sealed record ToolRouteDecision(
    string Action,
    string? ToolName,
    string Reason,
    int PromptTokens,
    int CompletionTokens,
    int BilledTokens);

public sealed class ToolRouter
{
    private const string ChatUrl = "https://api.giga.chat/v1/chat/completions";
    private readonly HttpClient _httpClient;
    private readonly AppOptions _options;
    private readonly Func<CancellationToken, Task<string>> _getAccessToken;

    public ToolRouter(
        HttpClient httpClient,
        AppOptions options,
        Func<CancellationToken, Task<string>> getAccessToken)
    {
        _httpClient = httpClient;
        _options = options;
        _getAccessToken = getAccessToken;
    }

    public async Task<ToolRouteDecision> RouteAsync(
        IReadOnlyCollection<ChatMessage> messages,
        IReadOnlyCollection<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
    {
        var toolNames = tools.Select(tool => tool.Name).ToArray();
        var catalogue = string.Join(
            Environment.NewLine,
            tools.Select(tool => $"- {tool.Name}: {tool.Description}"));
        var dialogue = string.Join(
            Environment.NewLine,
            messages.TakeLast(8).Select(message => $"{message.Role}: {message.Content}"));
        var parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["action"] = new { type = "string", @enum = new[] { "answer", "call_tool" } },
                ["tool"] = new { type = "string", @enum = toolNames },
                ["reason"] = new { type = "string" }
            },
            ["required"] = new[] { "action", "reason" }
        };
        var requestMessages = new[]
        {
            new
            {
                role = "system",
                content = "Ты маршрутизатор инструментов. Выбери call_tool, если для ответа нужны актуальные данные или действие в Revit. Выбери answer для разговора, объяснения или данных, уже присутствующих в диалоге. Не отвечай на задачу пользователя."
            },
            new { role = "user", content = $"Диалог:\n{dialogue}\n\nДоступные инструменты:\n{catalogue}" }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
        {
            Content = JsonContent.Create(new
            {
                model = _options.Model,
                messages = requestMessages,
                functions = new[]
                {
                    new { name = "route_tool_request", description = "Вернуть решение о маршрутизации запроса", parameters }
                },
                function_call = new { name = "route_tool_request" },
                max_tokens = 300
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await _getAccessToken(cancellationToken));
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"ToolRouter: GigaChat вернул {(int)response.StatusCode}: {body}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var functionCall = root.GetProperty("choices")[0].GetProperty("message").GetProperty("function_call");
        var rawArguments = functionCall.GetProperty("arguments");
        using var argumentsDocument = rawArguments.ValueKind == JsonValueKind.String
            ? JsonDocument.Parse(rawArguments.GetString() ?? "{}")
            : JsonDocument.Parse(rawArguments.GetRawText());
        var arguments = argumentsDocument.RootElement;
        var action = arguments.GetProperty("action").GetString() ?? "answer";
        var toolName = arguments.TryGetProperty("tool", out var tool) ? tool.GetString() : null;
        var reason = arguments.TryGetProperty("reason", out var reasonElement) ? reasonElement.GetString() ?? "" : "";
        if (action == "call_tool" && !toolNames.Contains(toolName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"ToolRouter выбрал неизвестный инструмент '{toolName}'.");
        }

        var usage = root.GetProperty("usage");
        return new ToolRouteDecision(
            action,
            toolName,
            reason,
            ReadInt(usage, "prompt_tokens"),
            ReadInt(usage, "completion_tokens"),
            ReadInt(usage, "total_tokens"));
    }

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
}
