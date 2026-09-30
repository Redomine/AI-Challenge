namespace DesignerAssistant.Models;

public sealed record PromptUnderstanding(
    string OriginalQuery,
    string Goal,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> RequiredData,
    IReadOnlyList<string> SuccessCriteria,
    string Model,
    bool UsedFallback,
    string? FailureReason = null)
{
    public bool IsFallback => UsedFallback || FailureReason is not null;

    public string ToPromptBlock()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("[PROMPT_UNDERSTANDING]");
        builder.AppendLine($"Цель: {Goal.Trim()}");
        if (Constraints.Count > 0)
        {
            builder.AppendLine("Ограничения:");
            foreach (var constraint in Constraints) builder.AppendLine($"- {constraint.Trim()}");
        }
        if (RequiredData.Count > 0)
        {
            builder.AppendLine("Необходимые данные / уточнения:");
            foreach (var required in RequiredData) builder.AppendLine($"- {required.Trim()}");
        }
        if (SuccessCriteria.Count > 0)
        {
            builder.AppendLine("Критерии результата:");
            foreach (var criterion in SuccessCriteria) builder.AppendLine($"- {criterion.Trim()}");
        }
        builder.AppendLine($"Модель builder: {Model}");
        if (UsedFallback) builder.AppendLine("Режим: fallback (Ultra недоступна или вернула ошибку).");
        if (!string.IsNullOrWhiteSpace(FailureReason)) builder.AppendLine($"Причина fallback: {FailureReason}");
        builder.Append("[/PROMPT_UNDERSTANDING]");
        return builder.ToString();
    }
}