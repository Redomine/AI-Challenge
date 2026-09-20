using DesignerAssistant.Agent;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class TaskStateMachineTests
{
    private readonly TaskStateMachine _machine = new();

    [Theory]
    [InlineData(TaskState.Planning, TaskState.Execution)]
    [InlineData(TaskState.Execution, TaskState.Validation)]
    [InlineData(TaskState.Execution, TaskState.Planning)]
    [InlineData(TaskState.Validation, TaskState.Done)]
    [InlineData(TaskState.Validation, TaskState.Execution)]
    public void AllowsConfiguredTransitions(TaskState current, TaskState target)
    {
        var context = Context(current) with { PlanApproved = true };

        Assert.Equal(target, _machine.Transition(context, target).State);
    }

    [Theory]
    [InlineData(TaskState.Planning, TaskState.Validation)]
    [InlineData(TaskState.Planning, TaskState.Done)]
    [InlineData(TaskState.Execution, TaskState.Done)]
    [InlineData(TaskState.Validation, TaskState.Planning)]
    [InlineData(TaskState.Done, TaskState.Planning)]
    [InlineData(TaskState.Done, TaskState.Execution)]
    public void RejectsTransitionsOutsideConfiguredGraph(TaskState current, TaskState target)
    {
        Assert.Throws<InvalidOperationException>(() =>
            _machine.Transition(Context(current) with { PlanApproved = true }, target));
    }

    [Fact]
    public void PlanningCannotFinishWithoutUserApproval()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            _machine.Transition(Context(TaskState.Planning), TaskState.Execution));

        Assert.Contains("согласован", error.Message);
    }

    private static TaskContext Context(TaskState state) => new("Тестовая задача", state, "План");
}
