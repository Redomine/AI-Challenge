using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DesignerAssistant.Configuration;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public sealed record ToolRouteDecision(
    string Action,
    string? ToolName,
    string Reason,
    int PromptTokens,
    int CompletionTokens,
    int BilledTokens,
    string? DirectResponse = null);

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
        string? routingContext = null,
        CancellationToken cancellationToken = default)
    {
        var toolNames = tools.Select(tool => tool.Name).ToArray();
        var latestUserMessage = messages.LastOrDefault(message =>
            message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Content ?? "";
        latestUserMessage = ExtractTaskQuery(latestUserMessage);
        if (ToolCapabilityCatalog.IsBridgeQuestion(latestUserMessage))
        {
            return new ToolRouteDecision(
                "answer",
                null,
                "Вопрос о MCP Bridge обработан по проверенному описанию доступных инструментов.",
                0,
                0,
                0,
                ToolCapabilityCatalog.BuildBridgeHelp(tools));
        }
        if (ToolCapabilityCatalog.TryDetectQuestion(latestUserMessage, out var capabilityScope))
        {
            return new ToolRouteDecision(
                "answer",
                null,
                "Вопрос о возможностях обработан по актуальному каталогу инструментов без LLM-вызова.",
                0,
                0,
                0,
                ToolCapabilityCatalog.BuildUserSummary(tools, capabilityScope));
        }
        if (IsViewInventoryRequest(latestUserMessage))
        {
            if (toolNames.Contains("revit_custom_summarize_elements", StringComparer.Ordinal))
            {
                return new ToolRouteDecision(
                    "call_tool",
                    "revit_custom_summarize_elements",
                    "Запрос состава вида направлен в специализированный инструмент инвентаризации.",
                    0,
                    0,
                    0);
            }

            return new ToolRouteDecision(
                "answer",
                null,
                "Инструмент инвентаризации вида отсутствует в текущем MCP-каталоге.",
                0,
                0,
                0,
                "Сейчас инструмент чтения состава вида недоступен в запущенном rvt-mcp. Я не буду угадывать элементы по сведениям о виде. Перезапустите Revit и ассистента после установки custom-плагина.");
        }
        if (IsSelectionRequest(latestUserMessage) &&
            toolNames.Contains("revit_get_selected_elements", StringComparer.Ordinal))
        {
            return new ToolRouteDecision(
                "call_tool",
                "revit_get_selected_elements",
                "Запрос относится к текущему выделению Revit; ElementId нужно получить автоматически.",
                0,
                0,
                0);
        }
        if (IsDocumentInfoRequest(latestUserMessage))
        {
            const string projectInfoTool = "revit_get_project_info";
            if (toolNames.Contains(projectInfoTool, StringComparer.Ordinal))
            {
                return new ToolRouteDecision(
                    "call_tool",
                    projectInfoTool,
                    "Запрос сведений об открытом документе направлен в специализированный инструмент.",
                    0,
                    0,
                    0);
            }

            return new ToolRouteDecision(
                "no_tool",
                null,
                "В текущем MCP-каталоге нет инструмента чтения сведений об открытом документе.",
                0,
                0,
                0,
                "У меня нет подходящих инструментов");
        }
        var deterministicTool = MatchDeterministicRoute(latestUserMessage, toolNames);
        if (deterministicTool is not null)
        {
            return new ToolRouteDecision(
                "call_tool",
                deterministicTool,
                "Однозначный запрос к Revit распознан локальным маршрутизатором.",
                0,
                0,
                0);
        }
        var catalogue = ToolCapabilityCatalog.BuildCompactRouterCatalogue(tools);
        var dialogue = string.Join(
            Environment.NewLine,
            messages.TakeLast(8).Select(message => $"{message.Role}: {message.Content}"));
        var parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["action"] = new { type = "string", @enum = new[] { "answer", "call_tool", "clarify", "no_tool" } },
                ["tool"] = new { type = "string", @enum = toolNames },
                ["reason"] = new { type = "string" },
                ["clarification"] = new { type = "string" }
            },
            ["required"] = new[] { "action", "reason" }
        };
        var requestMessages = new[]
        {
            new
            {
                role = "system",
                content = "Ты маршрутизатор инструментов. Выбери call_tool, если для ответа нужны актуальные данные Revit или workspace, либо пользователь явно просит выполнить однозначное действие и подходящий инструмент есть в каталоге. Выбери no_tool, если запрос требует актуальных данных или действия, но подходящего инструмента в каталоге нет. Выбери answer для разговора, объяснения или данных, уже присутствующих в диалоге. Выбери clarify, если для операции не определён объект, путь, проект, тест, обязательное значение или намерение изменить модель; сформулируй один короткий вопрос в clarification и не выбирай инструмент. Вопросы о названиях, назначении, параметрах или доступности инструментов всегда получают answer и никогда не вызывают инструмент. Слова 'команда', 'вызов', 'скрипт' и название инструмента сами по себе не означают просьбу выполнить действие. Не отвечай на задачу пользователя."
            },
            new
            {
                role = "user",
                content = $"Применимые инструкции профиля:\n{routingContext ?? "Не заданы."}\n\nДиалог:\n{dialogue}\n\nДоступные инструменты:\n{catalogue}"
            }
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
        var clarification = arguments.TryGetProperty("clarification", out var clarificationElement)
            ? clarificationElement.GetString()
            : null;
        if (action == "call_tool" && !toolNames.Contains(toolName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"ToolRouter выбрал неизвестный инструмент '{toolName}'.");
        }

        var usage = root.GetProperty("usage");
        if (action == "clarify" && string.IsNullOrWhiteSpace(clarification))
        {
            clarification = "Уточните, пожалуйста, какое именно изменение нужно выполнить, над каким объектом и с каким значением.";
        }
        if (action == "no_tool")
        {
            clarification = "У меня нет подходящих инструментов";
        }

        return new ToolRouteDecision(
            action,
            action == "call_tool" ? toolName : null,
            reason,
            ReadInt(usage, "prompt_tokens"),
            ReadInt(usage, "completion_tokens"),
            ReadInt(usage, "total_tokens"),
            action is "clarify" or "no_tool" ? clarification : null);
    }

    private static string ExtractTaskQuery(string message)
    {
        const string startTag = "[QUERY]";
        const string endTag = "[/QUERY]";
        var start = message.IndexOf(startTag, StringComparison.Ordinal);
        if (start < 0) return message;
        start += startTag.Length;
        var end = message.IndexOf(endTag, start, StringComparison.Ordinal);
        return end < 0 ? message : message[start..end].Trim();
    }

    private static string? MatchDeterministicRoute(string message, IReadOnlyCollection<string> toolNames)
    {
        bool Has(string name) => toolNames.Contains(name, StringComparer.Ordinal);
        if (Has("workspace_dotnet_test") &&
            Regex.IsMatch(message, @"\b(запусти|выполни|прогони|проверь)\w*\b.{0,45}\bтест\w*\b|\bdotnet\s+test\b", RegexOptions.IgnoreCase))
            return "workspace_dotnet_test";
        if (Has("workspace_dotnet_build") &&
            Regex.IsMatch(message, @"\b(собери|сборк\w*|build)\b.{0,60}\b(проект\w*|решени\w*|solution|csproj|sln)?\b", RegexOptions.IgnoreCase))
            return "workspace_dotnet_build";
        if (Has("workspace_read_text_file") &&
            Regex.IsMatch(message, @"\b(прочитай|открой|покажи содержимое)\b.{0,45}\b(файл\w*|\.cs|\.json|\.md|\.razor)\b", RegexOptions.IgnoreCase))
            return "workspace_read_text_file";
        if (Has("workspace_search_text") &&
            Regex.IsMatch(message, @"\b(найди|поищи)\b.{0,60}\b(в проекте|в файлах|использовани\w*|упоминани\w*)\b", RegexOptions.IgnoreCase))
            return "workspace_search_text";
        if (Has("workspace_path_exists") &&
            Regex.IsMatch(message, @"\b(существует|есть ли)\b.{0,60}\b(файл\w*|каталог\w*|папк\w*|проект\w*)\b", RegexOptions.IgnoreCase))
            return "workspace_path_exists";
        if (Has("workspace_list_processes") &&
            Regex.IsMatch(message, @"\b(запущен|работает|процесс)\w*\b.{0,35}\b(revit|rider|dotnet|процесс\w*)\b", RegexOptions.IgnoreCase))
            return "workspace_list_processes";
        if (Has("workspace_is_port_listening") &&
            Regex.IsMatch(message, @"\b(порт)\s+\d{1,5}\b.{0,30}\b(слуш|доступ|открыт|работает)\w*\b|\b(слуш|доступ|открыт)\w*\b.{0,30}\bпорт\s+\d{1,5}\b", RegexOptions.IgnoreCase))
            return "workspace_is_port_listening";
        if (Has("workspace_run_command_recipe") &&
            Regex.IsMatch(message, @"\b(git\s+status|git\s+diff|текущ\w*\s+ветк\w*|dotnet\s+--info|сведени\w*\s+о\s+dotnet)\b", RegexOptions.IgnoreCase))
            return "workspace_run_command_recipe";
        if (Has("revit_custom_open_model") &&
            Regex.IsMatch(message, @"\b(открой|открыть|загрузи|загрузить)\b.{0,180}\.rvt\b|\.rvt\b.{0,180}\b(открой|открыть|загрузи|загрузить)\b", RegexOptions.IgnoreCase))
            return "revit_custom_open_model";
        if (Has("revit_custom_load_families") &&
            Regex.IsMatch(message, @"\b(загрузи|загрузить|подгрузи|подгрузить|обнови|обновить)\w*\b.{0,180}\b(семейств\w*|\.rfa\b|папк\w*)", RegexOptions.IgnoreCase))
            return "revit_custom_load_families";
        if (Has("revit_custom_sync_relinquish_and_close") &&
            Regex.IsMatch(message, @"\b(синхронизируй|синхронизировать|отсинхронизируй|отсинхронизироваться)\w*\b.{0,100}\b(освобод\w*|закрой|закрыть)\b", RegexOptions.IgnoreCase))
            return "revit_custom_sync_relinquish_and_close";
        if (Has("revit_custom_open_family") &&
            Regex.IsMatch(message, @"\b(открой|открыть)\b.{0,180}\.rfa\b|\.rfa\b.{0,180}\b(открой|открыть)\b", RegexOptions.IgnoreCase))
            return "revit_custom_open_family";
        if (Has("revit_custom_execute_pyrevit_command") &&
            Regex.IsMatch(message, @"\.pushbutton\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(message, @"\b(запусти|запустить|выполни|выполнить|расчет|расчёт)\w*\b", RegexOptions.IgnoreCase))
            return "revit_custom_execute_pyrevit_command";
        if (Has("revit_custom_find_pyrevit_buttons") &&
            !Regex.IsMatch(message, @"\.pushbutton\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(message, @"\b(pyrevit|пиревит)\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(message, @"\b(запусти|запустить|выполни|выполнить)\w*\b.{0,100}\b(скрипт\w*|команд\w*|кнопк\w*|расчет\w*|расчёт\w*)\b", RegexOptions.IgnoreCase))
            return "revit_custom_find_pyrevit_buttons";
        if (Has("revit_custom_list_pyrevit_output_windows") &&
            Regex.IsMatch(message, @"\b(покажи|прочитай|проверь|перечисли)\w*\b.{0,70}\b(консол\w*|окн\w*\s+вывод\w*)\b.{0,30}\b(pyrevit|пиревит)\b|\b(pyrevit|пиревит)\b.{0,40}\b(консол\w*|окн\w*\s+вывод\w*)\b", RegexOptions.IgnoreCase))
            return "revit_custom_list_pyrevit_output_windows";
        if (Has("revit_custom_close_pyrevit_output_window") &&
            Regex.IsMatch(message, @"\b(закрой|закрыть)\w*\b.{0,70}\b(консол\w*|окн\w*)\b.{0,30}\b(pyrevit|пиревит)\b|\b(закрой|закрыть)\w*\b.{0,50}\boutputUniqueId\b", RegexOptions.IgnoreCase))
            return "revit_custom_close_pyrevit_output_window";
        if (Has("revit_custom_find_pyrevit_buttons") &&
            Regex.IsMatch(message, @"\b(найди|покажи|перечисли|отыщи)\w*\b.{0,80}\b(кнопк\w*|команд\w*)\b.{0,35}\b(pyrevit|пиревит)\b|\b(pyrevit|пиревит)\b.{0,45}\b(command\s*id|commandId|кнопк\w*|команд\w*)\b", RegexOptions.IgnoreCase))
            return "revit_custom_find_pyrevit_buttons";
        if (Has("revit_custom_unload_links_locally") &&
            Regex.IsMatch(message, @"\b(выгрузи|выгрузить|отключи|отключить)\b.{0,60}\b(все\s+)?(revit[- ]?)?(связи|линки)\b", RegexOptions.IgnoreCase))
            return "revit_custom_unload_links_locally";
        if (Has("revit_select_elements") &&
            Regex.IsMatch(message, @"\b(выдели|выбери)\b.{0,80}(?<!\d)\d{2,}(?!\d)", RegexOptions.IgnoreCase))
            return "revit_select_elements";
        if (Has("revit_get_element_details") &&
            Regex.IsMatch(message, @"(?<!\d)\d{5,}(?!\d)") &&
            Regex.IsMatch(message, @"элемент|объект|что\s+(это|за)", RegexOptions.IgnoreCase))
        {
            return "revit_get_element_details";
        }

        if (Has("revit_get_current_view_info") &&
            Regex.IsMatch(message, @"активн\w*\s+вид", RegexOptions.IgnoreCase))
        {
            return "revit_get_current_view_info";
        }

        return null;
    }

    private static bool IsDocumentInfoRequest(string message) =>
        !string.IsNullOrWhiteSpace(message) &&
        Regex.IsMatch(
            message,
            @"\b(что|какой|который|назови|покажи)\b.{0,45}\b(документ|проект|файл)\b.{0,25}\b(открыт|загружен|активн|текущ)\w*\b|\b(открыт|активн|текущ)\w*\s+(документ|проект|файл)\b",
            RegexOptions.IgnoreCase);

    private static bool IsViewInventoryRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var text = Regex.Replace(message.ToLowerInvariant(), @"\s+", " ");
        var mentionsView = Regex.IsMatch(
            text,
            @"\b(активн\w*|текущ\w*|открыт\w*|эт\w*)\s+вид\w*\b|\bна\s+вид\w*\b|\bвид\w*\s+(с\s+)?имен\w*\b");
        var asksForContents = Regex.IsMatch(
            text,
            @"\b(какие|что|сколько|перечисли|покажи|состав|сводк\w*|статистик\w*)\b.{0,55}\b(элемент\w*|объект\w*|категори\w*|оборудован\w*|воздуховод\w*|труб\w*)\b") ||
            Regex.IsMatch(text, @"\b(что|кто)\s+(есть|находится|расположен\w*|показан\w*|видно)\b");
        return mentionsView && asksForContents;
    }

    private static bool IsSelectionRequest(string message) =>
        !string.IsNullOrWhiteSpace(message) && Regex.IsMatch(
            message,
            @"\b(выбран\w*|выделен\w*|отмечен\w*|текущ\w*\s+выбор\w*|в\s+выборе)\b",
            RegexOptions.IgnoreCase);

    private static int ReadInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
}
