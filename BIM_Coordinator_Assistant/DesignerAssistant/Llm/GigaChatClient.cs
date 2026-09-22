using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesignerAssistant.Configuration;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public sealed class GigaChatClient : IToolCallingLlmClient, IStructuredLlmClient
{
    private const string AuthUrl = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";
    private const string ChatUrl = "https://api.giga.chat/v1/chat/completions";
    private const string TokenCountUrl = "https://api.giga.chat/v1/tokens/count";

    private readonly HttpClient _httpClient;
    private readonly AppOptions _options;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private bool _tokenCountApiUnavailable;
    private readonly ToolRouter _toolRouter;

    public GigaChatClient(HttpClient httpClient, AppOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _toolRouter = new ToolRouter(_httpClient, _options, GetAccessTokenForRouterAsync);
    }

    public async Task<LlmResponse> GenerateAsync(
        string instructions,
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        await GenerateAsync(instructions, messages, null, cancellationToken);

    public async Task<LlmResponse> GenerateStructuredAsync(
        string instructions,
        IReadOnlyCollection<ChatMessage> messages,
        JsonElement schema,
        CancellationToken cancellationToken = default)
    {
        InvalidOperationException? lastEmptyResponse = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await GenerateAsync(instructions, messages, schema, cancellationToken);
            }
            catch (InvalidOperationException exception) when (
                exception.Message.StartsWith("В ответе GigaChat не найден текст модели.", StringComparison.Ordinal))
            {
                lastEmptyResponse = exception;
            }
        }
        throw new InvalidOperationException(
            $"GigaChat трижды вернул пустой structured-ответ. {lastEmptyResponse?.Message}",
            lastEmptyResponse);
    }

    private async Task<LlmResponse> GenerateAsync(
        string instructions,
        IReadOnlyCollection<ChatMessage> messages,
        JsonElement? responseSchema,
        CancellationToken cancellationToken)
    {
        await EnsureAccessTokenAsync(cancellationToken);

        var textsToCount = new[] { instructions }
            .Concat(messages.Select(message => message.Content))
            .ToArray();
        var tokenCountResult = await CountTokensByTextAsync(textsToCount, cancellationToken);
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

        object requestBody = responseSchema is null
            ? new
            {
                model = _options.Model,
                messages = requestMessages,
                max_tokens = _options.MaxOutputTokens
            }
            : new
            {
                model = _options.Model,
                messages = requestMessages,
                max_tokens = _options.MaxOutputTokens,
                response_format = new { type = "json_schema", schema = responseSchema.Value, strict = true }
            };
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
        {
            Content = JsonContent.Create(requestBody)
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
        var message = choice.GetProperty("message");
        var content = message.GetProperty("content").GetString();
        var finishReason = choice.TryGetProperty("finish_reason", out var finishReasonElement)
            ? finishReasonElement.GetString() ?? "не указана"
            : "не указана";

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException(
                $"В ответе GigaChat не найден текст модели. finish_reason={finishReason}; message={message.GetRawText()}");
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

    public async Task<LlmResponse> GenerateWithToolsAsync(
        string instructions,
        IReadOnlyCollection<ChatMessage> messages,
        IToolProvider toolProvider,
        Action<string>? trace = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureAccessTokenAsync(cancellationToken);
        var tools = await toolProvider.GetToolsAsync(cancellationToken);
        trace?.Invoke($"Tool catalogue: {string.Join(", ", tools.Select(tool => tool.Name))}");
        var conversation = new List<Dictionary<string, object?>>
        {
            new() { ["role"] = "system", ["content"] = instructions }
        };
        conversation.AddRange(messages.Select(message => new Dictionary<string, object?>
        {
            ["role"] = message.Role, ["content"] = message.Content
        }));

        var route = await _toolRouter.RouteAsync(
            messages,
            tools,
            ExtractActiveProfile(instructions),
            cancellationToken);
        trace?.Invoke($"Tool route: {route.Action}; tool={route.ToolName ?? "none"}; reason={route.Reason}");
        if (!string.IsNullOrWhiteSpace(route.DirectResponse))
        {
            return new LlmResponse(
                route.DirectResponse,
                $"tool_router_{route.Action}",
                new TokenUsage(0, route.CompletionTokens, route.PromptTokens, route.PromptTokens, 0, route.CompletionTokens, route.BilledTokens, false));
        }
        var totalPrompt = route.PromptTokens;
        var totalCompletion = route.CompletionTokens;
        var totalBilled = route.BilledTokens;
        var forcedTool = route.Action == "call_tool" ? route.ToolName : null;
        var toolWasInvoked = false;
        var toolCallCount = 0;
        var finalizationRetries = 0;
        var initialResponseRetries = 0;
        var forceTextResponse = false;
        string? lastToolName = null;
        var executedToolResults = new List<(string Name, string Result)>();
        for (var step = 0; step < 7; step++)
        {
            object functionChoice = forceTextResponse
                ? "none"
                : forcedTool is not null
                ? new Dictionary<string, string> { ["name"] = forcedTool }
                : route.Action == "answer" ? "none" : "auto";
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
            {
                Content = JsonContent.Create(new
                {
                    model = _options.Model,
                    messages = conversation,
                    functions = tools.Select(tool => new { name = tool.Name, description = tool.Description, parameters = NormalizeToolSchema(tool.Parameters) }),
                    function_call = functionChoice,
                    max_tokens = _options.MaxOutputTokens
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"GigaChat API вернуло {(int)response.StatusCode} {response.ReasonPhrase}: {TryGetErrorMessage(body)}");
            }

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var choice = root.GetProperty("choices")[0];
            var message = choice.GetProperty("message");
            var usage = root.GetProperty("usage");
            totalPrompt += GetInt32(usage, "prompt_tokens");
            totalCompletion += GetInt32(usage, "completion_tokens");
            totalBilled += GetInt32(usage, "total_tokens");

            if (message.TryGetProperty("function_call", out var functionCall) && functionCall.ValueKind == JsonValueKind.Object)
            {
                if (forceTextResponse)
                    throw new InvalidOperationException(
                        $"GigaChat вернул вызов инструмента при принудительной финализации. message={message.GetRawText()}");
                if (toolCallCount >= 4) throw new ToolCallLimitExceededException(4);
                var name = functionCall.GetProperty("name").GetString() ?? throw new JsonException("GigaChat не указал имя функции.");
                var argumentsElement = functionCall.GetProperty("arguments");
                using var argumentsDocument = argumentsElement.ValueKind == JsonValueKind.String
                    ? JsonDocument.Parse(argumentsElement.GetString() ?? "{}")
                    : JsonDocument.Parse(argumentsElement.GetRawText());
                var arguments = argumentsDocument.RootElement.Clone();
                trace?.Invoke($"Tool trace: {name} {arguments.GetRawText()}");
                var toolResult = EnsureJsonToolResult(
                    await toolProvider.InvokeAsync(name, arguments, cancellationToken));
                trace?.Invoke($"Tool result: {name} {FormatTraceResult(toolResult)}");
                executedToolResults.Add((name, toolResult));
                forcedTool = null;
                toolWasInvoked = true;
                toolCallCount++;
                lastToolName = name;
                conversation.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant", ["content"] = message.TryGetProperty("content", out var interim) ? interim.GetString() ?? "" : "",
                    ["function_call"] = functionCall.Clone()
                });
                conversation.Add(new Dictionary<string, object?>
                {
                    ["role"] = "function", ["name"] = name, ["content"] = toolResult
                });
                continue;
            }

            var content = message.TryGetProperty("content", out var contentElement) ? contentElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(content))
            {
                var emptyFinishReason = choice.TryGetProperty("finish_reason", out var emptyFinish)
                    ? emptyFinish.GetString() ?? "не указана"
                    : "не указана";
                if (toolWasInvoked && finalizationRetries < 2)
                {
                    finalizationRetries++;
                    forceTextResponse = true;
                    trace?.Invoke(
                        $"Empty response after tool '{lastToolName}'. Finalization retry {finalizationRetries}/2; " +
                        $"finish_reason={emptyFinishReason}; message={message.GetRawText()}");
                    continue;
                }
                if (!toolWasInvoked && initialResponseRetries < 2)
                {
                    initialResponseRetries++;
                    trace?.Invoke(
                        $"Empty response before first tool. Initial retry {initialResponseRetries}/2; " +
                        $"finish_reason={emptyFinishReason}; message={message.GetRawText()}");
                    continue;
                }
                if (!toolWasInvoked && forcedTool is not null)
                {
                    var routedTool = tools.FirstOrDefault(tool => tool.Name == forcedTool);
                    if (routedTool is not null && HasNoRequiredArguments(routedTool.Parameters))
                    {
                        if (toolCallCount >= 4) throw new ToolCallLimitExceededException(4);
                        using var emptyArguments = JsonDocument.Parse("{}");
                        var toolResult = EnsureJsonToolResult(
                            await toolProvider.InvokeAsync(routedTool.Name, emptyArguments.RootElement, cancellationToken));
                        trace?.Invoke(
                            $"Direct routed fallback after empty model responses: {routedTool.Name} {{}}");
                        trace?.Invoke($"Tool result: {routedTool.Name} {FormatTraceResult(toolResult)}");
                        executedToolResults.Add((routedTool.Name, toolResult));
                        conversation.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "assistant",
                            ["content"] = "",
                            ["function_call"] = new Dictionary<string, object?>
                            {
                                ["name"] = routedTool.Name,
                                ["arguments"] = new Dictionary<string, object?>()
                            }
                        });
                        conversation.Add(new Dictionary<string, object?>
                        {
                            ["role"] = "function", ["name"] = routedTool.Name, ["content"] = toolResult
                        });
                        toolCallCount++;
                        toolWasInvoked = true;
                        lastToolName = routedTool.Name;
                        forcedTool = null;
                        forceTextResponse = true;
                        continue;
                    }
                }
                if (toolWasInvoked)
                {
                    trace?.Invoke(
                        $"Empty final response after retries. Returning grounded results for {executedToolResults.Count} completed tool call(s).");
                    return new LlmResponse(
                        BuildGroundedToolReport(executedToolResults),
                        "tool_results_empty_final_fallback",
                        new TokenUsage(0, totalCompletion, totalPrompt, totalPrompt, 0, totalCompletion, totalBilled, false));
                }
                throw new InvalidOperationException(
                    $"GigaChat не вернул ни текст, ни вызов инструмента. finish_reason={emptyFinishReason}; " +
                    $"last_tool={lastToolName ?? "нет"}; message={message.GetRawText()}");
            }
            var suggestedTool = tools.FirstOrDefault(tool => content.Contains(tool.Name, StringComparison.Ordinal));
            if (suggestedTool is not null && !toolWasInvoked && route.Action == "call_tool" && step < 3)
            {
                conversation.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = content });
                conversation.Add(new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = "Не описывай вызов инструмента пользователю. Выполни предложенный инструмент сейчас и затем ответь по фактическому результату."
                });
                forcedTool = suggestedTool.Name;
                continue;
            }
            var finishReason = choice.TryGetProperty("finish_reason", out var finish) ? finish.GetString() ?? "не указана" : "не указана";
            var finalContent = toolWasInvoked
                ? BuildGroundedToolReport(executedToolResults)
                : content.Trim();
            return new LlmResponse(
                finalContent,
                toolWasInvoked ? "tool_results_grounded" : finishReason,
                new TokenUsage(0, totalCompletion, totalPrompt, totalPrompt, 0, totalCompletion, totalBilled, false));
        }

        throw new ToolCallLimitExceededException(4);
    }

    private static bool HasNoRequiredArguments(JsonElement parameters) =>
        !parameters.TryGetProperty("required", out var required) ||
        required.ValueKind != JsonValueKind.Array ||
        required.GetArrayLength() == 0;

    private static string BuildGroundedToolReport(IReadOnlyCollection<(string Name, string Result)> results)
    {
        return string.Join(
            Environment.NewLine + Environment.NewLine,
            results.Select(result => $"Инструмент: {result.Name}{Environment.NewLine}Фактический результат: {result.Result}"));
    }

    private static string? ExtractActiveProfile(string instructions)
    {
        const string startTag = "[ACTIVE_USER_PROFILE";
        const string endTag = "[/ACTIVE_USER_PROFILE]";
        var start = instructions.IndexOf(startTag, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = instructions.IndexOf(endTag, start, StringComparison.Ordinal);
        return end < 0 ? null : instructions[start..(end + endTag.Length)];
    }

    public async Task<TokenCountResult> CountTextTokensAsync(
        IReadOnlyCollection<string> texts,
        CancellationToken cancellationToken = default)
    {
        if (texts.Count == 0)
        {
            return new TokenCountResult(0, false);
        }

        await EnsureAccessTokenAsync(cancellationToken);
        var result = await CountTokensByTextAsync(texts, cancellationToken);
        return new TokenCountResult(result.Counts.Sum(), result.IsEstimated);
    }

    private async Task<TextTokenCounts> CountTokensByTextAsync(
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

        return new TextTokenCounts(counts, false);
    }

    private static TextTokenCounts EstimateTokenCounts(IEnumerable<string> input)
    {
        var counts = input
            .Select(text => (int)Math.Ceiling(text.EnumerateRunes().Count() / 3.5d))
            .ToArray();
        return new TextTokenCounts(counts, true);
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

    private static JsonElement NormalizeToolSchema(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())
            ?? throw new JsonException("Пустая JSON-схема инструмента.");
        NormalizeNode(node);
        return JsonSerializer.SerializeToElement(node);
    }

    private static string EnsureJsonToolResult(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return value;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { result = value });
        }
    }

    private static string FormatTraceResult(string value)
    {
        const int maxLength = 1500;
        var singleLine = value.Replace("\r", " ").Replace("\n", " ");
        return singleLine.Length <= maxLength
            ? singleLine
            : singleLine[..maxLength] + "... [обрезано]";
    }

    private async Task<string> GetAccessTokenForRouterAsync(CancellationToken cancellationToken)
    {
        await EnsureAccessTokenAsync(cancellationToken);
        return _accessToken!;
    }

    private static void NormalizeNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["type"] is JsonArray types)
            {
                obj["type"] = types
                    .Select(item => item?.GetValue<string>())
                    .FirstOrDefault(value => value is not null && value != "null") ?? "string";
            }
            foreach (var child in obj.ToArray().Select(pair => pair.Value).Where(value => value is not null))
            {
                NormalizeNode(child!);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array.Where(value => value is not null)) NormalizeNode(child!);
        }
    }

    private sealed record TextTokenCounts(int[] Counts, bool IsEstimated);
}
