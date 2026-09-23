using DesignerAssistant.Models;

namespace DesignerAssistant.Agent;

public sealed class TaskWorkflow
{
    private const int MaxAutomaticStages = 8;
    private readonly ITaskStageRunner _runner;
    private readonly TaskStateMachine _machine;

    public TaskWorkflow(ITaskStageRunner runner, TaskStateMachine? machine = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _machine = machine ?? new TaskStateMachine();
    }

    public TaskContext? Context { get; private set; }
    public TaskPauseOptions PauseOptions { get; set; } = new();
    public bool AwaitingPlanApproval => Context?.State == TaskState.AwaitingPlanApproval;
    public bool PlanningInterrupted => Context?.State == TaskState.PlanningInterrupted;
    public bool AwaitingClarification => Context?.State == TaskState.Clarification;
    public bool ExecutionInterrupted => Context?.State == TaskState.ExecutionInterrupted;
    public bool IsPaused => Context?.State == TaskState.AwaitingContinuation;
    public bool ValidationFailed => Context?.State == TaskState.AwaitingValidationDecision;
    public AgentResponse? LastResponse { get; private set; }

    public async Task StartAsync(string query, CancellationToken cancellationToken = default)
    {
        if (Context is not null && !IsTerminal(Context.State))
            throw new InvalidOperationException("Сначала завершите текущую задачу.");
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Запрос не должен быть пустым.", nameof(query));

        Context = new TaskContext(query.Trim(), TaskState.Planning, Mode: TaskMode.Plan);
        await BuildPlanAsync(cancellationToken: cancellationToken);
    }

    public async Task StartDirectAsync(string query, CancellationToken cancellationToken = default)
    {
        if (Context is not null && !IsTerminal(Context.State))
            throw new InvalidOperationException("Сначала завершите текущую задачу.");
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Запрос не должен быть пустым.", nameof(query));

        Context = new TaskContext(query.Trim(), TaskState.Execution, Mode: TaskMode.Direct);
        await AdvanceAsync(cancellationToken);
    }

    public async Task ApprovePlanAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.AwaitingPlanApproval);
        Context = _machine.Transition(Context! with { PlanApproved = true }, TaskState.Execution);
        await AdvanceAsync(cancellationToken);
    }

    public async Task RefinePlanAsync(string feedback, CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.AwaitingPlanApproval);
        if (string.IsNullOrWhiteSpace(feedback)) throw new ArgumentException("Уточнение не должно быть пустым.", nameof(feedback));

        var trimmed = feedback.Trim();
        var revision = $"Текущий план:\n{Context!.Plan}\n\nУточнение пользователя:\n{trimmed}";
        Context = _machine.Transition(Context with { PlanApproved = false }, TaskState.Planning);
        await BuildPlanAsync(revision, trimmed, cancellationToken);
    }

    public async Task RetryPlanningAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.PlanningInterrupted);
        Context = _machine.Transition(Context! with { FailureReason = null, DiagnosticTraces = null }, TaskState.Planning);
        await BuildPlanAsync(cancellationToken: cancellationToken);
    }

    public async Task RefineInterruptedPlanningAsync(string feedback, CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.PlanningInterrupted);
        if (string.IsNullOrWhiteSpace(feedback)) throw new ArgumentException("Уточнение не должно быть пустым.", nameof(feedback));
        var trimmed = feedback.Trim();
        Context = _machine.Transition(Context! with { FailureReason = null, DiagnosticTraces = null }, TaskState.Planning);
        await BuildPlanAsync($"Уточнение пользователя после сбоя планирования:\n{trimmed}", trimmed, cancellationToken);
    }

    public async Task SubmitClarificationAsync(string answer, CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.Clarification);
        if (string.IsNullOrWhiteSpace(answer)) throw new ArgumentException("Ответ на уточнение не должен быть пустым.", nameof(answer));
        var request = Context!.Clarification ?? throw new InvalidOperationException("Отсутствует контекст уточнения.");
        var text = answer.Trim();

        if (request.ResumeState == TaskState.Planning)
        {
            Context = _machine.Transition(Context with { Clarification = null }, TaskState.Planning);
            await BuildPlanAsync($"Ответ пользователя на уточнение:\n{text}", text, cancellationToken);
            return;
        }

        Context = _machine.Transition(
            Context with
            {
                Clarification = null,
                ExecutionResult = $"{Context.ExecutionResult}\n\nОтвет пользователя на уточнение: {text}".Trim(),
                Checkpoint = (Context.Checkpoint ?? new ExecutionCheckpoint()) with { Attempt = (Context.Checkpoint?.Attempt ?? 0) + 1 }
            },
            TaskState.Execution);
        await AdvanceAsync(cancellationToken);
    }

    public async Task ContinueAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.AwaitingContinuation);
        var target = Context!.ResumeState ?? throw new InvalidOperationException("Не задан этап продолжения.");
        Context = _machine.Transition(Context with { ResumeState = null }, target);
        await AdvanceAsync(cancellationToken);
    }

    public async Task RetryValidationAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.AwaitingValidationDecision);
        Context = _machine.Transition(Context!, TaskState.Validation);
        await AdvanceAsync(cancellationToken);
    }

    public async Task RetryExecutionAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.AwaitingValidationDecision);
        if (!Context!.ExecutionRetrySafe)
            throw new InvalidOperationException("Повтор выполнения заблокирован: инструмент уже вернул результат до сбоя.");
        Context = _machine.Transition(Context!, TaskState.Execution);
        await AdvanceAsync(cancellationToken);
    }

    public async Task RetryInterruptedExecutionAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.ExecutionInterrupted);
        if (!Context!.ExecutionRetrySafe)
            throw new InvalidOperationException("Повтор выполнения заблокирован: до сбоя инструмент уже вернул результат. Перейдите к валидации или пересмотрите план.");
        Context = _machine.Transition(
            Context with
            {
                FailureReason = null,
                DiagnosticTraces = null,
                Checkpoint = (Context.Checkpoint ?? new ExecutionCheckpoint()) with { Attempt = (Context.Checkpoint?.Attempt ?? 0) + 1 }
            },
            TaskState.Execution);
        await AdvanceAsync(cancellationToken);
    }

    public async Task RefineInterruptedExecutionAsync(string feedback, CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.ExecutionInterrupted);
        if (!Context!.ExecutionRetrySafe)
            throw new InvalidOperationException("Уточнение с повтором заблокировано: до сбоя инструмент уже вернул результат. Перейдите к валидации или пересмотрите план.");
        if (string.IsNullOrWhiteSpace(feedback)) throw new ArgumentException("Уточнение не должно быть пустым.", nameof(feedback));
        Context = _machine.Transition(
            Context with
            {
                ExecutionResult = $"{Context.ExecutionResult}\n\nУточнение пользователя после сбоя: {feedback.Trim()}".Trim(),
                FailureReason = null,
                DiagnosticTraces = null,
                Checkpoint = (Context.Checkpoint ?? new ExecutionCheckpoint()) with { Attempt = (Context.Checkpoint?.Attempt ?? 0) + 1 }
            },
            TaskState.Execution);
        await AdvanceAsync(cancellationToken);
    }

    public async Task ValidateInterruptedExecutionAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.ExecutionInterrupted);
        if (Context!.ExecutionRetrySafe)
            throw new InvalidOperationException("До сбоя нет результата инструмента, который можно проверить.");
        Context = _machine.Transition(Context with { FailureReason = null }, TaskState.Validation);
        await AdvanceAsync(cancellationToken);
    }

    public async Task ReplanInterruptedExecutionAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.ExecutionInterrupted);
        var reason = Context!.FailureReason;
        Context = _machine.Transition(Context with { PlanApproved = false, FailureReason = null }, TaskState.Planning);
        await BuildPlanAsync($"Выполнение было прервано и пользователь запросил новый план:\n{reason}", cancellationToken: cancellationToken);
    }

    public async Task ReplanAsync(CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.AwaitingValidationDecision);
        Context = _machine.Transition(Context! with { PlanApproved = false }, TaskState.Planning);
        await BuildPlanAsync($"Валидация потребовала пересмотра плана:\n{Context.ValidationResult}", cancellationToken: cancellationToken);
    }

    public void FinishWithoutValidation()
    {
        EnsureState(TaskState.AwaitingValidationDecision);
        const string report = "[SKIPPED] Задача завершена пользователем без успешной валидации.";
        Context = _machine.Transition(Context! with { ValidationResult = report }, TaskState.Done);
        LastResponse = CreateWorkflowResponse(report, "task_state_validation_skipped");
    }

    public void Cancel()
    {
        if (Context is null || IsTerminal(Context.State)) return;
        Context = _machine.Transition(Context, TaskState.Cancelled);
        LastResponse = CreateWorkflowResponse("Задача отменена", "task_state_cancelled");
    }

    private async Task BuildPlanAsync(string? revision = null, string? storedUserMessage = null, CancellationToken cancellationToken = default)
    {
        EnsureState(TaskState.Planning);
        try
        {
            LastResponse = await _runner.PlanTaskAsync(Context!.Query, revision, cancellationToken, storedUserMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var traces = exception is AgentStageException stageException
                ? stageException.Traces
                : Array.Empty<string>();
            var report = $"Планирование прервано из-за ответа модели. Запрос можно повторить, уточнить или отменить.{Environment.NewLine}{exception.Message}";
            Context = _machine.Transition(
                Context! with { FailureReason = exception.Message, DiagnosticTraces = traces },
                TaskState.PlanningInterrupted);
            LastResponse = CreateWorkflowResponse(report, "task_state_planning_interrupted", traces);
            return;
        }
        if (LastResponse.ModelResponse.Content.TrimStart().StartsWith("[CLARIFY]", StringComparison.OrdinalIgnoreCase))
        {
            var question = LastResponse.ModelResponse.Content.TrimStart()[9..].Trim();
            Context = _machine.Transition(
                Context with { Clarification = new ClarificationRequest(question, TaskState.Planning, "Planning запросил недостающие данные.") },
                TaskState.Clarification);
            return;
        }
        Context = _machine.Transition(
            Context with { Plan = LastResponse.ModelResponse.Content, StructuredPlan = _runner.LastStructuredPlan, PlanApproved = false, Clarification = null },
            TaskState.AwaitingPlanApproval);
    }

    private async Task AdvanceAsync(CancellationToken cancellationToken)
    {
        for (var count = 0; count < MaxAutomaticStages; count++)
        {
            switch (Context!.State)
            {
                case TaskState.Execution:
                    await ExecuteAsync(cancellationToken);
                    if (Context.State == TaskState.Planning) continue;
                    if (Context.State != TaskState.Validation) return;
                    break;
                case TaskState.Validation:
                    await ValidateAsync(cancellationToken);
                    if (Context.State is TaskState.Execution or TaskState.Planning) continue;
                    return;
                case TaskState.Planning:
                    await BuildPlanAsync($"Предыдущее выполнение потребовало пересмотра плана:\n{Context.ExecutionResult}", cancellationToken: cancellationToken);
                    return;
                case TaskState.Done:
                    return;
                default:
                    return;
            }
        }

        Context = Context! with
        {
            State = TaskState.Failed,
            FailureReason = $"Превышен лимит автоматических переходов ({MaxAutomaticStages})."
        };
        LastResponse = CreateWorkflowResponse(Context.FailureReason, "task_state_transition_limit");
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        try
        {
            LastResponse = await _runner.ExecuteTaskAsync(Context!, cancellationToken);
            var outcome = ParseExecutionOutcome(LastResponse.ModelResponse.Content);
            Context = Context! with
            {
                ExecutionResult = LastResponse.ModelResponse.Content,
                ToolResults = LastResponse.ModelResponse.ToolResults
            };
            switch (outcome)
            {
                case ExecutionNeedsClarification clarification:
                    Context = _machine.Transition(
                        Context with { Clarification = new ClarificationRequest(clarification.Question, TaskState.Execution, "Execution запросил недостающие данные.") },
                        TaskState.Clarification);
                    return;
                case ExecutionNeedsReplan:
                    Context = _machine.Transition(Context with { PlanApproved = false }, TaskState.Planning);
                    return;
                case ExecutionCompleted:
                    if (PauseOptions.AfterExecution)
                    {
                        Context = _machine.Transition(Context with { ResumeState = TaskState.Validation }, TaskState.AwaitingContinuation);
                        return;
                    }
                    Context = _machine.Transition(Context, TaskState.Validation);
                    return;
            }
        }
        catch (ToolCallLimitExceededException exception)
        {
            var report = $"Выполнение остановлено: {exception.Message} План нужно сократить или разбить на этапы.";
            Context = _machine.Transition(Context! with { ExecutionResult = report, PlanApproved = false }, TaskState.Planning);
            LastResponse = CreateWorkflowResponse(report);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var traces = exception is AgentStageException stageException
                ? stageException.Traces
                : Array.Empty<string>();
            var toolCompleted = traces.Any(trace => trace.StartsWith("Tool result:", StringComparison.Ordinal));
            var report = toolCompleted
                ? $"Выполнение прервано после получения результата инструмента. Автоматический повтор заблокирован, чтобы не повторить изменение модели.{Environment.NewLine}{exception.Message}"
                : $"Выполнение прервано до подтверждённого результата инструмента. Задачу можно повторить, уточнить или перепланировать.{Environment.NewLine}{exception.Message}";
            Context = _machine.Transition(
                Context! with
                {
                    ExecutionResult = report,
                    FailureReason = exception.Message,
                    DiagnosticTraces = traces,
                    ExecutionRetrySafe = !toolCompleted
                },
                TaskState.ExecutionInterrupted);
            LastResponse = CreateWorkflowResponse(report, "task_state_execution_interrupted", traces);
        }
    }

    private async Task ValidateAsync(CancellationToken cancellationToken)
    {
        try
        {
            LastResponse = await _runner.ValidateTaskAsync(Context!, cancellationToken);
            Context = Context! with { ValidationResult = LastResponse.ModelResponse.Content };
            var outcome = ParseValidationOutcome(LastResponse.ModelResponse.Content);
            if (outcome == ValidationOutcome.Inconclusive)
            {
                Context = Context with
                {
                    ValidationResult = $"Проверка вернула неоднозначный результат. Повторное изменение модели не запущено.{Environment.NewLine}{LastResponse.ModelResponse.Content}"
                };
            }
            var target = outcome switch
            {
                ValidationOutcome.Passed => TaskState.Done,
                ValidationOutcome.CorrectionWithinPlan when Context.ExecutionRetrySafe => TaskState.Execution,
                ValidationOutcome.CorrectionWithinPlan => TaskState.AwaitingValidationDecision,
                ValidationOutcome.PlanMustChange => TaskState.Planning,
                _ => TaskState.AwaitingValidationDecision
            };
            if (PauseOptions.AfterValidation)
            {
                Context = _machine.Transition(Context with { ResumeState = target }, TaskState.AwaitingContinuation);
                return;
            }
            Context = _machine.Transition(Context with { PlanApproved = target != TaskState.Planning && Context.PlanApproved }, target);
        }
        catch (Exception exception)
        {
            var report = $"Проверка не завершена. Повторное изменение модели не запущено.{Environment.NewLine}{exception.Message}";
            Context = _machine.Transition(Context! with { ValidationResult = report }, TaskState.AwaitingValidationDecision);
            LastResponse = CreateWorkflowResponse(report, "task_state_validation_error");
        }
    }

    private static ExecutionStageOutcome ParseExecutionOutcome(string content)
    {
        var text = content.TrimStart();
        if (text.StartsWith("[CLARIFY]", StringComparison.OrdinalIgnoreCase))
            return new ExecutionNeedsClarification(text[9..].Trim());
        if (text.StartsWith("[REPLAN]", StringComparison.OrdinalIgnoreCase))
            return new ExecutionNeedsReplan(text[8..].Trim());
        return new ExecutionCompleted(content);
    }

    private static ValidationOutcome ParseValidationOutcome(string content)
    {
        var passed = content.Contains("[PASS]", StringComparison.OrdinalIgnoreCase);
        var retry = content.Contains("[RETRY_EXECUTION]", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("[FAIL]", StringComparison.OrdinalIgnoreCase);
        var replan = content.Contains("[REPLAN]", StringComparison.OrdinalIgnoreCase);
        var count = (passed ? 1 : 0) + (retry ? 1 : 0) + (replan ? 1 : 0);
        if (count != 1) return ValidationOutcome.Inconclusive;
        if (passed) return ValidationOutcome.Passed;
        return replan ? ValidationOutcome.PlanMustChange : ValidationOutcome.CorrectionWithinPlan;
    }

    private static AgentResponse CreateWorkflowResponse(
        string content,
        string finishReason = "task_state_replan",
        IReadOnlyList<string>? traces = null) => new(
        new LlmResponse(content, finishReason, new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)), 0, true, 0, traces);

    private void EnsureState(TaskState state)
    {
        if (Context?.State != state) throw new InvalidOperationException($"Операция доступна только в состоянии {state}.");
    }

    private static bool IsTerminal(TaskState state) => state is TaskState.Done or TaskState.Cancelled or TaskState.Failed;
}
