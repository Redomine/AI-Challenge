using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Revit;
using DesignerAssistant.Storage;
using DesignerAssistant.Tools;
using System.Text.Json;

namespace DesignerAssistant.Web.Services;

public sealed class AssistantSession : IAsyncDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private DesignAssistantAgent? _agent;
    private TaskWorkflow? _workflow;
    private RevitMcpClient? _revit;
    private StdioMcpToolProvider? _log;
    private StdioMcpToolProvider? _ops;
    private TaskOperationsReporter? _reporter;
    private TaskCompletionSource<bool>? _confirmation;
    private RagQueryService? _ragQuery;
    private SwitchableLlmClient? _llm;
    private bool _allowInteractiveConfirmation = true;
    private Func<string, Task<bool>>? _confirmationHandler;

    public AssistantSession(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public event Action? Changed;
    public string? PendingTransaction { get; private set; }
    public AppOptions? Options { get; private set; }
    public LlmProvider Provider => _llm?.Provider ?? LlmProvider.Ollama;
    public string OllamaModel => OllamaSettings.Model;
    public string? LastReasoning => _llm?.LastReasoning;
    public bool HasModelResponse => _llm?.HasResponse == true;
    public IReadOnlyList<AgentActivityEntry> AgentActivity => _workflow?.Activity ?? [];
    public TaskContext? CurrentTask => _workflow?.Context;
    public bool AwaitingPlanApproval => _workflow?.AwaitingPlanApproval == true;
    public bool PlanningInterrupted => _workflow?.PlanningInterrupted == true;
    public bool AwaitingClarification => _workflow?.AwaitingClarification == true;
    public bool ExecutionInterrupted => _workflow?.ExecutionInterrupted == true;
    public bool IsTaskPaused => _workflow?.IsPaused == true;
    public bool ValidationFailed => _workflow?.ValidationFailed == true;
    public AgentResponse? LastTaskResponse => _workflow?.LastResponse;
    public bool AutoApproveRevitChanges { get; set; }
    public int OperationTimeoutMinutes
    {
        get => _operationTimeoutMinutes;
        set
        {
            _operationTimeoutMinutes = Math.Clamp(value, 1, 120);
            if (_revit is not null) _revit.OperationTimeout = TimeSpan.FromMinutes(_operationTimeoutMinutes);
        }
    }
    private int _operationTimeoutMinutes = 10;

    public IReadOnlyList<ChatMessage> History => _agent?.GetHistory() ?? [];

    public async Task InitializeAsync(
        string contentRoot,
        RagQueryService? ragQuery = null,
        bool allowInteractiveConfirmation = true,
        bool autoApproveRevitChanges = false,
        int operationTimeoutMinutes = 10,
        bool automaticNotifications = true,
        CancellationToken cancellationToken = default,
        OllamaTuningProfile tuningProfile = OllamaTuningProfile.Standard,
        string? databasePathOverride = null,
        Func<string, Task<bool>>? confirmationHandler = null)
    {
        if (_agent is not null) return;
        _allowInteractiveConfirmation = allowInteractiveConfirmation;
        _confirmationHandler = confirmationHandler;
        AutoApproveRevitChanges = autoApproveRevitChanges;
        OperationTimeoutMinutes = operationTimeoutMinutes;
        _ragQuery = ragQuery;
        var databasePath = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_DB_PATH");
        if (string.IsNullOrWhiteSpace(databasePath) && databasePathOverride is null)
        {
            databasePath = Path.GetFullPath(Path.Combine(contentRoot, "..", "DesignerAssistant", "designer-assistant.db"));
            Environment.SetEnvironmentVariable("DESIGN_ASSISTANT_DB_PATH", databasePath);
        }

        Options = AppOptions.FromEnvironment(requireGigaChatKey: false);
        if (databasePathOverride is not null)
            Options = Options with { DatabasePath = Path.GetFullPath(databasePathOverride) };
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromMinutes(2);
        var ollamaHttp = _httpClientFactory.CreateClient();
        ollamaHttp.BaseAddress = OllamaSettings.BaseAddress;
        ollamaHttp.Timeout = TimeSpan.FromMinutes(5);
        _llm = new SwitchableLlmClient(
            new OllamaLlmClient(ollamaHttp, OllamaSettings.Model, Options.MaxOutputTokens,
                profile: tuningProfile),
            new GigaChatClient(httpClient, Options),
            !string.IsNullOrWhiteSpace(Options.AuthorizationKey));
        _llm.Changed += () => Changed?.Invoke();
        var history = new SqliteChatHistoryStore(Options.DatabasePath);
        var memory = new SqliteMemoryStore(Options.DatabasePath);
        var profiles = new SqliteUserProfileStore(Options.DatabasePath);
        var invariants = new SqliteInvariantStore(Options.DatabasePath);
        _revit = new RevitMcpClient(confirmWriteAsync: ConfirmTransactionAsync);
        _revit.OperationTimeout = TimeSpan.FromMinutes(OperationTimeoutMinutes);
        var operationsPath = OperationsMcpPath.Resolve(contentRoot);
        _log = new StdioMcpToolProvider("log", operationsPath);
        _ops = new StdioMcpToolProvider("ops", operationsPath);
        _reporter = new TaskOperationsReporter(_log, _ops, automaticNotifications);
        var workspace = new WorkspaceToolProvider(WorkspaceRootLocator.Find(contentRoot));
        var tools = new AuditedToolProvider(new CompositeToolProvider(_revit, workspace, _log, _ops), _log);
        _agent = new DesignAssistantAgent(_llm, history, memory, DesignerAssistantPrompt.Text, Options,
            tools, profiles, invariants, optimizedLocal: tuningProfile == OllamaTuningProfile.Optimized);
        await _agent.InitializeAsync(cancellationToken);
        _workflow = new TaskWorkflow(_agent);
        _workflow.ActivityChanged += () => Changed?.Invoke();
    }

    public void SelectProvider(LlmProvider provider)
    {
        _llm?.Select(provider);
        _workflow?.ClearActivity();
    }

    public async Task<ToolResultEnvelope> RunConfirmedRevitToolAsync(
        string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var policy = new ToolExecutionPolicy(Revit, await Revit.GetToolsAsync(cancellationToken));
        return await policy.ExecuteAsync(name, arguments, cancellationToken);
    }

    public Task<AgentResponse> AskAsync(string message, CancellationToken cancellationToken = default) =>
        Agent.AskAsync(message, cancellationToken);

    /// <summary>
    /// Отвечает на вопрос в режиме RAG или без поиска. Делегирует
    /// работу <see cref="RagQueryService"/>. Используется
    /// UI-переключателем и автотестом. Не трогает workflow Revit,
    /// поэтому вызов безопасен параллельно с активной задачей.
    /// </summary>
    public Task<RagQueryResult> AskRagAsync(
        string question,
        RagMode mode,
        CancellationToken cancellationToken = default)
    {
        if (_ragQuery is null)
        {
            throw new InvalidOperationException("RAG-сервис не инициализирован.");
        }
        return _ragQuery.QueryAsync(question, mode, cancellationToken);
    }

    public async Task StartTaskAsync(
        string message,
        TaskPauseOptions pauseOptions,
        CancellationToken cancellationToken = default)
    {
        await Revit.ConfigureOperationTimeoutAsync(TimeSpan.FromMinutes(OperationTimeoutMinutes));
        Workflow.PauseOptions = pauseOptions;
        await _reporter!.StartAsync(cancellationToken);
        await Workflow.StartAsync(message, cancellationToken);
        await _reporter.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task StartDirectTaskAsync(
        string message,
        TaskPauseOptions pauseOptions,
        CancellationToken cancellationToken = default)
    {
        await Revit.ConfigureOperationTimeoutAsync(TimeSpan.FromMinutes(OperationTimeoutMinutes));
        Workflow.PauseOptions = pauseOptions;
        await _reporter!.StartAsync(cancellationToken);
        await Workflow.StartDirectAsync(message, cancellationToken);
        await _reporter.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task ApprovePlanAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.ApprovePlanAsync(cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task RefinePlanAsync(string feedback, CancellationToken cancellationToken = default)
    {
        await Workflow.RefinePlanAsync(feedback, cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task RetryPlanningAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.RetryPlanningAsync(cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task RefineInterruptedPlanningAsync(string feedback, CancellationToken cancellationToken = default)
    {
        await Workflow.RefineInterruptedPlanningAsync(feedback, cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task SubmitClarificationAsync(string answer, CancellationToken cancellationToken = default)
    {
        await Workflow.SubmitClarificationAsync(answer, cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task RetryExecutionAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.RetryExecutionAsync(cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task RetryInterruptedExecutionAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.RetryInterruptedExecutionAsync(cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task RefineInterruptedExecutionAsync(string feedback, CancellationToken cancellationToken = default)
    {
        await Workflow.RefineInterruptedExecutionAsync(feedback, cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task ValidateInterruptedExecutionAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.ValidateInterruptedExecutionAsync(cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task ReplanInterruptedExecutionAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.ReplanInterruptedExecutionAsync(cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task ReplanAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.ReplanAsync(cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task ContinueTaskAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.ContinueAsync(cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task RetryValidationAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.RetryValidationAsync(cancellationToken);
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task FinishWithoutValidationAsync(CancellationToken cancellationToken = default)
    {
        Workflow.FinishWithoutValidation();
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        Changed?.Invoke();
    }

    public async Task CancelTaskAsync(CancellationToken cancellationToken = default)
    {
        Workflow.Cancel();
        await _reporter!.ObserveAsync(CurrentTask?.State, cancellationToken);
        await Agent.AppendAssistantMessageAsync("Задача отменена", TaskState.Cancelled, cancellationToken);
        Changed?.Invoke();
    }

    public async Task AppendErrorAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        await AppendErrorAsync(exception.Message, cancellationToken);
    }

    public async Task AppendErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message)) message = "Неизвестная ошибка.";
        await Agent.AppendAssistantMessageAsync($"Ошибка: {message.Trim()}", TaskState.Failed, cancellationToken);
        await _reporter!.ObserveAsync(TaskState.Failed, cancellationToken);
        Changed?.Invoke();
    }

    public Task<MemorySnapshot> GetMemoryAsync(CancellationToken cancellationToken = default) =>
        Agent.GetMemoryAsync(cancellationToken);

    public Task<UserProfile?> GetProfileAsync(CancellationToken cancellationToken = default) =>
        Agent.GetProfileAsync(cancellationToken);

    public Task<IReadOnlyList<UserProfile>> GetProfilesAsync(CancellationToken cancellationToken = default) =>
        Agent.GetProfilesAsync(cancellationToken);

    public Task SaveProfileAsync(UserProfile profile, CancellationToken cancellationToken = default) =>
        Agent.SaveProfileAsync(profile, cancellationToken);

    public Task<bool> SelectProfileAsync(string name, CancellationToken cancellationToken = default) =>
        Agent.SelectProfileAsync(name, cancellationToken);

    public Task DeleteProfileAsync(CancellationToken cancellationToken = default) =>
        Agent.DeleteProfileAsync(cancellationToken);

    public Task<string> GetInvariantsAsync(CancellationToken cancellationToken = default) =>
        Agent.GetInvariantsAsync(cancellationToken);

    public Task SaveInvariantsAsync(string text, CancellationToken cancellationToken = default) =>
        Agent.SaveInvariantsAsync(text, cancellationToken);

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        await Agent.ClearHistoryAsync(cancellationToken);
        _workflow?.ClearActivity();
    }

    public Task CompleteTaskAsync(CancellationToken cancellationToken = default) =>
        Agent.CompleteTaskAsync(cancellationToken);

    public Task<IReadOnlyList<RevitSession>> GetRevitSessionsAsync(CancellationToken cancellationToken = default) =>
        Revit.ListSessionsAsync(cancellationToken);

    public Task<string> SelectRevitSessionAsync(string selector, CancellationToken cancellationToken = default) =>
        Revit.SelectSessionAsync(selector, cancellationToken);

    public Task<string> SelectRevitYearAsync(int year, CancellationToken cancellationToken = default) =>
        Revit.UseAsync(year, cancellationToken);

    public Task<bool> ReportScheduledOutcomeAsync(
        string taskName, string summary, bool success, bool notifyOnSuccess,
        CancellationToken cancellationToken = default) =>
        (_reporter ?? throw new InvalidOperationException("Журнал задач не инициализирован."))
        .ReportScheduledOutcomeAsync(taskName, summary, success, notifyOnSuccess, cancellationToken);

    public void ResolveTransaction(bool approved)
    {
        var confirmation = _confirmation;
        _confirmation = null;
        PendingTransaction = null;
        confirmation?.TrySetResult(approved);
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _confirmation?.TrySetResult(false);
        if (_revit is not null) await _revit.DisposeAsync();
        if (_log is not null) await _log.DisposeAsync();
        if (_ops is not null) await _ops.DisposeAsync();
    }

    private async Task<bool> ConfirmTransactionAsync(string proposal)
    {
        if (_confirmationHandler is not null) return await _confirmationHandler(proposal);
        if (AutoApproveRevitChanges) return true;
        if (!_allowInteractiveConfirmation) return false;
        _confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingTransaction = proposal;
        Changed?.Invoke();
        return await _confirmation.Task;
    }

    private DesignAssistantAgent Agent => _agent ?? throw new InvalidOperationException("Сессия не инициализирована.");
    private TaskWorkflow Workflow => _workflow ?? throw new InvalidOperationException("Машина состояний не инициализирована.");
    private RevitMcpClient Revit => _revit ?? throw new InvalidOperationException("Сессия Revit не инициализирована.");
}
