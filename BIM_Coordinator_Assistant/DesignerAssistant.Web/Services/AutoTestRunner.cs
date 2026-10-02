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

    public AutoTestRunner(NoRagAnswerService noRag, RagAnswerService rag, string questionsPath)
    {
        _noRag = noRag;
        _rag = rag;
        _questionsPath = questionsPath;
    }

    public string QuestionsPath => _questionsPath;

    public async Task<IReadOnlyList<AutoTestCase>> LoadCasesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_questionsPath))
        {
            throw new FileNotFoundException(
                $"Файл вопросов не найден: {_questionsPath}", _questionsPath);
        }
        await using var stream = File.OpenRead(_questionsPath);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!doc.RootElement.TryGetProperty("questions", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "questions.json: ожидается поле questions с массивом.");
        }
        var list = new List<AutoTestCase>();
        var index = 0;
        foreach (var element in arr.EnumerateArray())
        {
            index++;
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
            list.Add(new AutoTestCase(
                Question: question,
                ExpectedSource: expectedSource,
                ExpectedPdfPage: expectedPage,
                ExpectedSectionContains: expectedSection,
                ExpectedFactTerms: expectedTerms,
                ExpectedNoEvidence: expectedNoEvidence));
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
        try
        {
            var result = await _noRag.AskAsync(testCase.Question, cancellationToken);
            answer = result.Answer;
            error = result.Error;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            answer = "";
            error = ex.Message;
        }
        var sources = Array.Empty<RagSource>();
        var facts = EvaluateFactTerms(answer, testCase.ExpectedFactTerms);
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
            Trace: null);
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
        try
        {
            var result = await _rag.AskAsync(testCase.Question, settings, cancellationToken);
            answer = result.Answer;
            error = result.Error;
            searchOk = result.SearchSucceeded;
            retrieved = result.RetrievedCount;
            sources = result.Sources;
            trace = result.Trace;
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
        }

        var facts = EvaluateFactTerms(answer, testCase.ExpectedFactTerms);
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
            Trace: trace);
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
}