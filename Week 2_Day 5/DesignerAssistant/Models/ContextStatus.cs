namespace DesignerAssistant.Models;

public sealed record ContextStatus(
    ContextStrategy Strategy,
    int FactsCount,
    string ActiveBranch,
    IReadOnlyCollection<string> BranchNames,
    int ActiveMessageCount);
