using DesignerAssistant.Agent;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class TaskStateMachineTests
{
    private readonly TaskStateMachine _machine = new();

    [Theory]
    [InlineData(TaskState.Planning, TaskState.AwaitingPlanApproval)]
    [InlineData(TaskState.AwaitingPlanApproval, TaskState.Planning)]
    [InlineData(TaskState.AwaitingPlanApproval, TaskState.Execution)]
    [InlineData(TaskState.Execution, TaskState.Clarification)]
    [InlineData(TaskState.Clarification, TaskState.Execution)]
    [InlineData(TaskState.Execution, TaskState.Validation)]
    [InlineData(TaskState.Validation, TaskState.Execution)]
    [InlineData(TaskState.Validation, TaskState.Planning)]
    [InlineData(TaskState.Validation, TaskState.AwaitingValidationDecision)]
    [InlineData(TaskState.AwaitingValidationDecision, TaskState.Done)]
    public void AllowsConfiguredTransitions(TaskState current, TaskState target)
    {
        var context = Context(current) with { PlanApproved = true };
        Assert.Equal(target, _machine.Transition(context, target).State);
    }

    [Theory]
    [InlineData(TaskState.Planning, TaskState.Execution)]
    [InlineData(TaskState.AwaitingPlanApproval, TaskState.Validation)]
    [InlineData(TaskState.Execution, TaskState.Done)]
    [InlineData(TaskState.Done, TaskState.Planning)]
    [InlineData(TaskState.Cancelled, TaskState.Execution)]
    [InlineData(TaskState.Failed, TaskState.Planning)]
    public void RejectsTransitionsOutsideConfiguredGraph(TaskState current, TaskState target)
    {
        Assert.Throws<InvalidOperationException>(() => _machine.Transition(Context(current) with { PlanApproved = true }, target));
    }

    [Fact]
    public void ApprovalStateCannotStartExecutionWithoutApproval()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            _machine.Transition(Context(TaskState.AwaitingPlanApproval), TaskState.Execution));
        Assert.Contains("согласован", error.Message);
    }

    private static TaskContext Context(TaskState state) => new("Тестовая задача", state, "План");
}
