using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Storage;

namespace DesignerAssistant.Agent;

public sealed class DesignAssistantAgent : IDesignAssistantAgent
{
    private readonly ILlmClient _llmClient;
    private readonly IChatHistoryStore _historyStore;
    private readonly IMemoryStore _memoryStore;
    private readonly IUserProfileStore _profileStore;
    private readonly IToolProvider? _toolProvider;
    private readonly string _instructions;
    private readonly int _recentMessageCount;
    private readonly List<ChatMessage> _history = [];
    private bool _isInitialized;

    public DesignAssistantAgent(
        ILlmClient llmClient,
        IChatHistoryStore historyStore,
        IMemoryStore memoryStore,
        string instructions,
        AppOptions options,
        IToolProvider? toolProvider = null,
        IUserProfileStore? profileStore = null)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _profileStore = profileStore ?? new InMemoryUserProfileStore();
        _toolProvider = toolProvider;
        _instructions = string.IsNullOrWhiteSpace(instructions) ? throw new ArgumentException("Системная инструкция не задана.", nameof(instructions)) : instructions;
        _recentMessageCount = options.RecentMessageCount;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return;
        await _historyStore.InitializeAsync(cancellationToken);
        await _memoryStore.InitializeAsync(cancellationToken);
        await _profileStore.InitializeAsync(cancellationToken);
        _history.AddRange(await _historyStore.LoadAsync(cancellationToken));
        _isInitialized = true;
    }

    public async Task<AgentResponse> AskAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(userMessage)) throw new ArgumentException("Сообщение не должно быть пустым.", nameof(userMessage));
        var messages = _history.TakeLast(_recentMessageCount).ToList();
        messages.Add(new ChatMessage("user", userMessage.Trim()));
        var profile = await _profileStore.LoadAsync(cancellationToken);
        var instructions = await BuildInstructionsAsync(profile, cancellationToken);
        var traces = new List<string>();
        var response = _toolProvider is not null && _llmClient is IToolCallingLlmClient toolClient
            ? await toolClient.GenerateWithToolsAsync(instructions, messages, _toolProvider, traces.Add, cancellationToken)
            : await _llmClient.GenerateAsync(instructions, messages, cancellationToken);
        if (profile is not null)
        {
            response = await EnforceProfileAsync(profile, userMessage.Trim(), response, traces, cancellationToken);
        }
        var completed = new[] { messages[^1], new ChatMessage("assistant", response.Content) };
        _history.AddRange(completed);
        await _historyStore.AppendAsync(completed, cancellationToken);
        var fullCount = await _llmClient.CountTextTokensAsync(_history.Select(message => message.Content).ToArray(), cancellationToken);
        var sentCount = await _llmClient.CountTextTokensAsync(messages.Select(message => message.Content).Append(response.Content).ToArray(), cancellationToken);
        return new AgentResponse(response, fullCount.TotalTokens, fullCount.IsEstimated, sentCount.TotalTokens, traces);
    }

    public IReadOnlyList<ChatMessage> GetHistory() { EnsureInitialized(); return _history.ToArray(); }

    public async Task<MemorySnapshot> GetMemoryAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return new MemorySnapshot(GetHistory(), await _memoryStore.LoadAsync(MemoryLayer.Working, cancellationToken: cancellationToken), await _memoryStore.LoadAsync(MemoryLayer.LongTerm, cancellationToken: cancellationToken), "current");
    }

    public async Task RememberAsync(MemoryLayer layer, string key, string value, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Укажите ключ и значение памяти.");
        await _memoryStore.UpsertAsync(layer, key, value, MemorySource.User, EvidenceStatus.Confirmed, cancellationToken: cancellationToken);
    }

    public async Task<UserProfile?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await _profileStore.LoadAsync(cancellationToken);
    }

    public async Task SaveProfileAsync(UserProfile profile, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _profileStore.SaveAsync(profile, cancellationToken);
    }

    public async Task<IReadOnlyList<UserProfile>> GetProfilesAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        return await _profileStore.ListAsync(cancellationToken);
    }

    public async Task<bool> SelectProfileAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Укажите имя профиля.", nameof(name));
        return await _profileStore.SelectAsync(name, cancellationToken);
    }

    public async Task DeleteProfileAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var profile = await _profileStore.LoadAsync(cancellationToken);
        if (profile is not null) await _profileStore.DeleteAsync(profile.Name, cancellationToken);
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _historyStore.ClearAsync(cancellationToken);
        _history.Clear();
    }

    public async Task CompleteTaskAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _memoryStore.ClearAsync(MemoryLayer.Working, cancellationToken: cancellationToken);
    }

    private async Task<string> BuildMemoryContextAsync(CancellationToken cancellationToken)
    {
        var longTerm = await _memoryStore.LoadAsync(MemoryLayer.LongTerm, cancellationToken: cancellationToken);
        var working = await _memoryStore.LoadAsync(MemoryLayer.Working, cancellationToken: cancellationToken);
        static string Lines(IEnumerable<MemoryEntry> entries) => string.Join(Environment.NewLine, entries.Select(entry => $"- {entry.Key}: {entry.Value} [source={entry.Source}; status={entry.Status}]"));
        return $"[LONG_TERM_MEMORY]\n{Lines(longTerm)}\n[/LONG_TERM_MEMORY]\n\n[WORKING_MEMORY task=current]\n{Lines(working)}\n[/WORKING_MEMORY]";
    }

    private async Task<string> BuildInstructionsAsync(UserProfile? profile, CancellationToken cancellationToken)
    {
        var profileBlock = profile is null
            ? "[ACTIVE_USER_PROFILE]\nNot configured.\n[/ACTIVE_USER_PROFILE]"
            : profile.ToPromptBlock();
        return $"{_instructions}\n\n{await BuildMemoryContextAsync(cancellationToken)}\n\n{profileBlock}";
    }

    private async Task<LlmResponse> EnforceProfileAsync(
        UserProfile profile,
        string userMessage,
        LlmResponse draft,
        IReadOnlyCollection<string> traces,
        CancellationToken cancellationToken)
    {
        var toolEvidence = string.Join(
            Environment.NewLine,
            traces.Where(trace =>
                trace.StartsWith("Tool result:", StringComparison.Ordinal) ||
                trace.StartsWith("Tool catalogue:", StringComparison.Ordinal)));
        var instructions = $$"""
            You are a profile compliance editor. Return only the final response to the user.
            Apply every instruction from ACTIVE_USER_PROFILE, including style, constraints, tooling rules, and context.
            Do not add preferences, profile fields, actions, or facts that are absent from the supplied data.
            Never claim that a profile or memory was changed unless the supplied evidence explicitly confirms that application action.
            Preserve ElementId values, Revit names, measurements, and factual tool results exactly, even when changing response language or style.

            {{profile.ToPromptBlock()}}
            """;
        var reviewMessage = $$"""
            [USER_REQUEST]
            {{userMessage}}
            [/USER_REQUEST]

            [DRAFT_RESPONSE]
            {{draft.Content}}
            [/DRAFT_RESPONSE]

            [TOOL_EVIDENCE]
            {{(string.IsNullOrWhiteSpace(toolEvidence) ? "None." : toolEvidence)}}
            [/TOOL_EVIDENCE]
            """;
        var enforced = await _llmClient.GenerateAsync(
            instructions,
            [new ChatMessage("user", reviewMessage)],
            cancellationToken);
        if (traces is List<string> mutableTraces)
        {
            mutableTraces.Add("Profile enforcement: final response reviewed against the complete active profile.");
        }
        return enforced with { Usage = AddUsage(draft.Usage, enforced.Usage) };
    }

    private static TokenUsage AddUsage(TokenUsage first, TokenUsage second) => new(
        first.CurrentRequestTokens + second.CurrentRequestTokens,
        first.HistoryTokens + second.HistoryTokens,
        first.ContextTokens + second.ContextTokens,
        first.PromptTokens + second.PromptTokens,
        first.CachedPromptTokens + second.CachedPromptTokens,
        first.CompletionTokens + second.CompletionTokens,
        first.BilledTokens + second.BilledTokens,
        first.TextCountsAreEstimated || second.TextCountsAreEstimated);

    private void EnsureInitialized()
    {
        if (!_isInitialized) throw new InvalidOperationException("Агент не инициализирован.");
    }
}
