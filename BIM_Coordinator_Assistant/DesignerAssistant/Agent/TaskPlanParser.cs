using System.Text.Json;
using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public static class TaskPlanParser
{
    public static TaskPlan ParseAndValidate(
        string content,
        IReadOnlyCollection<ToolDefinition> tools,
        string? sourceQuery = null)
    {
        try
        {
            using var document = JsonDocument.Parse(ExtractJson(content));
            var root = document.RootElement;
            var summary = RequiredString(root, "summary");
            var clarification = OptionalString(root, "clarification")?.Trim();
            if (!root.TryGetProperty("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Поле steps должно быть массивом.");
            if (stepsElement.GetArrayLength() == 0 && string.IsNullOrWhiteSpace(clarification))
                throw new InvalidDataException("Поле steps должно быть непустым массивом, если уточнение не требуется.");
            if (stepsElement.GetArrayLength() > 0 && !string.IsNullOrWhiteSpace(clarification))
                throw new InvalidDataException("Planning не может одновременно вернуть план и запрос уточнения.");

            var toolsByName = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
            var steps = new List<TaskPlanStep>();
            foreach (var element in stepsElement.EnumerateArray())
            {
                var action = RequiredString(element, "action");
                var toolName = OptionalToolName(element);
                var arguments = ReadArguments(element, "arguments");
                var sources = ReadSources(element, "argumentSources");
                if (toolName is not null)
                {
                    if (!toolsByName.TryGetValue(toolName, out var tool))
                        throw new InvalidDataException($"Инструмент '{toolName}' отсутствует в текущем каталоге.");
                    ValidateRequiredArguments(tool, arguments, sources);
                }
                ValidateLiteralPaths(arguments, sourceQuery);
                steps.Add(new TaskPlanStep(action, toolName, arguments, sources));
            }
            return new TaskPlan(summary, steps, clarification);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Planning вернул некорректный JSON: {exception.Message}", exception);
        }
    }

    private static void ValidateLiteralPaths(
        IReadOnlyDictionary<string, JsonElement> arguments,
        string? sourceQuery)
    {
        if (string.IsNullOrWhiteSpace(sourceQuery)) return;

        foreach (var (name, value) in arguments)
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            var text = value.GetString();
            if (string.IsNullOrWhiteSpace(text) || !Path.IsPathFullyQualified(text)) continue;
            if (!sourceQuery.Contains(text, StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Абсолютный путь в аргументе '{name}' изменён относительно запроса пользователя. " +
                    "Скопируй путь дословно, сохранив кириллицу, пробелы и регистр.");
        }
    }

    private static void ValidateRequiredArguments(
        ToolDefinition tool,
        IReadOnlyDictionary<string, JsonElement> arguments,
        IReadOnlyDictionary<string, string> sources)
    {
        if (!tool.Parameters.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array) return;
        foreach (var item in required.EnumerateArray())
        {
            var name = item.GetString();
            if (!string.IsNullOrWhiteSpace(name) && !arguments.ContainsKey(name) && !sources.ContainsKey(name))
                throw new InvalidDataException($"В шаге '{tool.Name}' не задан обязательный аргумент '{name}' и его источник.");
        }
    }

    private static Dictionary<string, JsonElement> ReadArguments(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Поле {name} должно быть объектом.");
        return value.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ReadSources(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return [];
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"Поле {name} должно быть объектом.");
        return value.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString() ?? "", StringComparer.Ordinal);
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) is { Length: > 0 } value ? value : throw new InvalidDataException($"Не задано поле {name}.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? OptionalToolName(JsonElement element)
    {
        var value = OptionalString(element, "tool")?.Trim();
        return string.IsNullOrWhiteSpace(value)
               || value.Equals("null", StringComparison.OrdinalIgnoreCase)
               || value.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    private static string ExtractJson(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }
}
