using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public sealed class TaskStateMachine
{
    private static readonly IReadOnlyDictionary<TaskState, IReadOnlySet<TaskState>> AllowedTransitions =
        new Dictionary<TaskState, IReadOnlySet<TaskState>>
        {
            [TaskState.Planning] = Set(TaskState.AwaitingPlanApproval, TaskState.Clarification, TaskState.Failed, TaskState.Cancelled),
            [TaskState.AwaitingPlanApproval] = Set(TaskState.Planning, TaskState.Execution, TaskState.Cancelled),
            [TaskState.Execution] = Set(TaskState.Validation, TaskState.Planning, TaskState.Clarification, TaskState.AwaitingContinuation, TaskState.Failed, TaskState.Cancelled),
            [TaskState.Clarification] = Set(TaskState.Planning, TaskState.Execution, TaskState.Cancelled),
            [TaskState.Validation] = Set(TaskState.Done, TaskState.Execution, TaskState.Planning, TaskState.AwaitingValidationDecision, TaskState.AwaitingContinuation, TaskState.Failed, TaskState.Cancelled),
            [TaskState.AwaitingValidationDecision] = Set(TaskState.Validation, TaskState.Execution, TaskState.Planning, TaskState.Done, TaskState.Cancelled),
            [TaskState.AwaitingContinuation] = Set(TaskState.Execution, TaskState.Validation, TaskState.Planning, TaskState.Done, TaskState.Cancelled),
            [TaskState.Done] = Set(),
            [TaskState.Cancelled] = Set(),
            [TaskState.Failed] = Set()
        };

    public TaskContext Transition(TaskContext context, TaskState target)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!AllowedTransitions[context.State].Contains(target))
            throw new InvalidOperationException($"Переход {context.State} -> {target} запрещён.");

        if (context.State == TaskState.AwaitingPlanApproval && target == TaskState.Execution && !context.PlanApproved)
            throw new InvalidOperationException("План должен быть согласован с пользователем.");

        return context with { State = target };
    }

    public bool CanTransition(TaskState current, TaskState target) => AllowedTransitions[current].Contains(target);

    private static IReadOnlySet<TaskState> Set(params TaskState[] states) => new HashSet<TaskState>(states);
}
