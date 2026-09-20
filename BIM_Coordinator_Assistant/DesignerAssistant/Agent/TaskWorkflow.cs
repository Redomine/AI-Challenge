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
    public bool ValidationFailed { get; private set; }
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
        if (ValidationFailed) throw new InvalidOperationException("Выберите: повторить валидацию или завершить задачу без неё.");
        if (!IsPaused) throw new InvalidOperationException("Задача не находится на паузе.");
        IsPaused = false;
        await AdvanceAsync(cancellationToken);
    }

    public async Task RetryValidationAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        if (Context!.State != TaskState.Validation || !ValidationFailed)
            throw new InvalidOperationException("Нет неудачной валидации для повторного запуска.");

        ValidationFailed = false;
        IsPaused = false;
        if (!await ValidateCurrentAsync(cancellationToken)) return;
        if (PauseOptions.AfterValidation) { IsPaused = true; return; }
        await AdvanceAsync(cancellationToken);
    }

    public void FinishWithoutValidation()
    {
        EnsureActive();
        if (Context!.State != TaskState.Validation || !ValidationFailed)
            throw new InvalidOperationException("Завершение без валидации доступно только после её сбоя.");

        const string report = "[SKIPPED] Задача завершена пользователем без успешной валидации.";
        Context = _machine.Transition(Context with { ValidationResult = report }, TaskState.Done);
        LastResponse = CreateWorkflowResponse(report, "task_state_validation_skipped");
        _pendingTransition = null;
        ValidationFailed = false;
        IsPaused = false;
    }

    public void Cancel()
    {
        Context = null;
        LastResponse = null;
        _pendingTransition = null;
        ValidationFailed = false;
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
                    if (!await ValidateCurrentAsync(cancellationToken)) return;
                    if (PauseOptions.AfterValidation) { IsPaused = true; return; }
                    break;

                case TaskState.Done:
                    IsPaused = false;
                    return;
            }
        }

        if (_pendingTransition is not null) IsPaused = true;
    }

    private async Task<bool> ValidateCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            LastResponse = await _runner.ValidateTaskAsync(Context!, cancellationToken);
            Context = Context! with { ValidationResult = LastResponse.ModelResponse.Content };
            _pendingTransition = GetValidationTarget(LastResponse);
            ValidationFailed = false;
            return true;
        }
        catch (Exception exception)
        {
            var report = $"Проверка не завершена. Повторное изменение модели не запущено.{Environment.NewLine}{exception.Message}";
            Context = Context! with { ValidationResult = report };
            LastResponse = CreateWorkflowResponse(report, "task_state_validation_error");
            _pendingTransition = null;
            ValidationFailed = true;
            IsPaused = true;
            return false;
        }
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
