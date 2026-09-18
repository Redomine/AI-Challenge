namespace DesignerAssistant.Models;

public sealed record LlmResponse(
    string Content,
    string FinishReason,
    TokenUsage Usage);
