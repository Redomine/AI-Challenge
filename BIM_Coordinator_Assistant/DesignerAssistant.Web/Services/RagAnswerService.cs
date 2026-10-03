using System.Text;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Сервис ответа с RAG: эмбеддинг запроса → top-k по индексу →
/// контекст + вопрос → LLM (без инструментов Revit).
/// Поиск и LLM — отдельные шаги с собственными таймаутами.
/// При пустом/ошибочном поиске результат помечается как
/// «без подтверждённых источников», а не как «найдено».
/// Поддерживает расширенный режим: исходный вопрос переписывается
/// в короткий русский поисковый запрос (через тот же LLM),
/// эмбеддинг считается для переписанного запроса, но модель
/// отвечает на исходный вопрос. Если переписывание не удалось —
/// поиск идёт по оригинальному вопросу, ошибка уходит в трассу.
/// <para>День 24: ответ модели парсится как структурированный (ANSWER
/// + QUOTES); цитаты проверяются против <see cref="RagSource.Text"/>;
/// при отсутствии валидных цитат или неподдержанном фактами ответе
/// сервис abstentionит «Не знаю» и предлагает уточнить вопрос.</para>
/// </summary>
public sealed class RagAnswerService
{
    private const string Instructions = """
        Ты отвечаешь на вопрос пользователя по локальной базе знаний.
        Используй только то, что прямо указано в разделе «Контекст».
        Не выдумывай факты, страницы или источники.

        Формат (СТРОГО, без исключений):
        ANSWER: <краткий ответ по фактам из контекста>
        QUOTES:
        [chunk_id] source=<source> section="<section>" pdf_page=<page_or_-> quote="<дословный фрагмент из контекста>"
        ... (одна строка на каждую цитату)

        Правила:
        - chunk_id, source и section/pdf_page бери ИЗ метаданных коллекции (см. «Метаданные коллекции»).
        - quote — это ДОСЛОВНЫЙ фрагмент из контекста, не перефразирование и не суммаризация.
        - Для quote скопируй короткий непрерывный фрагмент (5–20 слов) из ОДНОГО чанка. Не соединяй предложения из разных мест и не убирай слова или знаки препинания внутри цитаты.
        - Если в контексте нет подтверждающих фактов — ответь ровно «Не знаю» (ANSWER) и оставь QUOTES пустым. Не выдумывай цитаты.
        - Не указывай версию Revit, если её нет в цитате.
        - Сохраняй кириллицу, имена проектов и идентификаторы дословно.
        """;

    private const string RewriteInstructions = """
        Ты переписываешь вопрос пользователя в короткий поисковый
        запрос на русском языке для эмбеддинга и поиска по локальной
        базе знаний. Сохрани смысл и ключевые сущности (имена,
        объекты, единицы, числа), убери разговорные обороты и
        уточнения, оставь только то, что поможет найти релевантный
        фрагмент. Верни одну строку без пояснений и без кавычек.
        """;

    private readonly RagOptions _options;
    private readonly OllamaEmbeddingsClient _embeddings;
    private readonly ILlmClient _llm;

    public RagAnswerService(RagOptions options, OllamaEmbeddingsClient embeddings, ILlmClient llm)
    {
        _options = options;
        _embeddings = embeddings;
        _llm = llm;
    }

    public RagOptions Options => _options;

    /// <summary>
    /// Прогон с дефолтными singleton-настройками. Для обратной
    /// совместимости и сценариев «использовать то, что в env».
    /// Эквивалентно baseline-вызову.
    /// </summary>
    public Task<RagAnswer> AskAsync(string question, CancellationToken cancellationToken) =>
        AskAsync(question, _options.ToBaselineSettings(), cancellationToken);

    /// <summary>
    /// Прогон с per-request настройками. Не мутирует singleton
    /// <see cref="RagOptions"/> — все параметры берутся из
    /// <paramref name="settings"/>. Используется UI-переключателем
    /// и автотестом для сравнения режимов на одних и тех же данных.
    /// </summary>
    public async Task<RagAnswer> AskAsync(
        string question,
        RagRunSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(question))
        {
            return new RagAnswer(question, "", false, 0, Array.Empty<RagSource>(), "Пустой вопрос.");
        }

        var originalQuestion = question.Trim();
        var trace = new RagSearchTrace(
            OriginalQuestion: originalQuestion,
            SearchQuery: originalQuestion,
            PreFilterCount: 0,
            PostFilterCount: 0,
            Threshold: settings.ScoreThreshold,
            PostFilterLimit: settings.PostFilterK,
            PreFilterScores: Array.Empty<float>(),
            Rejected: Array.Empty<RagRejectedChunk>());

        IReadOnlyList<RagSource> sources = Array.Empty<RagSource>();
        bool searchOk = false;
        string? searchError = null;
        string? rewriteError = null;
        string searchQuery = originalQuestion;

        try
        {
            if (settings.RewriteEnabled)
            {
                var rewrite = await TryRewriteAsync(originalQuestion, settings.RewriteTimeout, cancellationToken);
                if (rewrite.Rewritten && !string.IsNullOrWhiteSpace(rewrite.Text))
                {
                    searchQuery = rewrite.Text!;
                    trace = trace with { SearchQuery = searchQuery };
                }
                else
                {
                    rewriteError = rewrite.Error ?? "Переписывание не удалось.";
                }
            }

            using var reader = new StructuralIndexReader(_options.IndexPath);
            if (!string.Equals(reader.EmbedModel, _options.EmbedModel, StringComparison.Ordinal))
            {
                searchError = $"Модель индекса {reader.EmbedModel} не совпадает с {_options.EmbedModel}.";
            }
            else
            {
                var vec = await _embeddings.EmbedAsync(
                    searchQuery,
                    _options.EmbedModel,
                    cancellationToken);
                var preHits = reader.Search(vec, settings.PreFilterK);
                var filtered = ApplyScoreAndLimit(settings.PreFilterK, settings.PostFilterK, settings.ScoreThreshold, preHits);
                sources = filtered.Sources;
                trace = trace with
                {
                    PreFilterCount = filtered.PreFilterCount,
                    PostFilterCount = filtered.Sources.Count,
                    PreFilterScores = filtered.PreFilterScores,
                    Rejected = filtered.Rejected,
                };
                searchOk = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            searchError = ex.Message;
        }

        string rawAnswer;
        string? llmError = null;
        try
        {
            using var llmCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            llmCts.CancelAfter(_options.LlmTimeout);
            var prompt = BuildPrompt(originalQuestion, sources);
            var response = await _llm.GenerateAsync(
                Instructions,
                new[] { new ChatMessage("user", prompt) },
                llmCts.Token);
            rawAnswer = response.Content;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            llmError = $"LLM превысил таймаут {_options.LlmTimeout.TotalSeconds:N0} с.";
            rawAnswer = "";
        }
        catch (Exception ex)
        {
            llmError = ex.Message;
            rawAnswer = "";
        }

        // 1. Поиск не выполнен → abstention «Не знаю» + ошибка поиска.
        if (!searchOk)
        {
            var errorText = !string.IsNullOrWhiteSpace(searchError)
                ? searchError
                : "Поиск не выполнен.";
            return new RagAnswer(
                Question: originalQuestion,
                Answer: RagAbstentionMessages.Russian,
                SearchSucceeded: false,
                RetrievedCount: 0,
                Sources: Array.Empty<RagSource>(),
                Error: errorText,
                Trace: trace,
                Citations: Array.Empty<RagCitation>(),
                Abstained: true,
                AbstentionReason: RagAbstentionReason.SearchError,
                ClarificationQuestion: RagAbstentionMessages.DefaultClarification);
        }

        // 2. Поиск успешен, но post-filter пуст → «нет подтверждающих
        //    источников». Это не техническая ошибка: модель не получила
        //    контекст.
        if (sources.Count == 0)
        {
            return new RagAnswer(
                Question: originalQuestion,
                Answer: RagAbstentionMessages.Russian,
                SearchSucceeded: true,
                RetrievedCount: 0,
                Sources: Array.Empty<RagSource>(),
                Error: null,
                Trace: trace,
                Citations: Array.Empty<RagCitation>(),
                Abstained: true,
                AbstentionReason: RagAbstentionReason.NoEvidence,
                ClarificationQuestion: RagAbstentionMessages.DefaultClarification);
        }

        // 3. LLM не дал ответ → «Не знаю» + причина LlmError.
        if (llmError is not null || string.IsNullOrWhiteSpace(rawAnswer))
        {
            var errorText = llmError ?? "LLM не вернул ответ.";
            return new RagAnswer(
                Question: originalQuestion,
                Answer: RagAbstentionMessages.Russian,
                SearchSucceeded: true,
                RetrievedCount: sources.Count,
                Sources: sources,
                Error: errorText,
                Trace: trace,
                Citations: Array.Empty<RagCitation>(),
                Abstained: true,
                AbstentionReason: RagAbstentionReason.LlmError,
                ClarificationQuestion: RagAbstentionMessages.DefaultClarification);
        }

        // 4. Парсим структурированный ответ и валидируем цитаты.
        var parsed = RagCitationParser.Parse(rawAnswer);
        if (parsed.CleanAnswer.StartsWith(RagAbstentionMessages.Russian,
                StringComparison.OrdinalIgnoreCase))
        {
            return new RagAnswer(
                Question: originalQuestion,
                Answer: RagAbstentionMessages.Russian,
                SearchSucceeded: true,
                RetrievedCount: sources.Count,
                Sources: sources,
                Error: null,
                Trace: trace,
                Citations: Array.Empty<RagCitation>(),
                Abstained: true,
                AbstentionReason: RagAbstentionReason.NoEvidence,
                ClarificationQuestion: RagAbstentionMessages.DefaultClarification);
        }
        var validated = RagCitationParser.ValidateAgainstSources(parsed, sources);
        var verified = validated.VerifiedCitations;
        var answerSupported = RagCitationParser.AnswerSupportedByQuotes(validated.CleanAnswer, verified);

        if (verified.Count == 0 || !answerSupported)
        {
            // Модель не привела валидных цитат или её ответ не
            // поддержан цитатами. Считаем ответ неподтверждённым и
            // abstentionим, чтобы UI не показывал «уверенный» ответ.
            var errorText = verified.Count == 0
                ? "Ответ не содержит валидных цитат."
                : "Ответ не подтверждён цитатами.";
            return new RagAnswer(
                Question: originalQuestion,
                Answer: RagAbstentionMessages.Russian,
                SearchSucceeded: true,
                RetrievedCount: sources.Count,
                Sources: sources,
                Error: errorText,
                Trace: trace,
                Citations: validated.Citations,
                Abstained: true,
                AbstentionReason: RagAbstentionReason.UnsupportedAnswer,
                ClarificationQuestion: RagAbstentionMessages.DefaultClarification);
        }

        // 5. Успех: есть валидные цитаты, ответ поддержан цитатами.
        var combinedError = (searchError, llmError, rewriteError) switch
        {
            (null, null, null) => null,
            _ => string.Join("; ",
                new[] { searchError, llmError, rewriteError }
                    .Where(s => !string.IsNullOrWhiteSpace(s)))
        };
        return new RagAnswer(
            Question: originalQuestion,
            Answer: string.IsNullOrWhiteSpace(validated.CleanAnswer) ? rawAnswer : validated.CleanAnswer,
            SearchSucceeded: true,
            RetrievedCount: sources.Count,
            Sources: sources,
            Error: combinedError,
            Trace: trace,
            Citations: validated.Citations,
            Abstained: false,
            AbstentionReason: RagAbstentionReason.None,
            ClarificationQuestion: null);
    }

    private async Task<(bool Rewritten, string? Text, string? Error)> TryRewriteAsync(
        string question, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);
            var response = await _llm.GenerateAsync(
                RewriteInstructions,
                new[] { new ChatMessage("user", question) },
                cts.Token);
            var cleaned = CleanRewrite(response.Content);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                return (false, null, "Переписывание вернуло пустой результат.");
            }
            return (true, cleaned, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Пользовательская отмена — пробрасываем наверх, никакого
            // тихого фоллбэка на оригинальный запрос.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Локальный таймаут — это не пользовательская отмена.
            return (false, null, $"Переписывание превысило таймаут {timeout.TotalSeconds:N0} с.");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static string CleanRewrite(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        // Убираем обрамляющие кавычки, если модель их добавила.
        if ((s.StartsWith('"') && s.EndsWith('"')) ||
            (s.StartsWith('«') && s.EndsWith('»')) ||
            (s.StartsWith('‘') && s.EndsWith('’')))
        {
            s = s.Substring(1, s.Length - 2).Trim();
        }
        // Берём только первую непустую строку — переписывание должно быть одной фразой.
        var firstLine = s.Split('\n', 2)[0].Trim();
        return firstLine;
    }

    private static FilterResult ApplyScoreAndLimit(
        int requestedPreFilterK,
        int requestedPostFilterK,
        float threshold,
        IReadOnlyList<RagSource> preHits)
    {
        var postLimit = requestedPostFilterK;
        if (postLimit > requestedPreFilterK)
        {
            // Защита: валидация гарантирует post <= pre, но не доверяем слепо.
            postLimit = requestedPreFilterK;
        }

        var rejected = new List<RagRejectedChunk>();
        var kept = new List<RagSource>(Math.Min(postLimit, preHits.Count));
        var scores = new List<float>(preHits.Count);

        foreach (var hit in preHits)
        {
            scores.Add(hit.Score);
            if (hit.Score < threshold)
            {
                rejected.Add(new RagRejectedChunk(
                    ChunkId: hit.ChunkId,
                    Source: hit.Source,
                    Title: hit.Title,
                    Section: hit.Section,
                    PdfPage: hit.PdfPage,
                    Score: hit.Score,
                    Reason: $"score {hit.Score:F4} < threshold {threshold:F4}"));
                continue;
            }
            if (kept.Count >= postLimit)
            {
                rejected.Add(new RagRejectedChunk(
                    ChunkId: hit.ChunkId,
                    Source: hit.Source,
                    Title: hit.Title,
                    Section: hit.Section,
                    PdfPage: hit.PdfPage,
                    Score: hit.Score,
                    Reason: $"превышен лимит post-filter K={postLimit}"));
                continue;
            }
            kept.Add(hit);
        }

        return new FilterResult(kept, rejected, scores);
    }

    private readonly record struct FilterResult(
        IReadOnlyList<RagSource> Sources,
        IReadOnlyList<RagRejectedChunk> Rejected,
        IReadOnlyList<float> PreFilterScores)
    {
        public int PreFilterCount => PreFilterScores.Count;
    }

    private string BuildPrompt(string question, IReadOnlyList<RagSource> sources)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Вопрос:");
        sb.AppendLine(question);
        sb.AppendLine();
        sb.AppendLine("Контекст (фрагменты из индекса; источник и страница — фактические метаданные):");
        if (sources.Count == 0)
        {
            sb.AppendLine("— фрагменты не найдены;");
        }
        else
        {
            // MaxContextChars — бюджет в символах (chars), как и говорит имя.
            // Считаем всё в chars, чтобы индексация текста (text[..N]) всегда
            // оставалась внутри строки, независимо от UTF-8 длины.
            var remainingChars = _options.MaxContextChars;
            for (var i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                var meta = s.Source == "pdf"
                    ? $"[{i + 1}] source={s.Source} pdf_page={s.PdfPage?.ToString() ?? "-"} chunk_id={s.ChunkId} section=\"{s.Section}\" title=\"{s.Title}\""
                    : $"[{i + 1}] source={s.Source} chunk_id={s.ChunkId} section=\"{s.Section}\" title=\"{s.Title}\"";
                var text = s.Text ?? "";
                // Заголовок строки + ":" + перевод строки AppendLine. meta — ASCII,
                // поэтому meta.Length совпадает с числом байт UTF-8; используем
                // .Length для согласованности с символьным бюджетом ниже.
                var headerLen = meta.Length + 2;
                // Clamp индекса строго в пределах длины текста: text[..N] валиден
                // только при 0 ≤ N ≤ text.Length. Это устраняет
                // "Index and length must refer to a location within the string"
                // для кириллицы/Unicode, где старый byte/char микс давал N > text.Length.
                var budget = Math.Min(text.Length, Math.Max(0, remainingChars - headerLen));
                text = text[..budget];
                remainingChars -= headerLen + text.Length;
                sb.Append(meta).AppendLine(":");
                sb.AppendLine(text);
                sb.AppendLine();
                if (remainingChars <= 0) break;
            }
        }
        sb.AppendLine();
        sb.AppendLine("Метаданные коллекции (для оформления цитат):");
        if (sources.Count == 0)
        {
            sb.AppendLine("— коллекция пуста; цитаты невозможны;");
        }
        else
        {
            for (var i = 0; i < sources.Count; i++)
            {
                var s = sources[i];
                var page = s.PdfPage?.ToString() ?? "-";
                sb.AppendLine($"[{s.ChunkId}] source={s.Source} section=\"{s.Section}\" pdf_page={page}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Дай короткий ответ по фактам из контекста, без выдуманных данных. Строго соблюдай формат ANSWER/QUOTES из инструкции.");
        return sb.ToString();
    }

    private static string FallbackAnswer(IReadOnlyList<RagSource> sources, string? searchError) =>
        sources.Count == 0
            ? RagAbstentionMessages.Russian
            : "Не удалось получить ответ модели; найденные фрагменты приведены ниже.";
}
