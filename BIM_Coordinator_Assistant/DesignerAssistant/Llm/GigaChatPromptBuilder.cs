using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesignerAssistant.Configuration;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public sealed class GigaChatPromptBuilder : IPromptBuilder
{
    private const string AuthUrl = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";
    private const string ChatUrl = "https://api.giga.chat/v1/chat/completions";

    private readonly HttpClient _httpClient;
    private readonly AppOptions _options;
    private readonly PromptBuilderOptions _builderOptions;
    private readonly Func<CancellationToken, Task<string>> _getAccessToken;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private bool? _available;
    private string? _availabilityError;
    private readonly object _availabilityLock = new();

    public GigaChatPromptBuilder(
        HttpClient httpClient,
        AppOptions options,
        PromptBuilderOptions builderOptions,
        Func<CancellationToken, Task<string>>? getAccessToken = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _builderOptions = builderOptions ?? throw new ArgumentNullException(nameof(builderOptions));
        _getAccessToken = getAccessToken ?? GetAccessTokenAsync;
    }

    public string ModelName => _builderOptions.Model;

    public bool IsAvailable
    {
        get
        {
            lock (_availabilityLock)
            {
                if (_available.HasValue) return _available.Value;
                _available = false;
                _availabilityError = "Доступность Ultra ещё не проверена.";
                return false;
            }
        }
    }

    public string? AvailabilityError
    {
        get
        {
            lock (_availabilityLock) return _availabilityError;
        }
    }

    public async Task<PromptUnderstanding> BuildAsync(string query, CancellationToken cancellationToken = default, string? revisionContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (!_builderOptions.Enabled)
        {
            return Fallback(query, "Prompt-builder отключён в конфигурации.");
        }

        if (!await EnsureAvailabilityAsync(cancellationToken))
        {
            return Fallback(query, _availabilityError ?? "GigaChat-3-Ultra недоступна для текущего ключа.");
        }

        var accessToken = await _getAccessToken(cancellationToken);
        var userMessage = string.IsNullOrWhiteSpace(revisionContext)
            ? query
            : $"[USER_QUERY]\n{query}\n[/USER_QUERY]\n\n[BUILDER_REVISION_CONTEXT]\n{revisionContext.Trim()}\n[/BUILDER_REVISION_CONTEXT]";
        var requestMessages = new[]
        {
            new
            {
                role = "system",
                content = BuildSystemPrompt()
            },
            new
            {
                role = "user",
                content = userMessage
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
        {
            Content = JsonContent.Create(new
            {
                model = _builderOptions.Model,
                messages = requestMessages,
                max_tokens = _builderOptions.MaxOutputTokens,
                response_format = new { type = "json_schema", schema = BuilderSchema, strict = true }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var unavailableReason = $"HTTP {(int)response.StatusCode}: {TryGetErrorMessage(body)}";
            MarkUnavailable(unavailableReason);
            return Fallback(query, unavailableReason);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content))
            {
                return Fallback(query, "GigaChat-3-Ultra вернула пустой ответ.");
            }
            var understanding = ParseUnderstanding(content, query);
            if (understanding is null)
            {
                return Fallback(query, "GigaChat-3-Ultra вернула неожидаемый JSON.");
            }
            return understanding;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return Fallback(query, $"Ошибка разбора ответа Ultra: {exception.Message}");
        }
    }

    private PromptUnderstanding Fallback(string query, string reason) =>
        new(
            OriginalQuery: query,
            Goal: query,
            Constraints: [],
            RequiredData: [],
            SuccessCriteria: [],
            Model: _builderOptions.Model,
            UsedFallback: true,
            FailureReason: reason);

    private static string BuildSystemPrompt() => """
        Ты внутренний prompt-builder ассистента BIM-координатора. Анализируй запрос пользователя и возвращай только структурированный JSON.

        Строгие правила:
        1. Не придумывай действия, инструменты, источники данных, идентификаторы, среды или имена приложений, которых пользователь явно не назвал. Если запрос упоминает «выбранные»/«выделенные»/«отмеченные» элементы, ElementId и параметры будут получены из MCP-инструмента revit_get_selected_elements, а не запрошены у пользователя. Не добавляй в requiredData просьбу ввести ElementId, GUID, имя процесса Revit или путь к файлу, если пользователь их не указал.
        2. constraints — только то, что пользователь явно ограничил (например, «без изменения модели», «только в выбранных», «запиши значение X»). Не выводи общие «подразумеваемые» ограничения и не додумывай, в какой среде идёт работа.
        3. requiredData — только реальные пробелы в запросе: что нужно уточнить у пользователя, прежде чем планировать. Не запрашивай данные, которые инструменты получают сами (ElementId выделенных элементов, параметры из revit_get_element_parameters, состояние модели через rvt-mcp). Если данных хватает и нужный инструмент очевиден, оставь requiredData пустым массивом.
        4. successCriteria — только признаки, прямо названные пользователем. Не добавляй визуальную подсветку, экспорт в CSV, уведомления и т.п., если пользователь их не просил.
        5. goal — короткое дословное отражение намерения пользователя без выдуманных действий.
        6. Сохраняй кириллицу, регистр, имена файлов и числовые значения дословно. Не переводи и не нормализуй.
        7. Возвращай только JSON без Markdown по схеме:
        {"goal":"краткая цель запроса","constraints":["..."],"requiredData":["..."],"successCriteria":["..."]}
        Массивы могут быть пустыми. Никаких других полей, никакого текста вокруг.
        """;

    private static readonly JsonElement BuilderSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            goal = new { type = "string" },
            constraints = new { type = "array", items = new { type = "string" } },
            requiredData = new { type = "array", items = new { type = "string" } },
            successCriteria = new { type = "array", items = new { type = "string" } }
        },
        required = new[] { "goal", "constraints", "requiredData", "successCriteria" }
    });

    private PromptUnderstanding? ParseUnderstanding(string content, string query)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var goal = root.TryGetProperty("goal", out var goalElement) ? goalElement.GetString() ?? query : query;
            var constraints = ReadStringArray(root, "constraints");
            var requiredData = ReadStringArray(root, "requiredData");
            var successCriteria = ReadStringArray(root, "successCriteria");
            SanitizeUnderstanding(query, ref goal, ref constraints, ref requiredData, ref successCriteria);
            return new PromptUnderstanding(
                OriginalQuery: query,
                Goal: goal,
                Constraints: constraints,
                RequiredData: requiredData,
                SuccessCriteria: successCriteria,
                Model: _builderOptions.Model,
                UsedFallback: false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
            return [];
        var items = new List<string>();
        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text)) items.Add(text);
            }
        }
        return items;
    }

    private static void SanitizeUnderstanding(
        string query,
        ref string goal,
        ref IReadOnlyList<string> constraints,
        ref IReadOnlyList<string> requiredData,
        ref IReadOnlyList<string> successCriteria)
    {
        var mentionsSelected = Regex.IsMatch(query, @"\b(выбран\w*|выделен\w*|отмечен\w*|текущ\w*\s+выбор\w*|в\s+выборе)\b", RegexOptions.IgnoreCase);
        if (!mentionsSelected) return;

        requiredData = StripAskedForSelectedElementIds(requiredData);
    }

    private static IReadOnlyList<string> StripAskedForSelectedElementIds(IReadOnlyList<string> requiredData)
    {
        if (requiredData.Count == 0) return requiredData;
        var patterns = new[]
        {
            @"введи(те)?\b.*\b(ID|Id|идентификатор|номер\w*).*\b(выбран|выделен|отмечен)",
            @"укажи(те)?\b.*\b(ID|Id|идентификатор|номер\w*).*\b(выбран|выделен|отмечен)",
            @"назови(те)?\b.*\b(ID|Id|идентификатор|номер\w*).*\b(выбран|выделен|отмечен)",
            @"пришли(те)?\b.*\b(ID|Id|идентификатор|номер\w*).*\b(выбран|выделен|отмечен)",
            @"список\s+(ID|Id|идентификатор\w*).*\b(выбран|выделен|отмечен)",
            @"перечень\s+(ID|Id|идентификатор\w*).*\b(выбран|выделен|отмечен)"
        };
        var result = new List<string>();
        foreach (var item in requiredData)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }
            if (patterns.Any(pattern => Regex.IsMatch(item, pattern, RegexOptions.IgnoreCase)))
            {
                continue;
            }
            result.Add(item);
        }
        return result;
    }

    private async Task<bool> EnsureAvailabilityAsync(CancellationToken cancellationToken)
    {
        lock (_availabilityLock)
        {
            if (_available == true) return true;
            if (_available == false && !string.IsNullOrWhiteSpace(_availabilityError) &&
                _availabilityError.StartsWith("HTTP 4", StringComparison.Ordinal))
            {
                return false;
            }
        }

        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probeCts.CancelAfter(TimeSpan.FromSeconds(_builderOptions.ProbeTimeoutSeconds));
            var accessToken = await _getAccessToken(probeCts.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
            {
                Content = JsonContent.Create(new
                {
                    model = _builderOptions.Model,
                    messages = new[]
                    {
                        new { role = "system", content = "ping" },
                        new { role = "user", content = "ok" }
                    },
                    max_tokens = 4
                })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await _httpClient.SendAsync(request, probeCts.Token);
            if (response.IsSuccessStatusCode)
            {
                lock (_availabilityLock)
                {
                    _available = true;
                    _availabilityError = null;
                }
                return true;
            }
            var body = await response.Content.ReadAsStringAsync(probeCts.Token);
            var message = $"HTTP {(int)response.StatusCode}: {TryGetErrorMessage(body)}";
            lock (_availabilityLock)
            {
                _available = false;
                _availabilityError = message;
            }
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_availabilityLock)
            {
                _available = false;
                _availabilityError = exception.Message;
            }
            return false;
        }
    }

    private void MarkUnavailable(string reason)
    {
        lock (_availabilityLock)
        {
            _available = false;
            _availabilityError = reason;
        }
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && _tokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return _accessToken;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, AuthUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["scope"] = _options.Scope })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("RqUID", Guid.NewGuid().ToString());
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            _options.AuthorizationKey.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                ? _options.AuthorizationKey[6..].Trim()
                : _options.AuthorizationKey.Trim());

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"OAuth GigaChat вернул {(int)response.StatusCode} {response.ReasonPhrase}: {TryGetErrorMessage(body)}");
        }
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        _accessToken = root.GetProperty("access_token").GetString()
            ?? throw new JsonException("OAuth не вернул access_token.");
        var expiresAt = root.GetProperty("expires_at").GetInt64();
        _tokenExpiresAt = expiresAt > 10_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(expiresAt)
            : DateTimeOffset.FromUnixTimeSeconds(expiresAt);
        return _accessToken;
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