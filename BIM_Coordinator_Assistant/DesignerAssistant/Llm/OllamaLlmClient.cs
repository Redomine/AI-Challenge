using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public sealed class OllamaLlmClient : IToolCallingLlmClient, IStructuredLlmClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly int _maxOutputTokens;
    private readonly bool _thinkingEnabled;

    public OllamaLlmClient(HttpClient http, string model = "qwen3:4b", int maxOutputTokens = 1200,
        bool thinkingEnabled = true)
    {
        _http = http;
        _model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("Укажите модель Ollama.", nameof(model)) : model;
        _maxOutputTokens = maxOutputTokens;
        _thinkingEnabled = thinkingEnabled;
    }

    public Task<TokenCountResult> CountTextTokensAsync(
        IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default)
    {
        var counts = texts.Select(text => Math.Max(1, (text?.Length ?? 0) / 4)).ToArray();
        return Task.FromResult(new TokenCountResult(counts.Sum(), true));
    }

    public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        GenerateCoreAsync(BuildMessages(instructions, messages), null, null, cancellationToken);

    public Task<LlmResponse> GenerateStructuredAsync(string instructions, IReadOnlyCollection<ChatMessage> messages,
        JsonElement schema, CancellationToken cancellationToken = default) =>
        GenerateCoreAsync(BuildMessages(instructions, messages), schema, null, cancellationToken);

    public async Task<LlmResponse> GenerateWithToolsAsync(string instructions,
        IReadOnlyCollection<ChatMessage> messages, IToolProvider toolProvider, Action<string>? trace = null,
        CancellationToken cancellationToken = default)
    {
        var tools = await toolProvider.GetToolsAsync(cancellationToken);
        var policy = new ToolExecutionPolicy(toolProvider, tools);
        var conversation = BuildMessages(instructions, messages);
        var definitions = tools.Select(tool => new
        {
            type = "function",
            function = new
            {
                name = tool.Name,
                description = ToolCapabilityCatalog.Get(tool).Description,
                parameters = tool.Parameters
            }
        }).ToArray();
        var results = new List<ToolResultEnvelope>();
        var promptTokens = 0;
        var completionTokens = 0;
        var reasoning = new List<string>();
        var latest = messages.LastOrDefault(message => message.Role == "user")?.Content ?? "";
        var forcedTool = DetectRequiredReadTool(latest);
        if (forcedTool is not null)
        {
            if (!tools.Any(tool => tool.Name == forcedTool))
                return new LlmResponse($"Инструмент {forcedTool} отсутствует в текущем tools/list. Данные модели недоступны.",
                    "missing_tool", Usage(0, 0));
            using var empty = JsonDocument.Parse("{}");
            var result = await policy.ExecuteAsync(forcedTool, empty.RootElement, cancellationToken);
            results.Add(result);
            var resultText = result.Ok ? result.Result?.GetRawText() ?? "null" : result.Error?.Message ?? "Ошибка инструмента";
            trace?.Invoke($"Tool trace: {forcedTool} {{}}");
            trace?.Invoke($"Tool result: {forcedTool} {resultText}");
            if (!result.Ok || !result.Completed) return ToolResultResponse(results, 0, 0);
            conversation.Add(new Dictionary<string, object?>
            {
                ["role"] = "assistant", ["content"] = "",
                ["tool_calls"] = new[] { new { type = "function", function = new { name = forcedTool, arguments = new { } } } }
            });
            conversation.Add(new Dictionary<string, object?>
            {
                ["role"] = "tool", ["tool_name"] = forcedTool, ["content"] = resultText
            });
        }
        for (var step = 0; step < 12; step++)
        {
            using var response = await PostAsync(conversation, null, definitions, cancellationToken);
            promptTokens += GetInt(response.RootElement, "prompt_eval_count");
            completionTokens += GetInt(response.RootElement, "eval_count");
            var message = response.RootElement.GetProperty("message");
            var parts = ReadResponse(message);
            if (!string.IsNullOrWhiteSpace(parts.Reasoning)) reasoning.Add(parts.Reasoning);
            if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0)
            {
                if (results.Count + calls.GetArrayLength() > 8) throw new ToolCallLimitExceededException(8);
                conversation.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = parts.Content ?? "",
                    ["tool_calls"] = calls.Clone()
                });
                foreach (var call in calls.EnumerateArray())
                {
                    var function = call.GetProperty("function");
                    var name = function.GetProperty("name").GetString() ?? throw new JsonException("Ollama не указала имя инструмента.");
                    var rawArguments = function.GetProperty("arguments");
                    using var parsed = rawArguments.ValueKind == JsonValueKind.String
                        ? JsonDocument.Parse(rawArguments.GetString() ?? "{}")
                        : JsonDocument.Parse(rawArguments.GetRawText());
                    var arguments = parsed.RootElement.Clone();
                    trace?.Invoke($"Tool trace: {name} {arguments.GetRawText()}");
                    var result = await policy.ExecuteAsync(name, arguments, cancellationToken);
                    results.Add(result);
                    var resultText = result.Ok ? result.Result?.GetRawText() ?? "null" : result.Error?.Message ?? "Ошибка инструмента";
                    trace?.Invoke($"Tool result: {name} {resultText}");
                    if (!result.Ok || !result.Completed)
                        return ToolResultResponse(results, promptTokens, completionTokens) with
                        { Reasoning = JoinReasoning(reasoning) };
                    conversation.Add(new Dictionary<string, object?>
                    {
                        ["role"] = "tool", ["tool_name"] = name, ["content"] = resultText
                    });
                }
                continue;
            }
            var content = parts.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                if (results.Count > 0) return ToolResultResponse(results, promptTokens, completionTokens) with
                { Reasoning = JoinReasoning(reasoning) };
                throw new InvalidOperationException("Ollama не вернула ни текст, ни вызов инструмента.");
            }
            return new LlmResponse(content.Trim(), results.Count > 0 ? "tool_results_grounded" : "stop",
                Usage(promptTokens, completionTokens), results.ToArray(), JoinReasoning(reasoning));
        }
        throw new ToolCallLimitExceededException(8);
    }

    private async Task<LlmResponse> GenerateCoreAsync(List<Dictionary<string, object?>> conversation,
        JsonElement? schema, object? tools, CancellationToken cancellationToken)
    {
        using var response = await PostAsync(conversation, schema, tools, cancellationToken);
        var parts = ReadResponse(response.RootElement.GetProperty("message"));
        var content = parts.Content;
        if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("Ollama вернула пустой ответ.");
        return new LlmResponse(content.Trim(), GetString(response.RootElement, "done_reason") ?? "stop",
            Usage(GetInt(response.RootElement, "prompt_eval_count"), GetInt(response.RootElement, "eval_count")),
            Reasoning: parts.Reasoning);
    }

    private async Task<JsonDocument> PostAsync(List<Dictionary<string, object?>> messages, JsonElement? schema,
        object? tools, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("api/chat", new
        {
            model = _model,
            messages,
            stream = false,
            think = _thinkingEnabled && schema is null && tools is null,
            format = schema,
            tools,
            options = new { num_predict = _maxOutputTokens, num_ctx = 16384 }
        }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama HTTP {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static List<Dictionary<string, object?>> BuildMessages(string instructions,
        IReadOnlyCollection<ChatMessage> messages)
    {
        var result = new List<Dictionary<string, object?>> { new() { ["role"] = "system", ["content"] = instructions } };
        result.AddRange(messages.Select(message => new Dictionary<string, object?>
        {
            ["role"] = message.Role, ["content"] = message.Content
        }));
        return result;
    }

    private static LlmResponse ToolResultResponse(IReadOnlyList<ToolResultEnvelope> results, int prompt, int completion) =>
        new(string.Join(Environment.NewLine, results.Select(result => result.Ok
                ? $"Инструмент {result.Tool}: {result.Result?.GetRawText()}"
                : $"Инструмент {result.Tool} не выполнен: {result.Error?.Message}")),
            "tool_result", Usage(prompt, completion), results);

    private static TokenUsage Usage(int prompt, int completion) =>
        new(0, completion, prompt, prompt, 0, completion, 0, false);

    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (string? Content, string? Reasoning) ReadResponse(JsonElement message)
    {
        var content = GetString(message, "content");
        var reasoning = GetString(message, "thinking")?.Trim();
        if (content is null) return (null, reasoning);
        var end = content.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (end >= 0)
        {
            var embedded = content[..end].Trim();
            if (embedded.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
                embedded = embedded["<think>".Length..].Trim();
            return (content[(end + "</think>".Length)..].Trim(),
                !string.IsNullOrWhiteSpace(reasoning) ? reasoning : embedded);
        }
        return content.TrimStart().StartsWith("<think>", StringComparison.OrdinalIgnoreCase)
            ? ("", reasoning)
            : (content.Trim(), reasoning);
    }

    private static string? JoinReasoning(IEnumerable<string> steps)
    {
        var result = string.Join(Environment.NewLine + Environment.NewLine, steps);
        return result.Length == 0 ? null : result;
    }

    private static string? DetectRequiredReadTool(string message)
    {
        if (Regex.IsMatch(message, @"\b(выбранн\w*|выделенн\w*|отмеченн\w*)\s+(элемент\w*|объект\w*)\b", RegexOptions.IgnoreCase))
            return "revit_get_selected_elements";
        if (Regex.IsMatch(message, @"\b(элемент\w*|объект\w*|состав)\b.{0,30}\b(на|активн\w*|текущ\w*)\s+вид\w*\b", RegexOptions.IgnoreCase))
            return "revit_custom_summarize_elements";
        return null;
    }
}
