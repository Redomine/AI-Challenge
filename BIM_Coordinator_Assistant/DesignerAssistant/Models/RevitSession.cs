namespace DesignerAssistant.Models;

public sealed record RevitSession(
    int ProcessId,
    int Year,
    string ProjectName,
    string DisplayName,
    bool IsRoutable);
