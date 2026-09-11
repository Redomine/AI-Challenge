using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Storage;

namespace DesignerAssistant.Agent;

public sealed class DesignAssistantAgent : IDesignAssistantAgent
{
    private readonly ILlmClient _llmClient;
    private readonly IChatHistoryStore _historyStore;
    private readonly string _instructions;
    private readonly List<ChatMessage> _history = [];
    private bool _isInitialized;

    public DesignAssistantAgent(
        ILlmClient llmClient,
        IChatHistoryStore historyStore,
        string instructions)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _instructions = string.IsNullOrWhiteSpace(instructions)
            ? throw new ArgumentException("Системная инструкция не задана.", nameof(instructions))
            : instructions;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        await _historyStore.InitializeAsync(cancellationToken);
        _history.AddRange(await _historyStore.LoadAsync(cancellationToken));
        _isInitialized = true;
    }

    public async Task<string> AskAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("Сообщение не должно быть пустым.", nameof(userMessage));
        }

        var pendingHistory = new List<ChatMessage>(_history)
        {
            new("user", userMessage.Trim())
        };

        var answer = await _llmClient.GenerateAsync(
            _instructions,
            pendingHistory,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(answer))
        {
            throw new InvalidOperationException("Модель вернула пустой ответ.");
        }

        var completedMessages = new[]
        {
            pendingHistory[^1],
            new ChatMessage("assistant", answer)
        };
        await _historyStore.AppendAsync(completedMessages, cancellationToken);
        _history.AddRange(completedMessages);

        return answer;
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _historyStore.ClearAsync(cancellationToken);
        _history.Clear();
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Агент не инициализирован.");
        }
    }
}
