using System.Text;
using System.Text.RegularExpressions;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public enum ToolCapabilityScope
{
    General,
    View,
    Element
}

public sealed record ToolCapability(
    string ToolName,
    string Title,
    string Description,
    string Category,
    ToolCapabilityScope Scope,
    bool IsWrite,
    string Example);

public static class ToolCapabilityCatalog
{
    private static readonly IReadOnlyDictionary<string, ToolCapability> Known =
        new Dictionary<string, ToolCapability>(StringComparer.Ordinal)
        {
            ["revit_list_available_targets"] = Read("revit_list_available_targets", "Показать доступные сеансы Revit", "Находит запущенные сеансы Revit, к которым может подключиться помощник.", "Подключение", ToolCapabilityScope.General, "Какие сеансы Revit доступны?"),
            ["revit_get_current_target"] = Read("revit_get_current_target", "Показать активный сеанс Revit", "Сообщает, к какому сеансу и документу Revit сейчас подключён помощник.", "Подключение", ToolCapabilityScope.General, "К какому Revit ты подключён?"),
            ["revit_switch_target"] = Read("revit_switch_target", "Переключить сеанс Revit", "Переключает подключение помощника на доступную версию или сеанс Revit; модель не изменяет.", "Подключение", ToolCapabilityScope.General, "Переключись на Revit 2022"),
            ["revit_get_current_view_info"] = Read("revit_get_current_view_info", "Получить сведения об активном виде", "Читает имя, тип и основные свойства текущего активного вида.", "Активный вид", ToolCapabilityScope.View, "Что сейчас открыто на активном виде?"),
            ["revit_get_selected_elements"] = Read("revit_get_selected_elements", "Прочитать выбранные элементы", "Возвращает элементы, которые пользователь выделил в активном документе Revit.", "Элементы", ToolCapabilityScope.Element, "Какие элементы сейчас выделены?"),
            ["revit_get_element_details"] = Read("revit_get_element_details", "Получить сведения об элементе", "Читает категорию, тип, семейство и другие основные сведения по ElementId.", "Элементы", ToolCapabilityScope.Element, "Что за элемент 5133824?"),
            ["revit_get_element_parameters"] = Read("revit_get_element_parameters", "Прочитать параметры элемента", "Возвращает параметры экземпляра указанного элемента без изменения модели.", "Параметры", ToolCapabilityScope.Element, "Покажи параметры элемента 5133824"),
            ["revit_get_type_parameters"] = Read("revit_get_type_parameters", "Прочитать параметры типа", "Возвращает параметры типа или семейства для указанного элемента.", "Параметры", ToolCapabilityScope.Element, "Какие параметры типа у элемента 5133824?"),
            ["revit_list_worksets"] = Read("revit_list_worksets", "Показать рабочие наборы", "Перечисляет рабочие наборы открытой модели и их состояние.", "Модель", ToolCapabilityScope.General, "Перечисли рабочие наборы модели"),
            ["revit_analyze_model_statistics"] = Read("revit_analyze_model_statistics", "Проанализировать статистику модели", "Собирает сводную статистику по категориям и элементам открытой модели.", "Модель", ToolCapabilityScope.General, "Покажи статистику модели"),
            ["revit_ai_element_filter"] = Read("revit_ai_element_filter", "Найти элементы по условиям", "Фильтрует элементы модели по категории, параметрам и другим заданным условиям.", "Элементы", ToolCapabilityScope.Element, "Найди стены на текущем уровне")
        };

    public static bool TryDetectQuestion(string message, out ToolCapabilityScope scope)
    {
        scope = ToolCapabilityScope.General;
        if (string.IsNullOrWhiteSpace(message)) return false;

        var text = Regex.Replace(message.ToLowerInvariant(), @"\s+", " ");
        var hasCapabilityNoun = Regex.IsMatch(
            text,
            @"\b(функционал\w*|возможност\w*|инструмент\w*|команд\w*|операци\w*|действи\w*)\b");
        var hasInquiryMarker = Regex.IsMatch(
            text,
            @"\b(что|какие|какой|чем|перечисли|покажи|опиши|расскажи|названи\w*|доступн\w*|есть|имеются|поддержива\w*)\b");
        var asksAboutCapability =
            hasCapabilityNoun && hasInquiryMarker ||
            Regex.IsMatch(text, @"\b(что|какие|какой|чем)\b.{0,50}\b(умеешь|можешь|можно|доступн\w*|поддерживаешь|способен)\b") ||
            Regex.IsMatch(text, @"\b(перечисли|покажи|опиши|расскажи)\b.{0,50}\b(функционал\w*|возможност\w*|инструмент\w*|команд\w*|операци\w*|действи\w*)\b") ||
            Regex.IsMatch(text, @"\b(функционал\w*|возможност\w*|инструмент\w*)\b.{0,35}\b(есть|доступн\w*|имеются|поддержива\w*)\b") ||
            Regex.IsMatch(text, @"\bчто\s+ты\s+умеешь\b|\bтвои\s+возможности\b|\bсписок\s+инструмент\w*\b");
        if (!asksAboutCapability) return false;

        if (Regex.IsMatch(text, @"\b(активн\w*|текущ\w*|открыт\w*|эт\w*)\s+вид\w*\b|\b(для|с|над)\s+вид\w*\b|\bвид\w*\s+revit\b"))
            scope = ToolCapabilityScope.View;
        else if (Regex.IsMatch(text, @"\b(элемент\w*|объект\w*|выделен\w*|параметр\w*)\b")) scope = ToolCapabilityScope.Element;
        return true;
    }

    public static IReadOnlyList<ToolCapability> Describe(IReadOnlyCollection<ToolDefinition> tools) =>
        tools.Select(Describe).ToArray();

    public static bool IsWriteTool(ToolDefinition tool) => Describe(tool).IsWrite;

    public static string BuildUserSummary(
        IReadOnlyCollection<ToolDefinition> tools,
        ToolCapabilityScope requestedScope)
    {
        var all = Describe(tools);
        var selected = requestedScope == ToolCapabilityScope.General
            ? all
            : all.Where(item => item.Scope == requestedScope || IsRelevantWrite(item, requestedScope)).ToArray();

        var heading = requestedScope switch
        {
            ToolCapabilityScope.View => "С активным видом через доступные инструменты я могу:",
            ToolCapabilityScope.Element => "С элементами через доступные инструменты я могу:",
            _ => "Сейчас мне доступны такие возможности:"
        };
        if (selected.Count == 0)
        {
            return $"{heading}\n\nВ текущем каталоге Revit нет инструментов для этой области. Я не буду придумывать недоступные операции.";
        }

        var builder = new StringBuilder(heading);
        foreach (var group in selected.GroupBy(item => item.Category))
        {
            builder.AppendLine().AppendLine().Append(group.Key).Append(':');
            foreach (var item in group)
            {
                builder.AppendLine()
                    .Append("- ").Append(item.Title).Append(" — ").Append(item.Description);
                if (item.IsWrite) builder.Append(" Требует отдельного подтверждения перед изменением модели.");
                builder.Append(" Например: «").Append(item.Example).Append("».");
            }
        }
        builder.AppendLine().AppendLine()
            .Append("Перечень сформирован по текущему каталогу rvt-mcp. Читающие операции выполняются сразу; изменяющие — только по явной команде и после подтверждения.");
        return builder.ToString();
    }

    public static string BuildCompactRouterCatalogue(IReadOnlyCollection<ToolDefinition> tools) =>
        string.Join(Environment.NewLine, Describe(tools).Select(item =>
            $"- {item.ToolName} | {(item.IsWrite ? "write" : "read")} | {item.Title}: {item.Description}"));

    private static ToolCapability Describe(ToolDefinition tool)
    {
        if (Known.TryGetValue(tool.Name, out var known)) return known;

        var isClearlyReadOnly = Regex.IsMatch(
            tool.Name,
            @"^revit_(get|list|analyze)_",
            RegexOptions.IgnoreCase);
        var isWrite = !isClearlyReadOnly;
        var scope = Regex.IsMatch(tool.Name, "view", RegexOptions.IgnoreCase)
            ? ToolCapabilityScope.View
            : Regex.IsMatch(tool.Name, "element|parameter|family|type|selection", RegexOptions.IgnoreCase)
                ? ToolCapabilityScope.Element
                : ToolCapabilityScope.General;
        var title = HumanizeName(tool.Name);
        var description = isWrite
            ? $"Выполняет операцию «{title.ToLowerInvariant()}» в Revit через rvt-mcp"
            : $"Читает из Revit данные операции «{title.ToLowerInvariant()}» без изменения модели";
        return new ToolCapability(
            tool.Name,
            title,
            description + ".",
            isWrite ? "Изменение модели" : CategoryFor(scope),
            scope,
            isWrite,
            isWrite ? $"Выполни операцию «{title.ToLowerInvariant()}»" : $"{title}");
    }

    private static ToolCapability Read(string name, string title, string description, string category, ToolCapabilityScope scope, string example) =>
        new(name, title, description, category, scope, false, example);

    private static bool IsRelevantWrite(ToolCapability item, ToolCapabilityScope scope) =>
        item.IsWrite && (item.Scope == scope || item.Scope == ToolCapabilityScope.General);

    private static string CategoryFor(ToolCapabilityScope scope) => scope switch
    {
        ToolCapabilityScope.View => "Активный вид",
        ToolCapabilityScope.Element => "Элементы",
        _ => "Модель"
    };

    private static string HumanizeName(string name)
    {
        var words = name.Replace("revit_", "", StringComparison.OrdinalIgnoreCase)
            .Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(TranslateWord)) switch
        {
            "" => name,
            var text => char.ToUpperInvariant(text[0]) + text[1..]
        };
    }

    private static string TranslateWord(string word) => word.ToLowerInvariant() switch
    {
        "get" => "получить", "list" => "показать", "create" => "создать", "add" => "добавить",
        "set" => "задать", "update" => "обновить", "modify" => "изменить", "delete" => "удалить",
        "remove" => "удалить", "rename" => "переименовать", "move" => "переместить", "copy" => "копировать",
        "view" => "вид", "element" => "элемент", "elements" => "элементы", "parameter" => "параметр",
        "parameters" => "параметры", "family" => "семейство", "type" => "тип", "current" => "текущий",
        "selected" => "выбранные", "info" => "сведения", "details" => "сведения", "model" => "модель",
        "project" => "проект", "color" => "окрасить", "show" => "показать", "message" => "сообщение",
        "point" => "точечный", "line" => "линейный", "surface" => "поверхностный", "based" => "элемент",
        "place" => "разместить", "on" => "на", "sheet" => "лист", "activate" => "активировать",
        "values" => "значения", "groups" => "группы", "group" => "группа", "operate" => "обработать",
        "room" => "помещение", "layout" => "компоновка", "grid" => "ось", "scale" => "масштаб",
        "members" => "состав", "available" => "доступные", "level" => "уровень", "crop" => "обрезка",
        "material" => "материал", "quantities" => "объёмы", "purge" => "очистить", "unused" => "неиспользуемое",
        "assign" => "назначить", "to" => "в", "workset" => "рабочий набор", "worksets" => "рабочие наборы",
        "assembly" => "сборка", "assemblies" => "сборки", "relationships" => "связи", "target" => "сеанс",
        "capture" => "сохранить", "image" => "изображение", "usage" => "использование", "patterns" => "статистика",
        "batch" => "пакетно", "execute" => "выполнить", "change" => "изменить", "filter" => "фильтр",
        _ => word
    };
}
