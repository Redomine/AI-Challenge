using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public sealed class TaskWorkflow
{
    private const int MaxAutomaticStages = 8;
    private readonly ITaskStageRunner _runner;
    private readonly TaskStateMachine _machine;
    private TaskState? _pendingTransition;

    public TaskWorkflow(ITaskStageRunner runner, TaskStateMachine? machine = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _machine = machine ?? new TaskStateMachine();
    }

    public TaskContext? Context { get; private set; }
    public TaskPauseOptions PauseOptions { get; set; } = new();
    public bool AwaitingPlanApproval => Context?.State == TaskState.Planning && _pendingTransition == TaskState.Execution;
    public bool IsPaused { get; private set; }
    public AgentResponse? LastResponse { get; private set; }

    public async Task StartAsync(string query, CancellationToken cancellationToken = default)
    {
        if (Context is not null && Context.State != TaskState.Done)
            throw new InvalidOperationException("Сначала завершите текущую задачу.");
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Запрос не должен быть пустым.", nameof(query));

        Context = new TaskContext(query.Trim(), TaskState.Planning);
        LastResponse = await _runner.PlanTaskAsync(Context.Query, cancellationToken: cancellationToken);
        Context = Context with { Plan = LastResponse.ModelResponse.Content };
        _pendingTransition = TaskState.Execution;
        IsPaused = true;
    }

    public async Task ApprovePlanAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (!AwaitingPlanApproval) throw new InvalidOperationException("Сейчас нет плана, ожидающего согласования.");
        Context = Context! with { PlanApproved = true };
        IsPaused = false;
        await AdvanceAsync(cancellationToken);
    }

    public async Task ContinueAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (AwaitingPlanApproval) throw new InvalidOperationException("Сначала согласуйте план.");
        if (!IsPaused) throw new InvalidOperationException("Задача не находится на паузе.");
        IsPaused = false;
        await AdvanceAsync(cancellationToken);
    }

    public void Cancel()
    {
        Context = null;
        LastResponse = null;
        _pendingTransition = null;
        IsPaused = false;
    }

    private async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        for (var count = 0; count < MaxAutomaticStages && _pendingTransition is not null; count++)
        {
            var target = _pendingTransition.Value;
            Context = _machine.Transition(Context!, target);
            _pendingTransition = null;

            switch (Context.State)
            {
                case TaskState.Planning:
                    LastResponse = await _runner.PlanTaskAsync(Context.Query, Context.ExecutionResult, cancellationToken);
                    Context = Context with { Plan = LastResponse.ModelResponse.Content, PlanApproved = false };
                    _pendingTransition = TaskState.Execution;
                    IsPaused = true;
                    return;

                case TaskState.Execution:
                    try
                    {
                        LastResponse = await _runner.ExecuteTaskAsync(Context, cancellationToken);
                        Context = Context with { ExecutionResult = LastResponse.ModelResponse.Content };
                        _pendingTransition = StartsWith(LastResponse, "[REPLAN]") ? TaskState.Planning : TaskState.Validation;
                    }
                    catch (ToolCallLimitExceededException exception)
                    {
                        var report = $"[REPLAN] Выполнение остановлено: {exception.Message} " +
                                     "План нужно сократить или разбить на этапы так, чтобы один проход не превышал лимит.";
                        Context = Context with { ExecutionResult = report };
                        LastResponse = CreateWorkflowResponse(report);
                        _pendingTransition = TaskState.Planning;
                    }
                    if (PauseOptions.AfterExecution) { IsPaused = true; return; }
                    break;

                case TaskState.Validation:
                    try
                    {
                        LastResponse = await _runner.ValidateTaskAsync(Context, cancellationToken);
                    }
                    catch (Exception exception)
                    {
                        var report = $"Проверка не завершена: {exception.Message} Повторное изменение модели не запущено.";
                        Context = Context with { ValidationResult = report };
                        LastResponse = CreateWorkflowResponse(report, "task_state_validation_error");
                        _pendingTransition = TaskState.Done;
                        IsPaused = true;
                        return;
                    }
                    Context = Context with { ValidationResult = LastResponse.ModelResponse.Content };
                    try
                    {
                        _pendingTransition = GetValidationTarget(LastResponse);
                    }
                    catch (InvalidOperationException)
                    {
                        _pendingTransition = TaskState.Done;
                        IsPaused = true;
                        throw;
                    }
                    if (PauseOptions.AfterValidation) { IsPaused = true; return; }
                    break;

                case TaskState.Done:
                    IsPaused = false;
                    return;
            }
        }

        if (_pendingTransition is not null) IsPaused = true;
    }

    private static bool StartsWith(AgentResponse response, string marker) =>
        response.ModelResponse.Content.TrimStart().StartsWith(marker, StringComparison.OrdinalIgnoreCase);

    private static TaskState GetValidationTarget(AgentResponse response)
    {
        var content = response.ModelResponse.Content;
        var passed = content.Contains("[PASS]", StringComparison.OrdinalIgnoreCase);
        var failed = content.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase);
        if (passed && !failed) return TaskState.Done;
        if (failed && !passed) return TaskState.Execution;
        throw new InvalidOperationException(
            "Валидация должна вернуть ровно один маркер [PASS] или [FAIL]. Повторное изменение модели не запущено; задачу можно завершить кнопкой «Продолжить» после ручной проверки.");
    }

    private static AgentResponse CreateWorkflowResponse(string content, string finishReason = "task_state_replan") => new(
        new LlmResponse(content, finishReason, new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)),
        0,
        true,
        0);

    private void EnsureActive()
    {
        if (Context is null || Context.State == TaskState.Done)
            throw new InvalidOperationException("Нет активной задачи.");
    }
}
