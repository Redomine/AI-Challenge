using System.Text;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Storage;

namespace DesignerAssistant.Agent;

public sealed class DesignAssistantAgent : IDesignAssistantAgent
{
    private readonly ILlmClient _llmClient;
    private readonly IChatHistoryStore _historyStore;
    private readonly string _instructions;
    private readonly int _recentMessageCount;
    private readonly int _summaryBatchSize;
    private readonly List<ChatMessage> _history = [];
    private CompressionState _compressionState = new(true, string.Empty, 0);
    private bool _isInitialized;

    public DesignAssistantAgent(
        ILlmClient llmClient,
        IChatHistoryStore historyStore,
        string instructions,
        AppOptions options)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _instructions = string.IsNullOrWhiteSpace(instructions)
            ? throw new ArgumentException("Системная инструкция не задана.", nameof(instructions))
            : instructions;
        _recentMessageCount = options.RecentMessageCount;
        _summaryBatchSize = options.SummaryBatchSize;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        await _historyStore.InitializeAsync(cancellationToken);
        _history.AddRange(await _historyStore.LoadAsync(cancellationToken));
        _compressionState = await _historyStore.LoadCompressionStateAsync(cancellationToken);

        if (_compressionState.SummarizedMessageCount > _history.Count)
        {
            _compressionState = _compressionState with
            {
                Summary = string.Empty,
                SummarizedMessageCount = 0
            };
            await _historyStore.SaveCompressionStateAsync(
                _compressionState,
                cancellationToken);
        }

        _isInitialized = true;
    }

    public async Task<AgentResponse> AskAsync(
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            throw new ArgumentException("Сообщение не должно быть пустым.", nameof(userMessage));
        }

        var messagesForModel = BuildMessagesForModel();
        messagesForModel.Add(new ChatMessage("user", userMessage.Trim()));
        var summaryForRequest = _compressionState.IsEnabled
            ? _compressionState.Summary
            : string.Empty;
        var instructionsForModel = BuildInstructionsForModel();

        var response = await _llmClient.GenerateAsync(
            instructionsForModel,
            messagesForModel,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(response.Content))
        {
            throw new InvalidOperationException("Модель вернула пустой ответ.");
        }

        var completedMessages = new[]
        {
            messagesForModel[^1],
            new ChatMessage("assistant", response.Content)
        };
        await _historyStore.AppendAsync(completedMessages, cancellationToken);
        _history.AddRange(completedMessages);

        var sentHistoryTexts = messagesForModel
            .Select(message => message.Content)
            .Append(response.Content)
            .ToList();
        if (!string.IsNullOrWhiteSpace(summaryForRequest))
        {
            sentHistoryTexts.Add(summaryForRequest);
        }

        var sentHistoryCount = await _llmClient.CountTextTokensAsync(
            sentHistoryTexts,
            cancellationToken);

        var fullHistoryCount = await _llmClient.CountTextTokensAsync(
            _history.Select(message => message.Content).ToArray(),
            cancellationToken);
        var compressionBilledTokens = await CompressIfNeededAsync(cancellationToken);

        return new AgentResponse(
            response,
            _compressionState.IsEnabled,
            fullHistoryCount.TotalTokens,
            fullHistoryCount.IsEstimated,
            sentHistoryCount.TotalTokens,
            _compressionState.SummarizedMessageCount,
            _history.Count - _compressionState.SummarizedMessageCount,
            compressionBilledTokens);
    }

    public async Task<CompressionStatus> SetCompressionEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        _compressionState = _compressionState with { IsEnabled = isEnabled };
        await _historyStore.SaveCompressionStateAsync(_compressionState, cancellationToken);

        var billedTokens = isEnabled
            ? await CompressIfNeededAsync(cancellationToken)
            : 0;
        return CreateCompressionStatus(billedTokens);
    }

    public CompressionStatus GetCompressionStatus()
    {
        EnsureInitialized();
        return CreateCompressionStatus(0);
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _historyStore.ClearAsync(cancellationToken);
        _history.Clear();
        _compressionState = _compressionState with
        {
            Summary = string.Empty,
            SummarizedMessageCount = 0
        };
    }

    private async Task<int> CompressIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!_compressionState.IsEnabled)
        {
            return 0;
        }

        var billedTokens = 0;
        while (_history.Count - _compressionState.SummarizedMessageCount -
               _recentMessageCount >= _summaryBatchSize)
        {
            var batch = _history
                .Skip(_compressionState.SummarizedMessageCount)
                .Take(_summaryBatchSize)
                .ToArray();
            var summaryRequest = BuildSummaryRequest(batch);
            var summaryResponse = await _llmClient.GenerateAsync(
                HistorySummaryPrompt.Text,
                [new ChatMessage("user", summaryRequest)],
                cancellationToken);

            billedTokens += summaryResponse.Usage.BilledTokens;
            _compressionState = _compressionState with
            {
                Summary = summaryResponse.Content,
                SummarizedMessageCount =
                    _compressionState.SummarizedMessageCount + batch.Length
            };
            await _historyStore.SaveCompressionStateAsync(
                _compressionState,
                cancellationToken);
        }

        return billedTokens;
    }

    private List<ChatMessage> BuildMessagesForModel() =>
        !_compressionState.IsEnabled
            ? new List<ChatMessage>(_history)
            : _history.Skip(_compressionState.SummarizedMessageCount).ToList();

    private string BuildInstructionsForModel()
    {
        if (!_compressionState.IsEnabled || string.IsNullOrWhiteSpace(_compressionState.Summary))
        {
            return _instructions;
        }

        return $"{_instructions}\n\nКраткое содержание ранней истории:\n{_compressionState.Summary}";
    }

    private string BuildSummaryRequest(IReadOnlyCollection<ChatMessage> batch)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Текущее краткое содержание:");
        builder.AppendLine(string.IsNullOrWhiteSpace(_compressionState.Summary)
            ? "(пока отсутствует)"
            : _compressionState.Summary);
        builder.AppendLine();
        builder.AppendLine("Следующие сообщения для добавления:");

        foreach (var message in batch)
        {
            builder.Append(message.Role).Append(": ").AppendLine(message.Content);
        }

        return builder.ToString();
    }

    private CompressionStatus CreateCompressionStatus(int billedTokens) => new(
        _compressionState.IsEnabled,
        _compressionState.SummarizedMessageCount,
        _history.Count - _compressionState.SummarizedMessageCount,
        billedTokens);

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Агент не инициализирован.");
        }
    }
}
