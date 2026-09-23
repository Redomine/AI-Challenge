using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Revit;
using DesignerAssistant.Storage;
using DesignerAssistant.Tools;

namespace DesignerAssistant.Web.Services;

public sealed class AssistantSession : IAsyncDisposable
{
    private readonly IHttpClientFactory _httpClientFactory;
    private DesignAssistantAgent? _agent;
    private TaskWorkflow? _workflow;
    private RevitMcpClient? _revit;
    private TaskCompletionSource<bool>? _confirmation;
    private bool _allowInteractiveConfirmation = true;

    public AssistantSession(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public event Action? Changed;
    public string? PendingTransaction { get; private set; }
    public AppOptions? Options { get; private set; }
    public TaskContext? CurrentTask => _workflow?.Context;
    public bool AwaitingPlanApproval => _workflow?.AwaitingPlanApproval == true;
    public bool PlanningInterrupted => _workflow?.PlanningInterrupted == true;
    public bool AwaitingClarification => _workflow?.AwaitingClarification == true;
    public bool ExecutionInterrupted => _workflow?.ExecutionInterrupted == true;
    public bool IsTaskPaused => _workflow?.IsPaused == true;
    public bool ValidationFailed => _workflow?.ValidationFailed == true;
    public AgentResponse? LastTaskResponse => _workflow?.LastResponse;

    public IReadOnlyList<ChatMessage> History => _agent?.GetHistory() ?? [];

    public async Task InitializeAsync(
        string contentRoot,
        bool allowInteractiveConfirmation = true,
        CancellationToken cancellationToken = default)
    {
        if (_agent is not null) return;
        _allowInteractiveConfirmation = allowInteractiveConfirmation;
        var databasePath = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_DB_PATH");
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.GetFullPath(Path.Combine(contentRoot, "..", "DesignerAssistant", "designer-assistant.db"));
            Environment.SetEnvironmentVariable("DESIGN_ASSISTANT_DB_PATH", databasePath);
        }

        Options = AppOptions.FromEnvironment();
        var httpClient = _httpClientFactory.CreateClient();
        httpClient.Timeout = TimeSpan.FromMinutes(2);
        var llm = new GigaChatClient(httpClient, Options);
        var history = new SqliteChatHistoryStore(Options.DatabasePath);
        var memory = new SqliteMemoryStore(Options.DatabasePath);
        var profiles = new SqliteUserProfileStore(Options.DatabasePath);
        var invariants = new SqliteInvariantStore(Options.DatabasePath);
        _revit = new RevitMcpClient(confirmWriteAsync: ConfirmTransactionAsync);
        var workspace = new WorkspaceToolProvider(WorkspaceRootLocator.Find(contentRoot));
        var tools = new CompositeToolProvider(_revit, workspace);
        _agent = new DesignAssistantAgent(llm, history, memory, DesignerAssistantPrompt.Text, Options, tools, profiles, invariants);
        await _agent.InitializeAsync(cancellationToken);
        _workflow = new TaskWorkflow(_agent);
    }

    public Task<AgentResponse> AskAsync(string message, CancellationToken cancellationToken = default) =>
        Agent.AskAsync(message, cancellationToken);

    public async Task StartTaskAsync(
        string message,
        TaskPauseOptions pauseOptions,
        CancellationToken cancellationToken = default)
    {
        Workflow.PauseOptions = pauseOptions;
        await Workflow.StartAsync(message, cancellationToken);
        Changed?.Invoke();
    }

    public async Task StartDirectTaskAsync(
        string message,
        TaskPauseOptions pauseOptions,
        CancellationToken cancellationToken = default)
    {
        Workflow.PauseOptions = pauseOptions;
        await Workflow.StartDirectAsync(message, cancellationToken);
        Changed?.Invoke();
    }

    public async Task ApprovePlanAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.ApprovePlanAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task RefinePlanAsync(string feedback, CancellationToken cancellationToken = default)
    {
        await Workflow.RefinePlanAsync(feedback, cancellationToken);
        Changed?.Invoke();
    }

    public async Task RetryPlanningAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.RetryPlanningAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task RefineInterruptedPlanningAsync(string feedback, CancellationToken cancellationToken = default)
    {
        await Workflow.RefineInterruptedPlanningAsync(feedback, cancellationToken);
        Changed?.Invoke();
    }

    public async Task SubmitClarificationAsync(string answer, CancellationToken cancellationToken = default)
    {
        await Workflow.SubmitClarificationAsync(answer, cancellationToken);
        Changed?.Invoke();
    }

    public async Task RetryExecutionAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.RetryExecutionAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task RetryInterruptedExecutionAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.RetryInterruptedExecutionAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task RefineInterruptedExecutionAsync(string feedback, CancellationToken cancellationToken = default)
    {
        await Workflow.RefineInterruptedExecutionAsync(feedback, cancellationToken);
        Changed?.Invoke();
    }

    public async Task ValidateInterruptedExecutionAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.ValidateInterruptedExecutionAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task ReplanInterruptedExecutionAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.ReplanInterruptedExecutionAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task ReplanAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.ReplanAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task ContinueTaskAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.ContinueAsync(cancellationToken);
        Changed?.Invoke();
    }

    public async Task RetryValidationAsync(CancellationToken cancellationToken = default)
    {
        await Workflow.RetryValidationAsync(cancellationToken);
        Changed?.Invoke();
    }

    public void FinishWithoutValidation()
    {
        Workflow.FinishWithoutValidation();
        Changed?.Invoke();
    }

    public async Task CancelTaskAsync(CancellationToken cancellationToken = default)
    {
        Workflow.Cancel();
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

    public Task ClearHistoryAsync(CancellationToken cancellationToken = default) =>
        Agent.ClearHistoryAsync(cancellationToken);

    public Task CompleteTaskAsync(CancellationToken cancellationToken = default) =>
        Agent.CompleteTaskAsync(cancellationToken);

    public Task<IReadOnlyList<RevitSession>> GetRevitSessionsAsync(CancellationToken cancellationToken = default) =>
        Revit.ListSessionsAsync(cancellationToken);

    public Task<string> SelectRevitSessionAsync(string selector, CancellationToken cancellationToken = default) =>
        Revit.SelectSessionAsync(selector, cancellationToken);

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
    }

    private async Task<bool> ConfirmTransactionAsync(string proposal)
    {
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
