using System.Text.Json;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Загружает контрольные вопросы из JSON, прогоняет их в режимах
/// no-rag и rag, оценивает ожидаемые термины и источники.
/// Оценка детерминирована: термины ищутся в lower-case тексте
/// ответа; источник проверяется по фактическим метаданным из индекса;
/// страница PDF сравнивается числом, если она ожидается.
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
            list.Add(new AutoTestCase(
                Question: question,
                ExpectedSource: expectedSource,
                ExpectedPdfPage: expectedPage,
                ExpectedSectionContains: expectedSection,
                ExpectedFactTerms: expectedTerms));
        }
        return list;
    }

    public async Task<AutoTestReport> RunAsync(
        IReadOnlyList<AutoTestCase> cases,
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
                // Сначала no-rag, потом rag — единый порядок, удобный для отчёта.
                runs.Add(await RunSingleAsync(i + 1, "no-rag", testCase, cancellationToken));
                progress?.Report($"  no-rag готов");
                runs.Add(await RunSingleAsync(i + 1, "rag", testCase, cancellationToken));
                progress?.Report($"  rag готов");
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
        return new AutoTestReport(
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.Now,
            Runs: runs,
            Error: fatal);
    }

    private async Task<AutoTestRunResult> RunSingleAsync(
        int index,
        string mode,
        AutoTestCase testCase,
        CancellationToken cancellationToken)
    {
        string answer;
        bool searchOk;
        int retrieved;
        IReadOnlyList<RagSource> sources;
        string? error;
        try
        {
            if (mode == "no-rag")
            {
                var result = await _noRag.AskAsync(testCase.Question, cancellationToken);
                answer = result.Answer;
                error = result.Error;
                searchOk = false;
                retrieved = 0;
                sources = Array.Empty<RagSource>();
            }
            else
            {
                var result = await _rag.AskAsync(testCase.Question, cancellationToken);
                answer = result.Answer;
                error = result.Error;
                searchOk = result.SearchSucceeded;
                retrieved = result.RetrievedCount;
                sources = result.Sources;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            answer = "";
            error = ex.Message;
            searchOk = false;
            retrieved = 0;
            sources = Array.Empty<RagSource>();
        }

        var facts = EvaluateFactTerms(answer, testCase.ExpectedFactTerms);
        var sourceOk = EvaluateSource(sources, mode, testCase);
        var sectionOk = EvaluateSection(sources, mode, testCase);
        return new AutoTestRunResult(
            Index: index,
            Mode: mode,
            Question: testCase.Question,
            ExpectedSource: testCase.ExpectedSource,
            ExpectedPdfPage: testCase.ExpectedPdfPage,
            ExpectedSectionContains: testCase.ExpectedSectionContains,
            SearchSucceeded: searchOk,
            RetrievedCount: retrieved,
            Sources: sources,
            Answer: answer,
            ExpectedFactTerms: testCase.ExpectedFactTerms ?? Array.Empty<string>(),
            FoundFactTerms: facts,
            FactCheckPassed: testCase.ExpectedFactTerms is { Count: > 0 } && facts.Count == testCase.ExpectedFactTerms.Count,
            SourceCheckPassed: sourceOk,
            SectionCheckPassed: sectionOk,
            Error: error);
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
        if (mode != "rag")
        {
            // В режиме без RAG источников быть не должно.
            return true;
        }
        if (sources.Count == 0)
        {
            return false;
        }
        if (string.IsNullOrEmpty(testCase.ExpectedSource)) return true;
        return sources.Any(s => s.Source == testCase.ExpectedSource &&
            (testCase.ExpectedPdfPage is null || s.PdfPage == testCase.ExpectedPdfPage));
    }

    private static bool EvaluateSection(
        IReadOnlyList<RagSource> sources, string mode, AutoTestCase testCase)
    {
        if (mode != "rag") return true;
        if (string.IsNullOrEmpty(testCase.ExpectedSectionContains)) return true;
        return sources.Any(s =>
            (string.IsNullOrEmpty(testCase.ExpectedSource) || s.Source == testCase.ExpectedSource) &&
            (testCase.ExpectedPdfPage is null || s.PdfPage == testCase.ExpectedPdfPage) &&
            s.Section.Contains(testCase.ExpectedSectionContains, StringComparison.OrdinalIgnoreCase));
    }
}
