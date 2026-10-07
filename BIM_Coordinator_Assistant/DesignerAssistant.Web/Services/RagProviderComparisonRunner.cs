using System.Diagnostics;

namespace DesignerAssistant.Web.Services;

public sealed record RagProviderRun(
    RagLlmProvider Provider,
    string Answer,
    bool Passed,
    bool SearchSucceeded,
    int RetrievedCount,
    long ElapsedMilliseconds,
    string? Error,
    string? QualityIssue);

public sealed record RagProviderCaseResult(
    int Index,
    string Question,
    RagProviderRun Local,
    RagProviderRun? Cloud);

public sealed record RagProviderComparisonReport(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    bool CloudAvailable,
    IReadOnlyList<RagProviderCaseResult> Cases)
{
    public IEnumerable<RagProviderRun> LocalRuns => Cases.Select(item => item.Local);
    public IEnumerable<RagProviderRun> CloudRuns => Cases.Where(item => item.Cloud is not null).Select(item => item.Cloud!);
}

public sealed class RagProviderComparisonRunner(RagQueryService query, AutoTestRunner autoTest)
{
    public async Task<RagProviderComparisonReport> RunAsync(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var cases = await autoTest.LoadCasesAsync(cancellationToken);
        return await RunAsync(cases, progress, cancellationToken);
    }

    public async Task<RagProviderComparisonReport> RunAsync(
        IReadOnlyList<AutoTestCase> cases,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var results = new List<RagProviderCaseResult>(cases.Count);
        var settings = query.DefaultForMode(RagMode.Rag);
        for (var index = 0; index < cases.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var testCase = cases[index];
            progress?.Report($"{index + 1}/{cases.Count} · Qwen: {testCase.Question}");
            var local = await RunOneAsync(testCase, RagLlmProvider.Ollama, settings, cancellationToken);
            RagProviderRun? cloud = null;
            if (query.CloudAvailable)
            {
                progress?.Report($"{index + 1}/{cases.Count} · GigaChat: {testCase.Question}");
                cloud = await RunOneAsync(testCase, RagLlmProvider.GigaChat, settings, cancellationToken);
            }
            results.Add(new RagProviderCaseResult(index + 1, testCase.Question, local, cloud));
            progress?.Report($"{index + 1}/{cases.Count} · готово");
        }
        return new RagProviderComparisonReport(started, DateTimeOffset.Now, query.CloudAvailable, results);
    }

    private async Task<RagProviderRun> RunOneAsync(
        AutoTestCase testCase, RagLlmProvider provider, RagRunSettings settings, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await query.QueryAsync(provider, testCase.Question, RagMode.Rag, settings, cancellationToken);
            var qualityIssue = EvaluateQuality(testCase, result);
            return new RagProviderRun(provider, result.Answer, qualityIssue is null,
                result.SearchSucceeded, result.RetrievedCount, timer.ElapsedMilliseconds,
                result.Error, qualityIssue);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new RagProviderRun(provider, "", false, false, 0, timer.ElapsedMilliseconds,
                exception.Message, "Запрос не завершён.");
        }
    }

    public static string? EvaluateQuality(AutoTestCase testCase, RagQueryResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.Error)) return result.Error;
        if (!result.SearchSucceeded) return "Локальный поиск не выполнен.";
        if (testCase.ExpectedNoEvidence)
            return result.Abstained ? null : "Ожидался отказ от ответа без подтверждённых фактов.";
        if (result.Abstained) return "Модель отказалась отвечать на подтверждаемый вопрос.";
        var missing = (testCase.ExpectedFactTerms ?? [])
            .Where(term => !result.Answer.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (missing.Length > 0) return $"Нет ожидаемых терминов: {string.Join(", ", missing)}.";
        var forbidden = (testCase.ForbiddenTerms ?? [])
            .Where(term => result.Answer.Contains(term, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (forbidden.Length > 0) return $"Обнаружены запрещённые термины: {string.Join(", ", forbidden)}.";
        if (!string.IsNullOrWhiteSpace(testCase.ExpectedSource) &&
            !result.Sources.Any(source => source.Source.Equals(testCase.ExpectedSource, StringComparison.OrdinalIgnoreCase)))
            return $"Не найден ожидаемый источник {testCase.ExpectedSource}.";
        if (result.CitationsSafe.All(citation => !citation.Verified))
            return "Нет подтверждённой дословной цитаты.";
        return null;
    }
}
