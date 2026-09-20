namespace DesignerAssistant.Models;

public sealed record ChatMessage(string Role, string Content, TaskState? Stage = null);
