namespace DesignerAssistant.Models;

public enum TaskState
{
    Planning,
    Execution,
    Validation,
    Done
}

public sealed record TaskContext(
    string Query,
    TaskState State,
    string Plan = "",
    string ExecutionResult = "",
    string ValidationResult = "",
    bool PlanApproved = false);

public sealed record TaskPauseOptions(
    bool AfterPlanning = true,
    bool AfterExecution = false,
    bool AfterValidation = false);
