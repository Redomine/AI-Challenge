using DesignerAssistant.Agent;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class TaskWorkflowTests
{
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
}
