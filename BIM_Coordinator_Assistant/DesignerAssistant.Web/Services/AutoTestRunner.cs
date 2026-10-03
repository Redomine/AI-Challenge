using System.Text.Json;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Загружает контрольные вопросы из JSON, прогоняет их в режимах
/// baseline/enhanced (и опционально no-rag), оценивает ожидаемые
/// термины и источники. Оценка детерминирована: термины ищутся
/// в lower-case тексте ответа; источник проверяется по фактическим
/// метаданным из индекса; страница PDF сравнивается числом,
/// если она ожидается. Baseline-режим использует оригинальный
/// вопрос без переписывания и без score-порога (top-K = TopK).
/// Enhanced — расширенный: rewrite + pre-K + threshold + post-K.
/// </summary>
public sealed class AutoTestRunner
{
    private readonly NoRagAnswerService _noRag;
    private readonly RagAnswerService _rag;
    private readonly string _questionsPath;
    private readonly string? _fallbackPath;

    public AutoTestRunner(NoRagAnswerService noRag, RagAnswerService rag, string questionsPath)
        : this(noRag, rag, questionsPath, null)
    {
    }

    /// <summary>
    /// Конструктор с дополнительным «фолбэк»-путём: если
    /// <paramref name="questionsPath"/> не существует, загрузчик
    /// пытается открыть <paramref name="fallbackPath"/>. Это позволяет
    /// автотесту «День 24» иметь собственный набор вопросов в
    /// wwwroot/app-data, а старым путям — дефолтный fallback.
    /// </summary>
    public AutoTestRunner(
        NoRagAnswerService noRag,
        RagAnswerService rag,
        string questionsPath,
        string? fallbackPath)
    {
        _noRag = noRag;
        _rag = rag;
        _questionsPath = questionsPath;
        _fallbackPath = fallbackPath;
    }

    public string QuestionsPath => _questionsPath;
    public string? FallbackPath => _fallbackPath;

    /// <summary>
    /// Возвращает фактический путь к JSON-файлу, который удалось
    /// открыть: основной, если существует, иначе фолбэк. Если
    /// ни один не найден — кидает <see cref="FileNotFoundException"/>
    /// с указанием обоих путей.
    /// </summary>
    public string ResolveExistingPath()
    {
        if (File.Exists(_questionsPath)) return _questionsPath;
        if (_fallbackPath is not null && File.Exists(_fallbackPath)) return _fallbackPath;
        var fallbackInfo = _fallbackPath is null
            ? "(не задан)"
            : _fallbackPath;
        throw new FileNotFoundException(
            $"Файл вопросов не найден: основной '{_questionsPath}', фолбэк '{fallbackInfo}'.",
            _questionsPath);
    }

    /// <summary>
    /// Определяет, относится ли файл к dedicated Day 24 набору:
    /// имя файла содержит 'day24' или в JSON есть поле <c>version</c>
    /// со схемой day24-*.
    /// </summary>
    public bool IsDay24Set(string? path = null)
    {
        // Безопасная попытка: если файл не существует, не кидаем
        // FileNotFoundException — это позволяет вызывать метод из
        // логики построения AutoTestRunResult, где основной путь
        // мог не существовать (тесты с собственным списком кейсов
        // через RunAggregateAsync без LoadCasesAsync).
        string actual;
        if (path is not null)
        {
            actual = path;
        }
        else if (File.Exists(_questionsPath))
        {
            actual = _questionsPath;
        }
        else if (_fallbackPath is not null && File.Exists(_fallbackPath))
        {
            actual = _fallbackPath;
        }
        else
        {
            return false;
        }
        var fileName = Path.GetFileName(actual);
        if (fileName.Contains("day24", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(actual));
            if (doc.RootElement.TryGetProperty("version", out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s) &&
                    s.StartsWith("day24", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Не валидный JSON или нет доступа — не считаем Day 24.
        }
        return false;
    }

    public async Task<IReadOnlyList<AutoTestCase>> LoadCasesAsync(CancellationToken cancellationToken)
    {
        var path = ResolveExistingPath();
        return await LoadCasesFromPathAsync(path, cancellationToken);
    }

    /// <summary>
    /// Загрузить вопросы из произвольного JSON-файла. Используется
    /// и UI-кнопкой «День 24», и тестами для прямой проверки dedicated
    /// набора. Парсинг толерантен к отсутствию новых полей: старые
    /// questions.json продолжают работать.
    /// </summary>
    public static async Task<IReadOnlyList<AutoTestCase>> LoadCasesFromPathAsync(
        string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Файл вопросов не найден: {path}", path);
        }
        await using var stream = File.OpenRead(path);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("questions", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "questions.json: ожидается поле questions с массивом.");
        }
        var list = new List<AutoTestCase>();
        foreach (var element in arr.EnumerateArray())
        {
            var question = element.TryGetProperty("question", out var q)
                ? q.GetString() ?? "" : "";
            var expectedSource = element.TryGetProperty("expected_source", out var s)
                ? s.GetString() ?? "" : "";
            int? expectedPage = null;
            if (element.TryGetProperty("expected_pdf_page", out var p) &&
                p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var pi))
            {
                expectedPage = pi;
            }
            string? expectedSection = null;
            if (element.TryGetProperty("expected_section_contains", out var sec) &&
                sec.ValueKind == JsonValueKind.String)
            {
                expectedSection = sec.GetString();
            }
            var expectedTerms = new List<string>();
            if (element.TryGetProperty("expected_fact_terms", out var terms) &&
                terms.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in terms.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String)
                    {
                        var str = t.GetString();
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            expectedTerms.Add(str);
                        }
                    }
                }
            }
            // Явный флаг негативного кейса: ожидаем, что в индексе нет
            // подтверждающих фактов и модель должна воздержаться от ответа.
            // Пустой expected_fact_terms на обычном вопросе НЕ считается
            // негативом — это две разные семантики. По умолчанию false
            // для обратной совместимости со старыми questions.json.
            var expectedNoEvidence = false;
            if (element.TryGetProperty("expected_no_evidence", out var noEvidence) &&
                (noEvidence.ValueKind == JsonValueKind.True ||
                 noEvidence.ValueKind == JsonValueKind.False))
            {
                expectedNoEvidence = noEvidence.GetBoolean();
            }
            // Day 24: категория кейса (stlb / neighbour / kb_positive и т. п.).
            // Используется UI и тестами для специальных правил.
            string? expectedKind = null;
            if (element.TryGetProperty("expected_kind", out var kind) &&
                kind.ValueKind == JsonValueKind.String)
            {
                expectedKind = kind.GetString();
            }
            // Day 24: список «запрещённых» токенов для позитивного ответа.
            // Если ответ содержит любой из них — он считается провалившим
            // проверку точности (для STLB-OK1 это revit-702/revit-704).
            var forbiddenTerms = new List<string>();
            if (element.TryGetProperty("forbidden_terms", out var forbidden) &&
                forbidden.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in forbidden.EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.String)
                    {
                        var str = t.GetString();
                        if (!string.IsNullOrWhiteSpace(str))
                        {
                            forbiddenTerms.Add(str);
                        }
                    }
                }
            }
            list.Add(new AutoTestCase(
                Question: question,
                ExpectedSource: expectedSource,
                ExpectedPdfPage: expectedPage,
                ExpectedSectionContains: expectedSection,
                ExpectedFactTerms: expectedTerms,
                ExpectedNoEvidence: expectedNoEvidence,
                ExpectedKind: expectedKind,
                ForbiddenTerms: forbiddenTerms));
        }
        return list;
    }

    /// <summary>
    /// Простой прогон baseline+rag (без enhanced и без агрегатов).
    /// Используется старыми тестами; делегирует полному
    /// <see cref="RunAggregateAsync"/> для согласованности.
    /// </summary>
    public Task<AutoTestReport> RunAsync(
        IReadOnlyList<AutoTestCase> cases,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // legacy-формат: «rag» и «no-rag». Чтобы не ломать обратную совместимость,
        // прогоним через полный путь и смаппим обратно.
        return RunLegacyAsync(cases, progress, cancellationToken);
    }

    private async Task<AutoTestReport> RunLegacyAsync(
        IReadOnlyList<AutoTestCase> cases,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // Используем baseline-настройки через дефолт.
        var baseline = new RagRunSettings(
            RewriteEnabled: false,
            PreFilterK: 4,
            ScoreThreshold: 0f,
            PostFilterK: 4,
            RewriteTimeout: TimeSpan.FromSeconds(20));
        var aggregate = await RunAggregateAsync(
            cases,
            baseline: baseline,
            enhanced: null,
            includeNoRag: true,
            progress: progress,
            cancellationToken: cancellationToken);
        // Переименовываем «baseline» → «rag» для совместимости со старым контрактом.
        var renamed = aggregate.Runs.Select(r =>
            r.Mode == "baseline"
                ? r with { Mode = "rag" }
                : r).ToArray();
        return new AutoTestReport(
            StartedAt: aggregate.StartedAt,
            CompletedAt: aggregate.CompletedAt,
            Runs: renamed,
            Error: aggregate.Error);
    }

    /// <summary>
    /// Полный прогон: baseline (RagMode.Rag) и enhanced (RagMode.Enhanced)
    /// обязательно, no-rag — опционально. Возвращает агрегаты по режимам,
    /// список негативных кейсов и фактические настройки прогона.
    /// Настройки берутся из аргументов и не мутируют singleton RagOptions.
    /// </summary>
    public async Task<AutoTestAggregateReport> RunAggregateAsync(
        IReadOnlyList<AutoTestCase> cases,
        RagRunSettings baseline,
        RagRunSettings? enhanced,
        bool includeNoRag,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;
        var runs = new List<AutoTestRunResult>();
        string? fatal = null;
        try
        {
            for (var i = 0; i < cases.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var testCase = cases[i];
                progress?.Report($"Вопрос {i + 1}/{cases.Count}: {testCase.Question}");

                if (includeNoRag)
                {
                    runs.Add(await RunNoRagAsync(i + 1, testCase, cancellationToken));
                    progress?.Report($"  no-rag готов");
                }
                runs.Add(await RunRagAsync(i + 1, "baseline", baseline, testCase, cancellationToken));
                progress?.Report($"  baseline готов");
                if (enhanced is not null)
                {
                    runs.Add(await RunRagAsync(i + 1, "enhanced", enhanced, testCase, cancellationToken));
                    progress?.Report($"  enhanced готов");
                }
            }
        }
        catch (OperationCanceledException)
        {
            fatal = "Отменено пользователем.";
        }
        catch (Exception ex)
        {
            fatal = ex.Message;
        }

        var modes = runs
            .GroupBy(r => r.Mode)
            .Select(g => BuildAggregate(g.Key, g.ToArray()))
            .ToArray();

        var negatives = runs.Where(r => r.IsNegativeCase).ToArray();
        var baselineSettings = new AutoTestRunSettings(
            baseline.RewriteEnabled,
            baseline.PreFilterK,
            baseline.ScoreThreshold,
            baseline.PostFilterK);
        AutoTestRunSettings? enhancedSettings = enhanced is null ? null : new(
            enhanced.RewriteEnabled,
            enhanced.PreFilterK,
            enhanced.ScoreThreshold,
            enhanced.PostFilterK);

        return new AutoTestAggregateReport(
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.Now,
            Baseline: baselineSettings,
            Enhanced: enhancedSettings,
            Aggregates: modes,
            NegativeCases: negatives,
            Runs: runs,
            Error: fatal);
    }

    private static AutoTestModeAggregate BuildAggregate(string mode, AutoTestRunResult[] runs)
    {
        if (runs.Length == 0)
        {
            return new AutoTestModeAggregate(mode, 0, 0, 0, 0, 0, 0);
        }
        var negative = runs.Where(r => r.IsNegativeCase).ToArray();
        return new AutoTestModeAggregate(
            Mode: mode,
            Total: runs.Length,
            Passed: runs.Count(r => r.Passed),
            WithErrors: runs.Count(r => r.Error is not null),
            NegativeTotal: negative.Length,
            NegativePassed: negative.Count(r => r.Passed),
            AverageRetrieved: runs.Length == 0
                ? 0
                : Math.Round(runs.Average(r => r.RetrievedCount), 2));
    }

    private async Task<AutoTestRunResult> RunNoRagAsync(
        int index,
        AutoTestCase testCase,
        CancellationToken cancellationToken)
    {
        string answer;
        string? error;
        bool abstained;
        RagAbstentionReason abstentionReason;
        string? clarification;
        try
        {
            var result = await _noRag.AskAsync(testCase.Question, cancellationToken);
            answer = result.Answer;
            error = result.Error;
            abstained = result.Abstained;
            abstentionReason = result.AbstentionReason;
            clarification = result.ClarificationQuestion;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            answer = "";
            error = ex.Message;
            abstained = false;
            abstentionReason = RagAbstentionReason.LlmError;
            clarification = null;
        }
        var sources = Array.Empty<RagSource>();
        var facts = EvaluateFactTerms(answer, testCase.ExpectedFactTerms);
        var forbidden = EvaluateForbiddenTerms(answer, testCase.ForbiddenTerms, abstained);
        return new AutoTestRunResult(
            Index: index,
            Mode: "no-rag",
            Question: testCase.Question,
            ExpectedSource: testCase.ExpectedSource,
            ExpectedPdfPage: testCase.ExpectedPdfPage,
            ExpectedSectionContains: testCase.ExpectedSectionContains,
            ExpectedNoEvidence: testCase.ExpectedNoEvidence,
            SearchSucceeded: false,
            RetrievedCount: 0,
            Sources: sources,
            Answer: answer,
            ExpectedFactTerms: testCase.ExpectedFactTerms ?? Array.Empty<string>(),
            FoundFactTerms: facts,
            FactCheckPassed: (testCase.ExpectedFactTerms?.Count ?? 0) == 0
                ? facts.Count == 0
                : facts.Count == testCase.ExpectedFactTerms!.Count,
            SourceCheckPassed: EvaluateSource(sources, "no-rag", testCase),
            SectionCheckPassed: EvaluateSection(sources, "no-rag", testCase),
            Error: error,
            Trace: null,
            Citations: null,
            QuoteCheckPassed: false,
            AnswerSupportedByQuotes: abstained,
            Abstained: abstained,
            AbstentionReason: abstentionReason,
            ExpectedKind: testCase.ExpectedKind,
            ForbiddenTerms: testCase.ForbiddenTerms ?? Array.Empty<string>(),
            FoundForbiddenTerms: forbidden.Found,
            ForbiddenTermsCheckPassed: forbidden.Passed,
            ClarificationQuestion: clarification,
            IsDay24Evaluation: IsDay24Set());
    }

    private async Task<AutoTestRunResult> RunRagAsync(
        int index,
        string mode,
        RagRunSettings settings,
        AutoTestCase testCase,
        CancellationToken cancellationToken)
    {
        string answer;
        bool searchOk;
        int retrieved;
        IReadOnlyList<RagSource> sources;
        string? error;
        RagSearchTrace? trace;
        IReadOnlyList<RagCitation>? citations;
        bool abstained;
        RagAbstentionReason abstentionReason;
        string? clarification;
        try
        {
            var result = await _rag.AskAsync(testCase.Question, settings, cancellationToken);
            answer = result.Answer;
            error = result.Error;
            searchOk = result.SearchSucceeded;
            retrieved = result.RetrievedCount;
            sources = result.Sources;
            trace = result.Trace;
            citations = result.CitationsSafe;
            abstained = result.Abstained;
            abstentionReason = result.AbstentionReason;
            clarification = result.ClarificationQuestion;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            answer = "";
            error = ex.Message;
            searchOk = false;
            retrieved = 0;
            sources = Array.Empty<RagSource>();
            trace = null;
            citations = Array.Empty<RagCitation>();
            abstained = true;
            abstentionReason = RagAbstentionReason.LlmError;
            clarification = null;
        }

        var facts = EvaluateFactTerms(answer, testCase.ExpectedFactTerms);
        var verifiedCitations = citations
            .Where(c => c.Verified)
            .ToArray();
        // QuoteCheckPassed — есть хотя бы одна подтверждённая цитата
        // для режима, который не abstentionит.
        var quoteCheckPassed = verifiedCitations.Length > 0;
        // AnswerSupportedByQuotes — текст ответа поддержан цитатами
        // (для abstention — не требуется; abstain уже «Не знаю»).
        var answerSupported = abstained || RagCitationParser.AnswerSupportedByQuotes(answer, verifiedCitations);
        // Forbidden-terms check (Day 24): если ответ содержит токены из
        // ForbiddenTerms (например, для STLB-OK1: revit-702 или revit-704),
        // это проваливает проверку точности. Для abstention — список
        // запрещённых токенов автоматически считается соблюдённым,
        // т.к. ответ «Не знаю» не содержит никаких фактов.
        var forbidden = EvaluateForbiddenTerms(answer, testCase.ForbiddenTerms, abstained);
        // День 24: флаг «это Day 24 набор» определяется по файлу
        // вопросов (имя или version). Для не-Day24 наборов старая
        // семантика Passed сохраняется без ужесточения.
        var isDay24 = IsDay24Set();
        return new AutoTestRunResult(
            Index: index,
            Mode: mode,
            Question: testCase.Question,
            ExpectedSource: testCase.ExpectedSource,
            ExpectedPdfPage: testCase.ExpectedPdfPage,
            ExpectedSectionContains: testCase.ExpectedSectionContains,
            ExpectedNoEvidence: testCase.ExpectedNoEvidence,
            SearchSucceeded: searchOk,
            RetrievedCount: retrieved,
            Sources: sources,
            Answer: answer,
            ExpectedFactTerms: testCase.ExpectedFactTerms ?? Array.Empty<string>(),
            FoundFactTerms: facts,
            FactCheckPassed: (testCase.ExpectedFactTerms?.Count ?? 0) == 0
                ? facts.Count == 0
                : facts.Count == testCase.ExpectedFactTerms!.Count,
            SourceCheckPassed: EvaluateSource(sources, mode, testCase),
            SectionCheckPassed: EvaluateSection(sources, mode, testCase),
            Error: error,
            Trace: trace,
            Citations: citations,
            QuoteCheckPassed: quoteCheckPassed,
            AnswerSupportedByQuotes: answerSupported,
            Abstained: abstained,
            AbstentionReason: abstentionReason,
            ExpectedKind: testCase.ExpectedKind,
            ForbiddenTerms: testCase.ForbiddenTerms ?? Array.Empty<string>(),
            FoundForbiddenTerms: forbidden.Found,
            ForbiddenTermsCheckPassed: forbidden.Passed,
            ClarificationQuestion: clarification,
            IsDay24Evaluation: isDay24);
    }

    private static IReadOnlyList<string> EvaluateFactTerms(string answer, IReadOnlyList<string>? terms)
    {
        if (terms is null || terms.Count == 0) return Array.Empty<string>();
        if (string.IsNullOrEmpty(answer)) return Array.Empty<string>();
        var lower = answer.ToLowerInvariant();
        var found = new List<string>();
        foreach (var term in terms)
        {
            if (lower.Contains(term.ToLowerInvariant(), StringComparison.Ordinal))
            {
                found.Add(term);
            }
        }
        return found;
    }

    private static bool EvaluateSource(
        IReadOnlyList<RagSource> sources, string mode, AutoTestCase testCase)
    {
        if (mode != "rag" && mode != "baseline" && mode != "enhanced")
        {
            // В режиме без RAG источников быть не должно.
            return true;
        }
        if (sources.Count == 0)
        {
            return string.IsNullOrEmpty(testCase.ExpectedSource);
        }
        if (string.IsNullOrEmpty(testCase.ExpectedSource)) return true;
        return sources.Any(s => s.Source == testCase.ExpectedSource &&
            (testCase.ExpectedPdfPage is null || s.PdfPage == testCase.ExpectedPdfPage));
    }

    private static bool EvaluateSection(
        IReadOnlyList<RagSource> sources, string mode, AutoTestCase testCase)
    {
        if (mode != "rag" && mode != "baseline" && mode != "enhanced") return true;
        if (string.IsNullOrEmpty(testCase.ExpectedSectionContains)) return true;
        return sources.Any(s =>
            (string.IsNullOrEmpty(testCase.ExpectedSource) || s.Source == testCase.ExpectedSource) &&
            (testCase.ExpectedPdfPage is null || s.PdfPage == testCase.ExpectedPdfPage) &&
            s.Section.Contains(testCase.ExpectedSectionContains, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Проверить, что ответ не содержит «запрещённых» токенов из
    /// <see cref="AutoTestCase.ForbiddenTerms"/>. Используется для Day 24:
    /// STLB-OK1 не должен упоминать revit-702/revit-704, даже если в
    /// ответе есть revit-703. Для abstention-ответов список
    /// запрещённых токенов автоматически считается соблюдённым.
    /// </summary>
    private static (IReadOnlyList<string> Found, bool Passed) EvaluateForbiddenTerms(
        string answer, IReadOnlyList<string>? forbidden, bool abstained)
    {
        if (abstained || forbidden is null || forbidden.Count == 0)
        {
            return (Array.Empty<string>(), true);
        }
        if (string.IsNullOrEmpty(answer))
        {
            return (Array.Empty<string>(), true);
        }
        var found = new List<string>();
        foreach (var term in forbidden)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            if (answer.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(term);
            }
        }
        return (found, found.Count == 0);
    }
}