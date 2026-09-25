using System.Text.Json;
using DesignerAssistant.Agent;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class TaskWorkflowTests
{
    [Fact]
    public async Task ToolErrorAfterOpeningModelStopsWithoutRepeatingExecution()
    {
        var runner = new ToolErrorRunner();
        var workflow = new TaskWorkflow(runner);

        await workflow.StartDirectAsync("Открой модель и загрузи семейства");

        Assert.Equal(TaskState.ExecutionInterrupted, workflow.Context?.State);
        Assert.Equal(1, runner.ExecutionCount);
        Assert.Equal(0, runner.ValidationCount);
        Assert.False(workflow.Context?.ExecutionRetrySafe);
        Assert.Contains("Supply exactly one of path or folderPath", workflow.Context?.FailureReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RetryInterruptedExecutionAsync());
    }

    [Fact]
    public async Task DirectModeUsesExecutionAndValidationWithoutPlanning()
    {
        var runner = new FakeRunner(["[EXECUTED] Готово"], ["[PASS] Проверено"]);
        var workflow = new TaskWorkflow(runner);

        await workflow.StartDirectAsync("Покажи выбранные элементы");

        Assert.Equal(TaskMode.Direct, workflow.Context?.Mode);
        Assert.Equal(TaskState.Done, workflow.Context?.State);
        Assert.Equal(0, runner.PlanningCount);
        Assert.Equal(1, runner.ExecutionCount);
        Assert.Equal(1, runner.ValidationCount);
    }

    [Fact]
    public async Task PlanModeStillStartsWithPlanningAndApproval()
    {
        var runner = new FakeRunner(["[EXECUTED] Готово"], ["[PASS] Проверено"]);
        var workflow = new TaskWorkflow(runner);

        await workflow.StartAsync("Сложная задача");

        Assert.Equal(TaskMode.Plan, workflow.Context?.Mode);
        Assert.Equal(TaskState.AwaitingPlanApproval, workflow.Context?.State);
        Assert.Equal(1, runner.PlanningCount);
        Assert.Equal(0, runner.ExecutionCount);
    }

    [Fact]
    public async Task RequiresApprovalBeforeExecutionAndCompletesValidTask()
    {
        var runner = new FakeRunner(["[EXECUTED] Готово"], ["[PASS] Проверено"]);
        var workflow = new TaskWorkflow(runner);

        await workflow.StartAsync("Выполни задачу");

        Assert.Equal(TaskState.AwaitingPlanApproval, workflow.Context?.State);
        Assert.True(workflow.AwaitingPlanApproval);
        Assert.Equal(0, runner.ExecutionCount);

        await workflow.ApprovePlanAsync();

        Assert.Equal(TaskState.Done, workflow.Context?.State);
        Assert.Equal(1, runner.ExecutionCount);
        Assert.Equal(1, runner.ValidationCount);
    }

    [Fact]
    public async Task PausesAfterExecutionUntilUserContinues()
    {
        var runner = new FakeRunner(["[EXECUTED] Готово"], ["[PASS] Проверено"]);
        var workflow = new TaskWorkflow(runner)
        {
            PauseOptions = new TaskPauseOptions(true, AfterExecution: true, AfterValidation: false)
        };
        await workflow.StartAsync("Выполни задачу");

        await workflow.ApprovePlanAsync();

        Assert.Equal(TaskState.AwaitingContinuation, workflow.Context?.State);
        Assert.True(workflow.IsPaused);
        Assert.Equal(TaskState.Validation, workflow.Context?.ResumeState);
        Assert.Equal(0, runner.ValidationCount);

        await workflow.ContinueAsync();
        Assert.Equal(TaskState.Done, workflow.Context?.State);
    }

    [Fact]
    public async Task FailedValidationReturnsToExecution()
    {
        var runner = new FakeRunner(
            ["[EXECUTED] Первая попытка", "[EXECUTED] Исправлено"],
            ["[FAIL] Нужна правка", "[PASS] Проверено"]);
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Выполни задачу");

        await workflow.ApprovePlanAsync();

        Assert.Equal(TaskState.Done, workflow.Context?.State);
        Assert.Equal(2, runner.ExecutionCount);
        Assert.Equal(2, runner.ValidationCount);
    }

    [Fact]
    public async Task PassMarkerAtEndDoesNotRepeatExecution()
    {
        var runner = new FakeRunner(
            ["[EXECUTED] Комментарий записан"],
            ["Комментарий проверен и заполнен. [PASS]"]);
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Заполни комментарий");

        await workflow.ApprovePlanAsync();

        Assert.Equal(TaskState.Done, workflow.Context?.State);
        Assert.Equal(1, runner.ExecutionCount);
        Assert.Equal(1, runner.ValidationCount);
    }

    [Theory]
    [InlineData("Проверка завершена без служебного маркера")]
    [InlineData("[PASS] Но результат одновременно [FAIL]")]
    public async Task AmbiguousValidationNeverRepeatsMutation(string validation)
    {
        var runner = new FakeRunner(["[EXECUTED] Готово"], [validation]);
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Измени модель");

        await workflow.ApprovePlanAsync();

        Assert.True(workflow.ValidationFailed);
        Assert.Equal(TaskState.AwaitingValidationDecision, workflow.Context?.State);
        Assert.Contains("Повторное изменение модели не запущено", workflow.Context?.ValidationResult);
        Assert.Equal(1, runner.ExecutionCount);
        Assert.Equal(1, runner.ValidationCount);
    }

    [Fact]
    public async Task ExecutionCanReturnToPlanningAndRequiresNewApproval()
    {
        var runner = new FakeRunner(["[REPLAN] Не хватает шага"], ["[PASS]"]);
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Выполни задачу");

        await workflow.ApprovePlanAsync();

        Assert.Equal(TaskState.AwaitingPlanApproval, workflow.Context?.State);
        Assert.True(workflow.AwaitingPlanApproval);
        Assert.Equal(2, runner.PlanningCount);
        Assert.False(workflow.Context?.PlanApproved);
    }

    [Fact]
    public async Task PlanningFeedbackRebuildsPlanWithoutStartingExecution()
    {
        var runner = new FakeRunner(["[EXECUTED] Готово"], ["[PASS] Проверено"]);
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Выполни задачу");

        await workflow.RefinePlanAsync("Сначала покажи выбранные элементы");

        Assert.Equal(TaskState.AwaitingPlanApproval, workflow.Context?.State);
        Assert.True(workflow.AwaitingPlanApproval);
        Assert.Equal(2, runner.PlanningCount);
        Assert.Equal(0, runner.ExecutionCount);
        Assert.Equal("Сначала покажи выбранные элементы", runner.StoredUserMessage);
        Assert.Contains("Текущий план", runner.RevisionContext);
        Assert.Contains("Уточнение пользователя", runner.RevisionContext);
    }

    [Fact]
    public async Task ToolCallLimitReturnsToPlanningWithFailureReport()
    {
        var runner = new LimitFailingRunner();
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Выполни многошаговую задачу");

        await workflow.ApprovePlanAsync();

        Assert.Equal(TaskState.AwaitingPlanApproval, workflow.Context?.State);
        Assert.True(workflow.AwaitingPlanApproval);
        Assert.False(workflow.Context?.PlanApproved);
        Assert.Contains("лимит последовательных вызовов", workflow.Context?.ExecutionResult);
        Assert.Contains("лимит последовательных вызовов", runner.ReplanningReport);
    }

    [Fact]
    public async Task ValidationFailureRequiresExplicitFinishWithoutValidation()
    {
        var runner = new ValidationFailingRunner();
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Измени модель");

        await workflow.ApprovePlanAsync();

        Assert.Equal(TaskState.AwaitingValidationDecision, workflow.Context?.State);
        Assert.True(workflow.ValidationFailed);
        Assert.Equal(1, runner.ExecutionCount);
        Assert.Equal("task_state_validation_error", workflow.LastResponse?.ModelResponse.FinishReason);
        Assert.Contains("не найден текст модели", workflow.Context?.ValidationResult);

        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ContinueAsync());

        workflow.FinishWithoutValidation();

        Assert.Equal(TaskState.Done, workflow.Context?.State);
        Assert.Equal(1, runner.ExecutionCount);
        Assert.Equal("task_state_validation_skipped", workflow.LastResponse?.ModelResponse.FinishReason);
    }

    [Fact]
    public async Task RetryValidationContinuesWithoutRepeatingExecution()
    {
        var runner = new RetryValidationRunner();
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Измени модель");
        await workflow.ApprovePlanAsync();

        Assert.True(workflow.ValidationFailed);

        await workflow.RetryValidationAsync();

        Assert.Equal(TaskState.Done, workflow.Context?.State);
        Assert.Equal(1, runner.ExecutionCount);
        Assert.Equal(2, runner.ValidationCount);
    }

    [Fact]
    public async Task ExecutionClarificationResumesExecutionWithoutReturningToPlanning()
    {
        var runner = new FakeRunner(
            ["[CLARIFY] Какое значение записать?", "[EXECUTED] Значение записано"],
            ["[PASS] Проверено"]);
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Заполни параметр");
        await workflow.ApprovePlanAsync();

        Assert.Equal(TaskState.Clarification, workflow.Context?.State);
        Assert.Equal(TaskState.Execution, workflow.Context?.Clarification?.ResumeState);
        Assert.Equal(1, runner.ExecutionCount);

        await workflow.SubmitClarificationAsync("АР");

        Assert.Equal(TaskState.Done, workflow.Context?.State);
        Assert.Equal(2, runner.ExecutionCount);
        Assert.Equal(1, workflow.Context?.Checkpoint?.Attempt);
    }

    [Fact]
    public async Task PlanningClarificationReturnsToPlanningAndRequiresApproval()
    {
        var runner = new ClarifyingPlanningRunner();
        var workflow = new TaskWorkflow(runner);

        await workflow.StartAsync("Заполни параметр");
        Assert.Equal(TaskState.Clarification, workflow.Context?.State);
        Assert.Equal(TaskState.Planning, workflow.Context?.Clarification?.ResumeState);

        await workflow.SubmitClarificationAsync("Комментарии");

        Assert.Equal(TaskState.AwaitingPlanApproval, workflow.Context?.State);
        Assert.True(workflow.AwaitingPlanApproval);
        Assert.Equal("Комментарии", runner.StoredUserMessage);
        Assert.Contains("Ответ пользователя", runner.RevisionContext);
    }

    [Fact]
    public async Task EmptyExecutionFailureCanBeRetriedWithoutLosingTask()
    {
        var runner = new InterruptedRunner(toolCompleted: false);
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Выполни задачу");

        await workflow.ApprovePlanAsync();

        Assert.True(workflow.ExecutionInterrupted);
        Assert.Equal(TaskState.ExecutionInterrupted, workflow.Context?.State);
        Assert.True(workflow.Context?.ExecutionRetrySafe);
        Assert.Equal("task_state_execution_interrupted", workflow.LastResponse?.ModelResponse.FinishReason);
        Assert.Contains("Initial retry", workflow.LastResponse?.ToolTraces?.Single());

        await workflow.RetryInterruptedExecutionAsync();

        Assert.Equal(TaskState.Done, workflow.Context?.State);
        Assert.Equal(2, runner.ExecutionCount);
        Assert.Equal(1, workflow.Context?.Checkpoint?.Attempt);
    }

    [Fact]
    public async Task ToolResultBeforeFailureBlocksMutationRetryAndAllowsValidation()
    {
        var runner = new InterruptedRunner(toolCompleted: true);
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Измени модель");

        await workflow.ApprovePlanAsync();

        Assert.True(workflow.ExecutionInterrupted);
        Assert.False(workflow.Context?.ExecutionRetrySafe);
        Assert.Contains("Автоматический повтор заблокирован", workflow.Context?.ExecutionResult);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RetryInterruptedExecutionAsync());

        await workflow.ValidateInterruptedExecutionAsync();

        Assert.Equal(TaskState.Done, workflow.Context?.State);
        Assert.Equal(1, runner.ExecutionCount);
        Assert.Equal(1, runner.ValidationCount);
    }

    [Fact]
    public async Task FailedValidationAfterInterruptedMutationNeverRepeatsExecutionAutomatically()
    {
        var runner = new InterruptedRunner(toolCompleted: true, validation: "[RETRY_EXECUTION] Требуется проверка пользователем");
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Измени модель");
        await workflow.ApprovePlanAsync();

        await workflow.ValidateInterruptedExecutionAsync();

        Assert.Equal(TaskState.AwaitingValidationDecision, workflow.Context?.State);
        Assert.Equal(1, runner.ExecutionCount);
        Assert.Equal(1, runner.ValidationCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RetryExecutionAsync());
    }

    [Fact]
    public async Task EmptyPlanningResponseCanBeRetriedWithoutBlockingTheTask()
    {
        var runner = new InterruptedPlanningRunner();
        var workflow = new TaskWorkflow(runner);

        await workflow.StartAsync("Выполни задачу");

        Assert.True(workflow.PlanningInterrupted);
        Assert.Equal(TaskState.PlanningInterrupted, workflow.Context?.State);
        Assert.Equal("task_state_planning_interrupted", workflow.LastResponse?.ModelResponse.FinishReason);
        Assert.Contains("пустой structured-ответ", workflow.Context?.FailureReason);

        await workflow.RetryPlanningAsync();

        Assert.Equal(TaskState.AwaitingPlanApproval, workflow.Context?.State);
        Assert.Equal(2, runner.PlanningCount);
    }

    [Fact]
    public async Task InterruptedPlanningAcceptsUserRefinement()
    {
        var runner = new InterruptedPlanningRunner();
        var workflow = new TaskWorkflow(runner);
        await workflow.StartAsync("Выполни задачу");

        await workflow.RefineInterruptedPlanningAsync("Используй только выбранные элементы");

        Assert.Equal(TaskState.AwaitingPlanApproval, workflow.Context?.State);
        Assert.Equal("Используй только выбранные элементы", runner.StoredUserMessage);
        Assert.Contains("Уточнение пользователя после сбоя", runner.RevisionContext);
    }

    [Fact]
    public async Task CancelCreatesUserFacingResponse()
    {
        var workflow = new TaskWorkflow(new InterruptedPlanningRunner());
        await workflow.StartAsync("Выполни задачу");

        workflow.Cancel();

        Assert.Equal(TaskState.Cancelled, workflow.Context?.State);
        Assert.Equal("Задача отменена", workflow.LastResponse?.ModelResponse.Content);
        Assert.Equal("task_state_cancelled", workflow.LastResponse?.ModelResponse.FinishReason);
    }

    private sealed class FakeRunner(
        IEnumerable<string> executions,
        IEnumerable<string> validations) : ITaskStageRunner
    {
        private readonly Queue<string> _executions = new(executions);
        private readonly Queue<string> _validations = new(validations);
        public int PlanningCount { get; private set; }
        public int ExecutionCount { get; private set; }
        public int ValidationCount { get; private set; }
        public string? RevisionContext { get; private set; }
        public string? StoredUserMessage { get; private set; }

        public Task<AgentResponse> PlanTaskAsync(string query, string? revisionContext = null, CancellationToken cancellationToken = default, string? storedUserMessage = null)
        {
            PlanningCount++;
            RevisionContext = revisionContext;
            StoredUserMessage = storedUserMessage;
            return Task.FromResult(Response(PlanningCount == 1 ? "1. Первый план" : "1. Пересмотренный план"));
        }

        public Task<AgentResponse> ExecuteTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.FromResult(Response(_executions.Dequeue()));
        }

        public Task<AgentResponse> ValidateTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            ValidationCount++;
            return Task.FromResult(Response(_validations.Dequeue()));
        }

        private static AgentResponse Response(string content) => new(
            new LlmResponse(content, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)),
            0,
            true,
            0);
    }

    private sealed class ToolErrorRunner : ITaskStageRunner
    {
        public int ExecutionCount { get; private set; }
        public int ValidationCount { get; private set; }

        public Task<AgentResponse> PlanTaskAsync(string query, string? revisionContext = null,
            CancellationToken cancellationToken = default, string? storedUserMessage = null) =>
            throw new InvalidOperationException("Direct mode must not plan.");

        public Task<AgentResponse> ExecuteTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            var results = new[]
            {
                new ToolResultEnvelope(true, "revit_custom_open_model", JsonSerializer.SerializeToElement(new { }),
                    JsonSerializer.SerializeToElement(new { success = true }), null, true, true, 1, true),
                new ToolResultEnvelope(false, "revit_custom_load_families", JsonSerializer.SerializeToElement(new { }),
                    null, new ToolError("invalid_arguments", "Supply exactly one of path or folderPath."),
                    true, true, 1, true)
            };
            return Task.FromResult(new AgentResponse(
                new LlmResponse("Инструмент revit_custom_load_families не выполнен.", "tool_error",
                    new TokenUsage(0, 0, 0, 0, 0, 0, 0, true), results), 0, true, 0));
        }

        public Task<AgentResponse> ValidateTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            ValidationCount++;
            throw new InvalidOperationException("Validation must not run after a tool error.");
        }
    }

    private sealed class InterruptedPlanningRunner : ITaskStageRunner
    {
        public int PlanningCount { get; private set; }
        public string? RevisionContext { get; private set; }
        public string? StoredUserMessage { get; private set; }

        public Task<AgentResponse> PlanTaskAsync(string query, string? revisionContext = null, CancellationToken cancellationToken = default, string? storedUserMessage = null)
        {
            PlanningCount++;
            RevisionContext = revisionContext;
            StoredUserMessage = storedUserMessage;
            if (PlanningCount == 1)
                throw new AgentStageException(
                    "GigaChat трижды вернул пустой structured-ответ.",
                    [],
                    new InvalidOperationException("empty"));
            return Task.FromResult(Response("1. Проверенный план"));
        }

        public Task<AgentResponse> ExecuteTaskAsync(TaskContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response("[EXECUTED] Готово"));

        public Task<AgentResponse> ValidateTaskAsync(TaskContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response("[PASS] Проверено"));

        private static AgentResponse Response(string content) => new(
            new LlmResponse(content, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)), 0, true, 0);
    }

    private sealed class LimitFailingRunner : ITaskStageRunner
    {
        public string? ReplanningReport { get; private set; }

        public Task<AgentResponse> PlanTaskAsync(string query, string? revisionContext = null, CancellationToken cancellationToken = default, string? storedUserMessage = null)
        {
            ReplanningReport = revisionContext;
            return Task.FromResult(Response("1. Сокращённый план"));
        }

        public Task<AgentResponse> ExecuteTaskAsync(TaskContext context, CancellationToken cancellationToken = default) =>
            throw new ToolCallLimitExceededException(4);

        public Task<AgentResponse> ValidateTaskAsync(TaskContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation не должна запускаться после превышения лимита.");

        private static AgentResponse Response(string content) => new(
            new LlmResponse(content, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)),
            0,
            true,
            0);
    }

    private sealed class ValidationFailingRunner : ITaskStageRunner
    {
        public int ExecutionCount { get; private set; }

        public Task<AgentResponse> PlanTaskAsync(string query, string? revisionContext = null, CancellationToken cancellationToken = default, string? storedUserMessage = null) =>
            Task.FromResult(Response("1. Изменить модель"));

        public Task<AgentResponse> ExecuteTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.FromResult(Response("[EXECUTED] Модель изменена"));
        }

        public Task<AgentResponse> ValidateTaskAsync(TaskContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("В ответе GigaChat не найден текст модели.");

        private static AgentResponse Response(string content) => new(
            new LlmResponse(content, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)),
            0,
            true,
            0);
    }

    private sealed class RetryValidationRunner : ITaskStageRunner
    {
        public int ExecutionCount { get; private set; }
        public int ValidationCount { get; private set; }

        public Task<AgentResponse> PlanTaskAsync(string query, string? revisionContext = null, CancellationToken cancellationToken = default, string? storedUserMessage = null) =>
            Task.FromResult(Response("1. Изменить модель"));

        public Task<AgentResponse> ExecuteTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return Task.FromResult(Response("[EXECUTED] Модель изменена"));
        }

        public Task<AgentResponse> ValidateTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            ValidationCount++;
            return ValidationCount == 1
                ? throw new InvalidOperationException("Временный сбой Validation.")
                : Task.FromResult(Response("[PASS] Проверено"));
        }

        private static AgentResponse Response(string content) => new(
            new LlmResponse(content, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)),
            0,
            true,
            0);
    }

    private sealed class ClarifyingPlanningRunner : ITaskStageRunner
    {
        private int _planningCount;
        public string? RevisionContext { get; private set; }
        public string? StoredUserMessage { get; private set; }

        public Task<AgentResponse> PlanTaskAsync(string query, string? revisionContext = null, CancellationToken cancellationToken = default, string? storedUserMessage = null)
        {
            _planningCount++;
            RevisionContext = revisionContext;
            StoredUserMessage = storedUserMessage;
            return Task.FromResult(Response(_planningCount == 1 ? "[CLARIFY] Какой параметр заполнить?" : "1. Заполнить параметр"));
        }

        public Task<AgentResponse> ExecuteTaskAsync(TaskContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response("[EXECUTED] Готово"));

        public Task<AgentResponse> ValidateTaskAsync(TaskContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response("[PASS] Проверено"));

        private static AgentResponse Response(string content) => new(
            new LlmResponse(content, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)), 0, true, 0);
    }

    private sealed class InterruptedRunner(bool toolCompleted, string validation = "[PASS] Проверено") : ITaskStageRunner
    {
        public int ExecutionCount { get; private set; }
        public int ValidationCount { get; private set; }

        public Task<AgentResponse> PlanTaskAsync(string query, string? revisionContext = null, CancellationToken cancellationToken = default, string? storedUserMessage = null) =>
            Task.FromResult(Response("1. Выполнить задачу"));

        public Task<AgentResponse> ExecuteTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            if (ExecutionCount == 1)
            {
                var traces = toolCompleted
                    ? new[] { "Tool result: revit_write {\"ok\":true}" }
                    : new[] { "Empty response before first tool. Initial retry 2/2" };
                throw new AgentStageException("GigaChat вернул пустой ответ.", traces, new InvalidOperationException("empty"));
            }
            return Task.FromResult(Response("[EXECUTED] Готово"));
        }

        public Task<AgentResponse> ValidateTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            ValidationCount++;
            return Task.FromResult(Response(validation));
        }

        private static AgentResponse Response(string content) => new(
            new LlmResponse(content, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)), 0, true, 0);
    }
}
