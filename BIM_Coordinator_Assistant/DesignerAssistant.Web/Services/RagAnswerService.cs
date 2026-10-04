using System.Text;
using System.Text.RegularExpressions;
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
        - Если отвечаешь о сервере, проекте или годе, цитируй всю строку чанка от «Сервер:» до «Год:», а не только шифр проекта. Для списка проектов дай отдельную такую цитату на каждый проект.
        - Если в контексте нет подтверждающих фактов — ответь ровно «Не знаю» (ANSWER) и оставь QUOTES пустым. Не выдумывай цитаты.
        - Не указывай версию Revit, если её нет в цитате.
        - Если подтверждена только часть составного вопроса, ответь на эту часть и явно назови неподтверждённую. Заголовок «Проекты 2022» не доказывает версию Revit 2022.
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
        CancellationToken cancellationToken) =>
        await AskAsync(question, settings, null, cancellationToken);

    public async Task<RagAnswer> AskAsync(
        string question,
        RagRunSettings settings,
        string? answerContext,
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
        var exactServer = ExactServerTerm(originalQuestion);
        var exactProject = Regex.IsMatch(originalQuestion, @"сервер|где\s+.*проект|в\s+каком\s+разделе",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            ? ExactProjectTerm(originalQuestion) : null;

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
                var preHits = reader.Search(vec, settings.PreFilterK, exactServer ?? exactProject);
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

        if (searchOk && sources.Count > 0 &&
            TryBuildServerProjectAnswer(originalQuestion, answerContext, exactServer, sources, trace, out var tableAnswer))
            return tableAnswer;
        if (searchOk && sources.Count > 0 &&
            TryBuildProjectServerAnswer(originalQuestion, exactProject, sources, trace, out var projectAnswer))
            return projectAnswer;

        string rawAnswer;
        string? llmError = null;
        try
        {
            using var llmCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            llmCts.CancelAfter(_options.LlmTimeout);
            var prompt = BuildPrompt(string.IsNullOrWhiteSpace(answerContext) ? originalQuestion : answerContext, sources);
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
        var cleanAnswer = string.IsNullOrWhiteSpace(validated.CleanAnswer) ? rawAnswer : validated.CleanAnswer;
        if (Regex.IsMatch(originalQuestion, @"верси\w*\s+ревита|верси\w*\s+Revit|Revit\s+20\d\d",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            sources.All(s => !Regex.IsMatch(s.Text, @"верси\w*\s+Revit|Revit\s+20\d\d",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
        {
            cleanAnswer = cleanAnswer.TrimEnd() + " В приведённых записях версия Revit не указана; «Проекты 2022» обозначает раздел таблицы.";
        }
        return new RagAnswer(
            Question: originalQuestion,
            Answer: cleanAnswer,
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

    private static string? ExactServerTerm(string question)
    {
        var match = Regex.Match(question,
            @"\b(?:revit[-\s]*|сервер(?:е|а|у|ом)?\s+)(\d{3,4})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? $"revit-{match.Groups[1].Value}" : null;
    }

    private static string? ExactProjectTerm(string question)
    {
        var match = Regex.Match(question, @"\b[A-ZА-Я]{2,}[A-ZА-Я0-9]*-[A-ZА-Я0-9][A-ZА-Я0-9.-]*\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && !match.Value.StartsWith("revit-", StringComparison.OrdinalIgnoreCase)
            ? match.Value : null;
    }

    private static readonly Regex ServerProjectRow = new(
        @"Сервер:\s*(revit-\d+)\.\s*Проект:\s*(.+?)\.\s*Год:\s*(Проекты\s+\d{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static bool TryBuildProjectServerAnswer(
        string question, string? exactProject, IReadOnlyList<RagSource> sources,
        RagSearchTrace trace, out RagAnswer answer)
    {
        answer = null!;
        if (exactProject is null || !Regex.IsMatch(question,
                @"сервер|где\s+.*проект|в\s+каком\s+разделе",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;

        var entries = sources.Select(source => (Source: source, Match: ServerProjectRow.Match(source.Text)))
            .Where(e => e.Match.Success && string.Equals(e.Match.Groups[2].Value, exactProject,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length == 0) return false;
        var citations = entries.Select(e => new RagCitation(e.Source.ChunkId, e.Source.Source,
            e.Source.Section, e.Source.PdfPage, e.Match.Value, true)).ToArray();
        var locations = entries.Select(e =>
            $"{e.Match.Groups[1].Value} в разделе «{e.Match.Groups[3].Value}»")
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var text = $"Проект {exactProject} указан на сервере {string.Join("; ", locations)}.";
        answer = new RagAnswer(question, text, true, sources.Count, sources, null, trace, citations);
        return true;
    }

    private static bool TryBuildServerProjectAnswer(
        string question, string? answerContext, string? exactServer, IReadOnlyList<RagSource> sources,
        RagSearchTrace trace, out RagAnswer answer)
    {
        answer = null!;
        if (exactServer is null || !Regex.IsMatch(question, @"\bкакие\s+проекты\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;

        var entries = new List<(string Project, string Year, RagSource Source, string Quote)>();
        foreach (var source in sources)
        {
            var match = ServerProjectRow.Match(source.Text);
            if (match.Success && string.Equals(match.Groups[1].Value, exactServer, StringComparison.OrdinalIgnoreCase))
                entries.Add((match.Groups[2].Value, match.Groups[3].Value, source, match.Value));
        }
        if (entries.Count == 0) return false;

        var year = Regex.Match(question, @"\b20\d{2}\b").Value;
        if (year.Length > 0 && entries.Any(e => e.Year.EndsWith(year, StringComparison.Ordinal)))
            entries = entries.Where(e => e.Year.EndsWith(year, StringComparison.Ordinal)).ToList();
        var citations = entries.Select(e => new RagCitation(
            e.Source.ChunkId, e.Source.Source, e.Source.Section, e.Source.PdfPage, e.Quote, true)).ToArray();
        var requestedYears = answerContext is null ? [] : Regex.Matches(answerContext, @"\b(20\d{2})\s*:")
            .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        var yearFormat = requestedYears.Length >= 2;
        var byYear = entries.GroupBy(e => e.Year)
            .OrderBy(group => yearFormat ? Array.IndexOf(requestedYears, group.Key[^4..]) : int.MaxValue)
            .ThenBy(group => group.Key, StringComparer.Ordinal).ToArray();
        var text = yearFormat
            ? string.Join("\n", byYear.Select(group =>
                $"{group.Key["Проекты ".Length..]}: {string.Join(", ", group.Select(e => e.Project).Distinct(StringComparer.OrdinalIgnoreCase))}"))
            : $"В найденных записях для {exactServer} указаны проекты {string.Join("; ", byYear.Select(group =>
                $"в разделе «{group.Key}»: {string.Join(", ", group.Select(e => e.Project).Distinct(StringComparer.OrdinalIgnoreCase))}"))}.";
        if (Regex.IsMatch(question, @"верси\w*\s+ревита|верси\w*\s+Revit|Revit\s+20\d\d",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            text += " Эти записи не указывают версию Revit; год в названии раздела не является подтверждением версии.";
        answer = new RagAnswer(question, text, true, sources.Count, sources, null, trace, citations);
        return true;
    }
}
