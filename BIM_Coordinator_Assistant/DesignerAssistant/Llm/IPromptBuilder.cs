using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public interface IPromptBuilder
{
    string ModelName { get; }

    bool IsAvailable { get; }

    string? AvailabilityError { get; }

    Task<PromptUnderstanding> BuildAsync(string query, CancellationToken cancellationToken = default, string? revisionContext = null);
}