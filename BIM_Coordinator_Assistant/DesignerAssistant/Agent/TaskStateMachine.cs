using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public sealed class TaskStateMachine
{
    private static readonly IReadOnlyDictionary<TaskState, IReadOnlySet<TaskState>> AllowedTransitions =
        new Dictionary<TaskState, IReadOnlySet<TaskState>>
        {
            [TaskState.Planning] = new HashSet<TaskState> { TaskState.Execution },
            [TaskState.Execution] = new HashSet<TaskState> { TaskState.Validation, TaskState.Planning },
            [TaskState.Validation] = new HashSet<TaskState> { TaskState.Done, TaskState.Execution },
            [TaskState.Done] = new HashSet<TaskState>()
        };

    public TaskContext Transition(TaskContext context, TaskState target)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!AllowedTransitions[context.State].Contains(target))
        {
            throw new InvalidOperationException(
                $"Переход {context.State} -> {target} запрещён.");
        }

        if (context.State == TaskState.Planning && target == TaskState.Execution && !context.PlanApproved)
        {
            throw new InvalidOperationException("План должен быть согласован с пользователем.");
        }

        return context with { State = target };
    }

    public bool CanTransition(TaskState current, TaskState target) =>
        AllowedTransitions[current].Contains(target);
}
