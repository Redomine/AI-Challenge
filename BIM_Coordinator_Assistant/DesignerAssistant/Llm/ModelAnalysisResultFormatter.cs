using System.Text.Json;
using DesignerAssistant.Models;
using ToonFormat;

namespace DesignerAssistant.Llm;

public static class ModelAnalysisResultFormatter
{
    private const int MaxContextCharacters = 65536;
    private static readonly HashSet<string> Tools = new(StringComparer.Ordinal)
    {
        "revit_custom_collect_mep_elements", "revit_custom_collect_category_elements",
        "revit_custom_filter_selection", "revit_custom_summarize_selection",
        "revit_custom_get_selection_page", "revit_custom_export_selection_json"
    };

    public static string Format(ToolResultEnvelope result)
    {
        if (!result.Ok || !Tools.Contains(result.Tool) || result.Result is not { } payload)
            return result.ToJson();

        var json = payload.GetRawText();
        if (json.Length > MaxContextCharacters)
            throw new InvalidDataException($"Результат {result.Tool} превышает лимит передачи в контекст.");
        if (ContainsKey(payload, "elementIds") || ContainsKey(payload, "ids") ||
            result.Tool != "revit_custom_get_selection_page" &&
            (ContainsKey(payload, "elements") || ContainsKey(payload, "items")))
            throw new InvalidDataException($"Результат {result.Tool} содержит неожиданный список элементов.");
        if (result.Tool == "revit_custom_get_selection_page" &&
            TryFindArray(payload, "items", out var items) && items.GetArrayLength() > 20)
            throw new InvalidDataException("Страница выборки превышает лимит 20 элементов.");

        var toon = Toon.FromJson(json);
        if (toon.Length > MaxContextCharacters)
            throw new InvalidDataException($"TOON-результат {result.Tool} превышает лимит передачи в контекст.");
        return $"TOON result for {result.Tool} (ok=true, completed={result.Completed.ToString().ToLowerInvariant()}):\n{toon}";
    }

    private static bool ContainsKey(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object)
            return element.EnumerateObject().Any(property =>
                property.NameEquals(key) || ContainsKey(property.Value, key));
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(item => ContainsKey(item, key));
        return false;
    }

    private static bool TryFindArray(JsonElement element, string key, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(key) && property.Value.ValueKind == JsonValueKind.Array)
                {
                    found = property.Value;
                    return true;
                }
                if (TryFindArray(property.Value, key, out found)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (TryFindArray(item, key, out found)) return true;
        }
        found = default;
        return false;
    }
}
