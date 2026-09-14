namespace DesignerAssistant.Models;

public sealed record ContextState(
    ContextStrategy Strategy,
    Dictionary<string, string> Facts,
    string ActiveBranch,
    List<ChatMessage> CheckpointMessages,
    Dictionary<string, List<ChatMessage>> Branches)
{
    public static ContextState CreateDefault() => new(
        ContextStrategy.SlidingWindow,
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        "main",
        [],
        new Dictionary<string, List<ChatMessage>>(StringComparer.OrdinalIgnoreCase));
}
