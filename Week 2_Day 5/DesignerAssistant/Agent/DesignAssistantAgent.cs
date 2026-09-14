using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
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
    private readonly List<ChatMessage> _history = [];
    private ContextState _state = ContextState.CreateDefault();
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
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        await _historyStore.InitializeAsync(cancellationToken);
        _history.AddRange(await _historyStore.LoadAsync(cancellationToken));
        _state = await _historyStore.LoadContextStateAsync(cancellationToken);
        EnsureBranchState();
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

        var trimmedMessage = userMessage.Trim();
        var memoryBilledTokens = 0;
        if (_state.Strategy == ContextStrategy.StickyFacts)
        {
            memoryBilledTokens = await UpdateFactsAsync(trimmedMessage, cancellationToken);
        }

        var sourceHistory = GetActiveHistory();
        var messagesForModel = BuildMessagesForModel(sourceHistory);
        messagesForModel.Add(new ChatMessage("user", trimmedMessage));
        var factsBlock = BuildFactsBlock();
        var instructions = string.IsNullOrEmpty(factsBlock)
            ? _instructions
            : $"{_instructions}\n\nДолговременные facts:\n{factsBlock}";

        var response = await _llmClient.GenerateAsync(
            instructions,
            messagesForModel,
            cancellationToken);
        var completedMessages = new[]
        {
            messagesForModel[^1],
            new ChatMessage("assistant", response.Content)
        };
        await SaveCompletedMessagesAsync(completedMessages, cancellationToken);

        var activeHistory = GetActiveHistory();
        var fullCount = await _llmClient.CountTextTokensAsync(
            activeHistory.Select(message => message.Content).ToArray(),
            cancellationToken);
        var sentTexts = messagesForModel
            .Select(message => message.Content)
            .Append(response.Content)
            .ToList();
        if (!string.IsNullOrEmpty(factsBlock))
        {
            sentTexts.Add(factsBlock);
        }

        var sentCount = await _llmClient.CountTextTokensAsync(
            sentTexts,
            cancellationToken);

        return new AgentResponse(
            response,
            _state.Strategy,
            fullCount.TotalTokens,
            fullCount.IsEstimated,
            sentCount.TotalTokens,
            _state.Facts.Count,
            _state.ActiveBranch,
            memoryBilledTokens);
    }

    public async Task<ContextStatus> SetStrategyAsync(
        ContextStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        if (_state.Strategy == strategy)
        {
            return GetContextStatus();
        }

        if (_state.Strategy == ContextStrategy.Branching)
        {
            _history.Clear();
            _history.AddRange(GetActiveHistory());
            await _historyStore.ReplaceAsync(_history, cancellationToken);
        }

        _state = _state with { Strategy = strategy };
        if (strategy == ContextStrategy.Branching)
        {
            EnsureBranchState(resetMainBranch: true);
        }

        await _historyStore.SaveContextStateAsync(_state, cancellationToken);
        return GetContextStatus();
    }

    public async Task<ContextStatus> CreateCheckpointAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureBranching();
        _state = _state with
        {
            CheckpointMessages = new List<ChatMessage>(GetActiveHistory())
        };
        await _historyStore.SaveContextStateAsync(_state, cancellationToken);
        return GetContextStatus();
    }

    public async Task<ContextStatus> CreateBranchAsync(
        string branchName,
        CancellationToken cancellationToken = default)
    {
        EnsureBranching();
        branchName = NormalizeBranchName(branchName);
        if (_state.Branches.ContainsKey(branchName))
        {
            throw new InvalidOperationException($"Ветка '{branchName}' уже существует.");
        }

        var branches = CloneBranches();
        branches[branchName] = new List<ChatMessage>(_state.CheckpointMessages);
        _state = _state with { Branches = branches, ActiveBranch = branchName };
        await _historyStore.SaveContextStateAsync(_state, cancellationToken);
        return GetContextStatus();
    }

    public async Task<ContextStatus> SwitchBranchAsync(
        string branchName,
        CancellationToken cancellationToken = default)
    {
        EnsureBranching();
        branchName = NormalizeBranchName(branchName);
        if (!_state.Branches.ContainsKey(branchName))
        {
            throw new InvalidOperationException($"Ветка '{branchName}' не найдена.");
        }

        _state = _state with { ActiveBranch = branchName };
        await _historyStore.SaveContextStateAsync(_state, cancellationToken);
        return GetContextStatus();
    }

    public ContextStatus GetContextStatus()
    {
        EnsureInitialized();
        return new ContextStatus(
            _state.Strategy,
            _state.Facts.Count,
            _state.ActiveBranch,
            _state.Branches.Keys.OrderBy(name => name).ToArray(),
            GetActiveHistory().Count);
    }

    public async Task ClearHistoryAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _historyStore.ClearAsync(cancellationToken);
        _history.Clear();
        _state = ContextState.CreateDefault();
    }

    private async Task<int> UpdateFactsAsync(
        string userMessage,
        CancellationToken cancellationToken)
    {
        var currentFacts = JsonSerializer.Serialize(_state.Facts);
        var request = $"Текущие facts:\n{currentFacts}\n\nНовое сообщение:\n{userMessage}";
        var billedTokens = 0;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await _llmClient.GenerateAsync(
                FactsExtractionPrompt.Text,
                [new ChatMessage("user", request)],
                cancellationToken);
            billedTokens += response.Usage.BilledTokens;

            try
            {
                var facts = ParseFacts(response.Content);
                _state = _state with { Facts = facts };
                await _historyStore.SaveContextStateAsync(_state, cancellationToken);
                return billedTokens;
            }
            catch (JsonException) when (attempt == 0)
            {
                request = $"""
                    Исправь следующий невалидный ответ. Верни только обычный
                    JSON-объект без Markdown и без экранирования всего объекта:
                    {response.Content}
                    """;
            }
            catch (JsonException)
            {
                return billedTokens;
            }
        }

        return billedTokens;
    }

    private async Task SaveCompletedMessagesAsync(
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        if (_state.Strategy == ContextStrategy.Branching)
        {
            var branches = CloneBranches();
            branches[_state.ActiveBranch].AddRange(messages);
            _state = _state with { Branches = branches };
            await _historyStore.SaveContextStateAsync(_state, cancellationToken);
            return;
        }

        _history.AddRange(messages);
        await _historyStore.AppendAsync(messages, cancellationToken);
    }

    private List<ChatMessage> BuildMessagesForModel(IReadOnlyList<ChatMessage> history) =>
        _state.Strategy == ContextStrategy.Branching
            ? new List<ChatMessage>(history)
            : history.TakeLast(_recentMessageCount).ToList();

    private IReadOnlyList<ChatMessage> GetActiveHistory()
    {
        if (_state.Strategy != ContextStrategy.Branching)
        {
            return _history;
        }

        EnsureBranchState();
        return _state.Branches[_state.ActiveBranch];
    }

    private string BuildFactsBlock() =>
        _state.Strategy != ContextStrategy.StickyFacts || _state.Facts.Count == 0
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                _state.Facts.OrderBy(pair => pair.Key)
                    .Select(pair => $"- {pair.Key}: {pair.Value}"));

    private void EnsureBranchState(bool resetMainBranch = false)
    {
        if (!resetMainBranch && _state.Branches.ContainsKey(_state.ActiveBranch))
        {
            return;
        }

        var branches = resetMainBranch
            ? new Dictionary<string, List<ChatMessage>>(StringComparer.OrdinalIgnoreCase)
            : CloneBranches();
        branches["main"] = new List<ChatMessage>(_history);
        _state = _state with
        {
            ActiveBranch = "main",
            CheckpointMessages = new List<ChatMessage>(_history),
            Branches = branches
        };
    }

    private Dictionary<string, List<ChatMessage>> CloneBranches() =>
        _state.Branches.ToDictionary(
            pair => pair.Key,
            pair => new List<ChatMessage>(pair.Value),
            StringComparer.OrdinalIgnoreCase);

    private void EnsureBranching()
    {
        EnsureInitialized();
        if (_state.Strategy != ContextStrategy.Branching)
        {
            throw new InvalidOperationException(
                "Сначала включите стратегию branching: /strategy branching.");
        }
    }

    private static string NormalizeBranchName(string branchName)
    {
        branchName = branchName.Trim();
        if (!Regex.IsMatch(branchName, "^[A-Za-zА-Яа-я0-9_-]{1,30}$"))
        {
            throw new ArgumentException(
                "Имя ветки: от 1 до 30 букв, цифр, символов '_' или '-'.");
        }

        return branchName;
    }

    private static Dictionary<string, string> ParseFacts(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new JsonException("Модель не вернула JSON-объект facts.");
        }

        var rawJson = content[start..(end + 1)];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(EscapeInvalidJsonBackslashes(rawJson));
        }
        catch (JsonException) when (rawJson.Contains("\\\"", StringComparison.Ordinal))
        {
            var unwrappedJson = rawJson.Replace("\\\"", "\"");
            document = JsonDocument.Parse(EscapeInvalidJsonBackslashes(unwrappedJson));
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Facts должны быть представлены JSON-объектом.");
            }

            var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                facts[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }

            return facts;
        }
    }

    private static string EscapeInvalidJsonBackslashes(string json)
    {
        var result = new StringBuilder(json.Length);
        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (character != '\\')
            {
                result.Append(character);
                continue;
            }

            var hasNext = index + 1 < json.Length;
            var next = hasNext ? json[index + 1] : '\0';
            var isSimpleEscape = hasNext && "\"\\/bfnrt".Contains(next);
            var isUnicodeEscape = next == 'u' &&
                                  index + 5 < json.Length &&
                                  json.AsSpan(index + 2, 4).ToString()
                                      .All(Uri.IsHexDigit);

            if (isSimpleEscape)
            {
                result.Append(character).Append(next);
                index++;
            }
            else if (isUnicodeEscape)
            {
                result.Append(json, index, 6);
                index += 5;
            }
            else
            {
                result.Append("\\\\");
            }
        }

        return result.ToString();
    }

    private void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            throw new InvalidOperationException("Агент не инициализирован.");
        }
    }
}
