using System.Text;
using System.Text.Json;

namespace DesignerAssistant.Models;

public sealed record TaskPlan(string Summary, IReadOnlyList<TaskPlanStep> Steps, string? Clarification = null)
{
    public string ToDisplayText()
    {
        if (!string.IsNullOrWhiteSpace(Clarification)) return $"[CLARIFY] {Clarification.Trim()}";
        var text = new StringBuilder(Summary.Trim());
        for (var index = 0; index < Steps.Count; index++)
        {
            var step = Steps[index];
            text.Append($"\n\n{index + 1}. {step.Action.Trim()}");
            if (!string.IsNullOrWhiteSpace(step.Tool)) text.Append($"\n   Инструмент: {step.Tool}");
            if (step.Arguments.Count > 0)
                text.Append($"\n   Аргументы: {string.Join(", ", step.Arguments.Select(item => $"{item.Key}={item.Value.GetRawText()}"))}");
            if (step.ArgumentSources.Count > 0)
                text.Append($"\n   Источники: {string.Join(", ", step.ArgumentSources.Select(item => $"{item.Key} <- {item.Value}"))}");
        }
        return text.ToString();
    }
}

public sealed record TaskPlanStep(
    string Action,
    string? Tool,
    IReadOnlyDictionary<string, JsonElement> Arguments,
    IReadOnlyDictionary<string, string> ArgumentSources);
