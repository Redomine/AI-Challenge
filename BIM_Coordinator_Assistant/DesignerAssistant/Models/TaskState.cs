namespace DesignerAssistant.Models;

public enum TaskState
{
    Planning,
    PlanningInterrupted,
    AwaitingPlanApproval,
    Execution,
    ExecutionInterrupted,
    Clarification,
    Validation,
    AwaitingValidationDecision,
    AwaitingContinuation,
    Done,
    Cancelled,
    Failed
}

public enum TaskMode
{
    Direct,
    Plan
}

public sealed record ExecutionCheckpoint(
    int Attempt = 0,
    IReadOnlySet<string>? CompletedStepIds = null,
    IReadOnlyDictionary<string, string>? StepResults = null)
{
    public IReadOnlySet<string> CompletedSteps { get; init; } = CompletedStepIds ?? new HashSet<string>();
    public IReadOnlyDictionary<string, string> Results { get; init; } = StepResults ?? new Dictionary<string, string>();
}

public sealed record ClarificationRequest(
    string Question,
    TaskState ResumeState,
    string Reason);

public sealed record TaskContext(
    string Query,
    TaskState State,
    string Plan = "",
    string ExecutionResult = "",
    string ValidationResult = "",
    bool PlanApproved = false,
    TaskPlan? StructuredPlan = null,
    ClarificationRequest? Clarification = null,
    TaskState? ResumeState = null,
    ExecutionCheckpoint? Checkpoint = null,
    string? FailureReason = null,
    IReadOnlyList<string>? DiagnosticTraces = null,
    bool ExecutionRetrySafe = true,
    IReadOnlyList<ToolResultEnvelope>? ToolResults = null,
    TaskMode Mode = TaskMode.Plan);

public sealed record TaskPauseOptions(
    bool AfterPlanning = true,
    bool AfterExecution = false,
    bool AfterValidation = false);

public abstract record ExecutionStageOutcome(string Report);
public sealed record ExecutionCompleted(string Result) : ExecutionStageOutcome(Result);
public sealed record ExecutionNeedsClarification(string Question) : ExecutionStageOutcome(Question);
public sealed record ExecutionNeedsReplan(string Reason) : ExecutionStageOutcome(Reason);

public enum ValidationOutcome
{
    Passed,
    CorrectionWithinPlan,
    PlanMustChange,
    Inconclusive
}
