using DesignerAssistant.Models;

namespace DesignerAssistant.Memory;

public sealed record MemoryCapture(MemoryLayer? Layer, string Value, string MatchedPhrase)
{
    public bool NeedsLayerChoice => Layer is null;
}

public static class MemoryPhraseParser
{
    public static readonly IReadOnlyList<string> LongTermPhrases =
    [
        "запомни на будущее", "сохрани на будущее", "запомни долговременно",
        "сохрани долговременно", "запомни навсегда", "сохрани навсегда",
        "сохрани в долговременную память", "запиши в долговременную память",
        "запомни в долгосрочную память", "сохрани в долгосрочную память",
        "всегда помни", "помни в будущих задачах", "учитывай в дальнейшем",
        "на будущее запомни", "для всех будущих задач",
        "это постоянное правило", "сохрани как постоянное правило"
    ];

    public static readonly IReadOnlyList<string> WorkingPhrases =
    [
        "запомни для этой задачи", "сохрани для этой задачи",
        "запомни для текущей задачи", "сохрани для текущей задачи",
        "запомни в рабочую память", "сохрани в рабочую память",
        "запиши в рабочую память", "на время этой задачи",
        "для текущей задачи запомни", "в рамках этой задачи запомни",
        "для этого проекта запомни", "для текущего проекта запомни",
        "запомни для проекта", "сохрани для проекта",
        "для этой задачи учти", "в текущей задаче учти",
        "пока работаем над проектом запомни", "до завершения задачи запомни",
        "запомни до конца задачи", "запомни на время работы"
    ];

    private static readonly string[] AmbiguousPhrases =
    [
        "запомни", "сохрани", "запиши", "учти"
    ];

    public static MemoryCapture? Parse(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.StartsWith("пожалуйста", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed["пожалуйста".Length..].TrimStart(' ', ',', ':');
        }
        var match = Match(trimmed, LongTermPhrases);
        if (match is not null) return new MemoryCapture(MemoryLayer.LongTerm, ExtractValue(trimmed, match), match);
        match = Match(trimmed, WorkingPhrases);
        if (match is not null) return new MemoryCapture(MemoryLayer.Working, ExtractValue(trimmed, match), match);
        match = Match(trimmed, AmbiguousPhrases);
        return match is null ? null : new MemoryCapture(null, ExtractValue(trimmed, match), match);
    }

    private static string? Match(string input, IEnumerable<string> phrases) =>
        phrases.OrderByDescending(phrase => phrase.Length)
            .FirstOrDefault(phrase => input.StartsWith(phrase, StringComparison.OrdinalIgnoreCase));

    private static string ExtractValue(string input, string phrase)
    {
        var value = input[phrase.Length..].TrimStart(' ', ',', ':', ';', '-', '—');
        if (value.StartsWith("что ", StringComparison.OrdinalIgnoreCase)) value = value[4..].Trim();
        return value.Trim().TrimEnd('.');
    }
}
