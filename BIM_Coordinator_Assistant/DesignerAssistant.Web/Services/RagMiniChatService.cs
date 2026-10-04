using System.Text;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Параметры окна истории, которое подаётся в RAG как контекст
/// для очередного вопроса. Полная история никогда не теряется,
/// но в промпт уходит только последние <c>MaxTurns</c> реплик
/// (вопрос + ответ парой), плюс goal/clarifications/terms.
/// </summary>
public sealed record RagMiniChatWindowOptions(
    int MaxTurns = 6,
    int MaxGoalChars = 1000,
    int MaxClarificationsChars = 1000,
    int MaxTermsChars = 500)
{
    public static RagMiniChatWindowOptions Default { get; } = new();

    public void Validate()
    {
        if (MaxTurns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTurns),
                "MaxTurns должен быть положительным.");
        }
        if (MaxGoalChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxGoalChars),
                "MaxGoalChars должен быть положительным.");
        }
        if (MaxClarificationsChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxClarificationsChars),
                "MaxClarificationsChars должен быть положительным.");
        }
        if (MaxTermsChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxTermsChars),
                "MaxTermsChars должен быть положительным.");
        }
    }
}

/// <summary>
/// Состояние задачи для одной беседы: цель, уточнения, термины/ограничения.
/// Это «память намерения» — она дополняет историю реплик и не теряется
/// между сообщениями. Лимиты длины применяются при сборке контекста, а не
/// при записи: фактические значения сохраняются целиком.
/// </summary>
public sealed class RagMiniChatTaskState
{
    public string Goal { get; private set; } = "";
    public List<string> Clarifications { get; } = new();
    public List<string> Terms { get; } = new();

    public void SetGoal(string goal)
    {
        Goal = goal ?? "";
    }

    public void AddClarification(string clarification)
    {
        if (string.IsNullOrWhiteSpace(clarification)) return;
        if (!Clarifications.Contains(clarification.Trim(), StringComparer.OrdinalIgnoreCase))
            Clarifications.Add(clarification.Trim());
    }

    public void AddTerms(IEnumerable<string> terms)
    {
        if (terms is null) return;
        foreach (var term in terms)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            if (!Terms.Contains(term.Trim(), StringComparer.OrdinalIgnoreCase))
                Terms.Add(term.Trim());
        }
    }

    public void Reset()
    {
        Goal = "";
        Clarifications.Clear();
        Terms.Clear();
    }
}

/// <summary>
/// Одна реплика (turn) в мини-чате. Сохраняется в памяти беседы
/// целиком, но в контекст для следующего вопроса попадают только
/// последние N реплик согласно <see cref="RagMiniChatWindowOptions"/>.
/// <para><c>EnrichedQuestion</c> — фактический текст, отправленный
/// в <see cref="RagQueryService.QueryAsync"/>, с приклеенным
/// контекстом (goal/clarifications/terms/предыдущие реплики).</para>
/// </summary>
public sealed record RagMiniChatTurn(
    string UserQuestion,
    string EnrichedQuestion,
    RagQueryResult Result,
    DateTimeOffset AskedAt);

/// <summary>
/// Сводка по сессии: фактические значения из истории и состояния.
/// </summary>
public sealed record RagMiniChatSession(
    string ConversationId,
    RagMiniChatTaskState State,
    IReadOnlyList<RagMiniChatTurn> Turns)
{
    public int TurnCount => Turns.Count;
}

/// <summary>
/// Мини-чат с RAG: отдельная беседа на каждый <c>ConversationId</c>.
/// <list type="bullet">
/// <item>Все реплики сохраняются в памяти целиком (unbounded history).</item>
/// <item>В <see cref="RagQueryService.QueryAsync"/> уходит контекст —
/// goal, уточнения, термины/ограничения и окно из последних N реплик.</item>
/// <item>Если поиск не нашёл подтверждающих фактов, сервис
/// возвращает <see cref="RagQueryResult"/> с явным abstention
/// (<c>Abstained=true</c>, <c>Answer="Не знаю"</c>) — ничего не
/// выдумывается.</item>
/// <item>Поддерживает <see cref="ResetAsync"/> для очистки одной
/// беседы и <see cref="ResetAllAsync"/> для очистки всех.</item>
/// </list>
/// <para>Сервис намеренно не хранит данные на диске и не модифицирует
/// <see cref="RagQueryService"/> или его настройки: между беседами
/// изоляция по <c>ConversationId</c>, общие ресурсы — только
/// фасад и его зависимости.</para>
/// </summary>
public sealed class RagMiniChatService
{
    private const string ContextSeparator = "\n\n";
    private const string HistoryHeader = "История беседы (только для контекста, не цитировать дословно):";
    private const string GoalHeader = "Цель беседы:";
    private const string ClarificationsHeader = "Уточнения пользователя:";
    private const string TermsHeader = "Термины и ограничения (использовать в поисковом запросе):";

    private readonly RagQueryService _ragQuery;
    private readonly RagMiniChatWindowOptions _window;
    private readonly object _lock = new();
    private readonly Dictionary<string, ConversationState> _conversations = new(StringComparer.Ordinal);

    public RagMiniChatService(RagQueryService ragQuery, RagMiniChatWindowOptions? window = null)
    {
        ArgumentNullException.ThrowIfNull(ragQuery);
        _ragQuery = ragQuery;
        _window = window ?? RagMiniChatWindowOptions.Default;
        _window.Validate();
    }

    /// <summary>Текущие настройки окна контекста.</summary>
    public RagMiniChatWindowOptions Window => _window;

    /// <summary>
    /// Задать цель беседы. Сохраняется в памяти и подмешивается
    /// в контекст каждого следующего вопроса. Заменяет предыдущее
    /// значение целиком.
    /// </summary>
    public void SetGoal(string conversationId, string goal)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("ConversationId обязателен.", nameof(conversationId));
        }
        lock (_lock)
        {
            var state = GetOrCreate(conversationId);
            state.State.SetGoal(goal ?? "");
        }
    }

    /// <summary>Добавить уточнение от пользователя (например, ответ на clarification_question).</summary>
    public void AddClarification(string conversationId, string clarification)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("ConversationId обязателен.", nameof(conversationId));
        }
        lock (_lock)
        {
            var state = GetOrCreate(conversationId);
            state.State.AddClarification(clarification);
        }
    }

    /// <summary>
    /// Добавить термины/ограничения, которые нужно учитывать в поиске.
    /// Повторы не схлопываются — полная история сохраняется в <see cref="TaskState"/>.
    /// </summary>
    public void AddTerms(string conversationId, IEnumerable<string> terms)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("ConversationId обязателен.", nameof(conversationId));
        }
        lock (_lock)
        {
            var state = GetOrCreate(conversationId);
            state.State.AddTerms(terms);
        }
    }

    public void RemoveClarification(string conversationId, int index)
    {
        lock (_lock)
        {
            var items = GetOrCreate(conversationId).State.Clarifications;
            if (index >= 0 && index < items.Count) items.RemoveAt(index);
        }
    }

    public void RemoveTerm(string conversationId, int index)
    {
        lock (_lock)
        {
            var items = GetOrCreate(conversationId).State.Terms;
            if (index >= 0 && index < items.Count) items.RemoveAt(index);
        }
    }

    /// <summary>
    /// Получить снимок сессии: состояние и все реплики. Никаких
    /// побочных эффектов — список копируется, чтобы вызывающий
    /// код не мог мутировать внутреннее состояние.
    /// </summary>
    public RagMiniChatSession GetSession(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("ConversationId обязателен.", nameof(conversationId));
        }
        lock (_lock)
        {
            if (!_conversations.TryGetValue(conversationId, out var state))
            {
                return new RagMiniChatSession(
                    conversationId,
                    new RagMiniChatTaskState(),
                    Array.Empty<RagMiniChatTurn>());
            }
            return new RagMiniChatSession(
                conversationId,
                CloneState(state.State),
                state.Turns.ToArray());
        }
    }

    /// <summary>
    /// Главный метод: задать вопрос в контексте беседы. Полная
    /// история реплик сохраняется, но в <see cref="RagQueryService"/>
    /// уходит контекстное окно из последних <c>MaxTurns</c> реплик
    /// плюс goal/clarifications/terms. Если поиск не дал подтверждающих
    /// фактов — возвращается <see cref="RagQueryResult"/> с явным
    /// abstention (UI не должен показывать «уверенный» ответ).
    /// </summary>
    public async Task<RagMiniChatTurn> AskAsync(
        string conversationId,
        string question,
        RagMode mode = RagMode.Rag,
        RagRunSettings? overrideSettings = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("ConversationId обязателен.", nameof(conversationId));
        }
        ArgumentNullException.ThrowIfNull(question);
        if (mode == RagMode.NoRag)
            throw new ArgumentException("Мини-чат требует RAG-поиск для каждого вопроса.", nameof(mode));
        if (string.IsNullOrWhiteSpace(question))
        {
            // Пустой вопрос пользователя не должен идти в RAG: иначе поиск
            // отработает по «голому» контексту и вернёт мусорный результат.
            // Возвращаем явный abstention-turn, чтобы UI показывал «Не знаю».
            var emptyTurn = new RagMiniChatTurn(
                UserQuestion: question,
                EnrichedQuestion: "",
                Result: new RagQueryResult(
                    question ?? "",
                    mode,
                    RagAbstentionMessages.Russian,
                    false,
                    0,
                    Array.Empty<RagSource>(),
                    "Пустой вопрос.",
                    null,
                    null,
                    Array.Empty<RagCitation>(),
                    true,
                    RagAbstentionReason.NoEvidence,
                    RagAbstentionMessages.DefaultClarification),
                AskedAt: DateTimeOffset.UtcNow);
            lock (_lock)
            {
                var state = GetOrCreate(conversationId);
                state.Turns.Add(emptyTurn);
            }
            return emptyTurn;
        }

        RagMiniChatTaskState stateSnapshot;
        IReadOnlyList<RagMiniChatTurn> historySnapshot;
        lock (_lock)
        {
            var state = GetOrCreate(conversationId);
            CaptureTaskState(state.State, question);
            stateSnapshot = CloneState(state.State);
            historySnapshot = state.Turns.ToArray();
        }

        if (IsTaskUpdate(question))
        {
            var update = new RagMiniChatTurn(question, "",
                new RagQueryResult(question, RagMode.Rag,
                    "Принял уточнение и учту его в следующих ответах.",
                    false, 0, Array.Empty<RagSource>(), null), DateTimeOffset.UtcNow);
            lock (_lock) GetOrCreate(conversationId).Turns.Add(update);
            return update;
        }

        var enriched = BuildContextEnrichedQuestion(
            stateSnapshot,
            historySnapshot,
            question);

        var searchQuestion = BuildSearchQuestion(historySnapshot, question);
        var result = await _ragQuery
            .QueryAsync(searchQuestion, mode, overrideSettings, enriched, cancellationToken)
            .ConfigureAwait(false);

        // Дополнительная страховка: если результат пустой/ошибочный и не
        // помечен как abstention, добавляем явный abstention. Это
        // страхует от потенциальных регрессий фасада в будущем.
        var normalizedResult = EnsureAbstention(result);

        var turn = new RagMiniChatTurn(
            UserQuestion: question,
            EnrichedQuestion: enriched,
            Result: normalizedResult,
            AskedAt: DateTimeOffset.UtcNow);

        lock (_lock)
        {
            var state = GetOrCreate(conversationId);
            state.Turns.Add(turn);
        }
        return turn;
    }

    /// <summary>
    /// Сбросить состояние и историю одной беседы. Полезно для
    /// тестов и для UI-кнопки «новая беседа».
    /// </summary>
    public void Reset(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw new ArgumentException("ConversationId обязателен.", nameof(conversationId));
        }
        lock (_lock)
        {
            _conversations.Remove(conversationId);
        }
    }

    /// <summary>Сбросить все беседы.</summary>
    public void ResetAll()
    {
        lock (_lock)
        {
            _conversations.Clear();
        }
    }

    /// <summary>
    /// Список известных идентификаторов бесед. Только для отладки/тестов:
    /// возвращает копию, чтобы вызывающий код не мог мутировать
    /// внутреннее состояние.
    /// </summary>
    public IReadOnlyList<string> ListConversations()
    {
        lock (_lock)
        {
            return _conversations.Keys.ToArray();
        }
    }

    /// <summary>
    /// Собирает контекстно-обогащённый вопрос: goal, clarifications, terms
    /// и последние <c>MaxTurns</c> реплик беседы. Оригинальный вопрос
    /// пользователя идёт последним — это то, на что модель должна ответить.
    /// </summary>
    internal string BuildContextEnrichedQuestion(
        RagMiniChatTaskState state,
        IReadOnlyList<RagMiniChatTurn> history,
        string userQuestion)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(userQuestion);

        var sb = new StringBuilder();

        var goal = Truncate(state.Goal, _window.MaxGoalChars);
        if (!string.IsNullOrWhiteSpace(goal))
        {
            sb.Append(GoalHeader).Append(' ').AppendLine(goal);
            sb.AppendLine();
        }

        var clarifications = TruncateList(state.Clarifications, _window.MaxClarificationsChars);
        if (clarifications.Count > 0)
        {
            sb.AppendLine(ClarificationsHeader);
            foreach (var c in clarifications)
            {
                sb.Append("- ").AppendLine(c);
            }
            sb.AppendLine();
        }

        var terms = TruncateList(state.Terms, _window.MaxTermsChars);
        if (terms.Count > 0)
        {
            sb.AppendLine(TermsHeader);
            sb.AppendLine(string.Join(", ", terms));
            sb.AppendLine();
        }

        var window = history.Count > _window.MaxTurns
            ? history.Skip(history.Count - _window.MaxTurns).ToArray()
            : (IReadOnlyList<RagMiniChatTurn>)history;
        if (window.Count > 0)
        {
            sb.AppendLine(HistoryHeader);
            for (var i = 0; i < window.Count; i++)
            {
                var t = window[i];
                sb.Append("[Q").Append(i + 1).Append("] ").AppendLine(t.UserQuestion);
                var answerLine = t.Result.Abstained
                    ? (string.IsNullOrWhiteSpace(t.Result.ClarificationQuestion)
                        ? RagAbstentionMessages.Russian
                        : $"{RagAbstentionMessages.Russian} ({t.Result.ClarificationQuestion})")
                    : t.Result.Answer;
                sb.Append("[A").Append(i + 1).Append("] ").AppendLine(answerLine);
            }
            sb.AppendLine();
        }

        sb.Append("Текущий вопрос пользователя: ").AppendLine(userQuestion);
        return sb.ToString();
    }

    private static string BuildSearchQuestion(
        IReadOnlyList<RagMiniChatTurn> history,
        string question)
    {
        var current = question.Trim();
        if (history.Count == 0 || current.Any(char.IsDigit) ||
            !(current.StartsWith("А ", StringComparison.OrdinalIgnoreCase) ||
              current.StartsWith("И ", StringComparison.OrdinalIgnoreCase) ||
              current.StartsWith("А для ", StringComparison.OrdinalIgnoreCase)))
            return current;
        var previous = history[^1].UserQuestion.Trim();
        if (previous.Length == 0) return current;
        return $"{current} {previous}";
    }

    private static RagMiniChatTaskState CloneState(RagMiniChatTaskState src)
    {
        var copy = new RagMiniChatTaskState();
        copy.SetGoal(src.Goal);
        foreach (var c in src.Clarifications)
        {
            copy.AddClarification(c);
        }
        copy.AddTerms(src.Terms);
        return copy;
    }

    private static void CaptureTaskState(RagMiniChatTaskState state, string question)
    {
        var text = question.Trim();
        if (string.IsNullOrEmpty(state.Goal) || text.StartsWith("Цель:", StringComparison.OrdinalIgnoreCase))
            state.SetGoal(text.StartsWith("Цель:", StringComparison.OrdinalIgnoreCase) ? text[5..].Trim() : text);
        if (text.StartsWith("Уточнение:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Уточняю:", StringComparison.OrdinalIgnoreCase))
            state.AddClarification(text[(text.IndexOf(':') + 1)..].Trim());
        if (text.StartsWith("Ограничение:", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Термин:", StringComparison.OrdinalIgnoreCase))
            state.AddTerms([text[(text.IndexOf(':') + 1)..].Trim()]);
    }

    private static bool IsTaskUpdate(string question) =>
        !question.Contains('?') &&
        (question.TrimStart().StartsWith("Уточняю:", StringComparison.OrdinalIgnoreCase) ||
         question.TrimStart().StartsWith("Уточнение:", StringComparison.OrdinalIgnoreCase) ||
         question.TrimStart().StartsWith("Ограничение:", StringComparison.OrdinalIgnoreCase) ||
         question.TrimStart().StartsWith("Термин:", StringComparison.OrdinalIgnoreCase));

    private ConversationState GetOrCreate(string conversationId)
    {
        if (_conversations.TryGetValue(conversationId, out var existing))
            return existing;
        var fresh = new ConversationState();
        _conversations[conversationId] = fresh;
        return fresh;
    }

    private static string Truncate(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= maxChars) return s;
        return s[..maxChars];
    }

    private static IReadOnlyList<string> TruncateList(IReadOnlyList<string> items, int maxChars)
    {
        if (items.Count == 0) return items;
        var sb = new StringBuilder();
        var kept = new List<string>(items.Count);
        foreach (var raw in items)
        {
            if (sb.Length >= maxChars) break;
            var piece = (raw ?? "").Trim();
            if (piece.Length == 0) continue;
            var budget = maxChars - sb.Length;
            if (piece.Length > budget)
            {
                piece = piece[..Math.Max(0, budget)];
            }
            if (piece.Length == 0) break;
            kept.Add(piece);
            sb.Append(piece).Append('\n');
        }
        return kept;
    }

    /// <summary>
    /// Гарантирует, что пустой/ошибочный ответ без abstention получит
    /// явный abstention-флажок. UI не должен показывать «уверенный»
    /// текст без источников.
    /// </summary>
    private static RagQueryResult EnsureAbstention(RagQueryResult result)
    {
        if (result.Abstained) return result;
        if (result.SearchSucceeded && !string.IsNullOrWhiteSpace(result.Answer)
            && result.CitationsSafe.Any(c => c.Verified))
        {
            return result;
        }
        // Поиск не удался или нет ответа — нормализуем к abstention.
        var reason = result.SearchSucceeded
            ? RagAbstentionReason.NoEvidence
            : (result.Error is not null ? RagAbstentionReason.SearchError : RagAbstentionReason.NoEvidence);
        var clarification = string.IsNullOrWhiteSpace(result.ClarificationQuestion)
            ? RagAbstentionMessages.DefaultClarification
            : result.ClarificationQuestion;
        return result with
        {
            Abstained = true,
            AbstentionReason = reason,
            Answer = RagAbstentionMessages.Russian,
            ClarificationQuestion = clarification,
        };
    }

    private sealed class ConversationState
    {
        public RagMiniChatTaskState State { get; } = new();
        public List<RagMiniChatTurn> Turns { get; } = new();
    }
}
