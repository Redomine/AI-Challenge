using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Web.Components;
using DesignerAssistant.Web.Services;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpClient();
builder.Services.AddScoped<AssistantSession>();
builder.Services.AddSingleton<ScheduledTaskService>();
// RAG/No-RAG: разделяемое LLM-клиент + обёртки, конфигурация из env.
builder.Services.AddSingleton(_ => RagOptions.FromEnvironment());
builder.Services.AddSingleton<LlmClientFactory>();
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<RagOptions>();
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var http = factory.CreateClient();
    http.BaseAddress = new Uri(options.OllamaUrl.TrimEnd('/') + "/");
    return new OllamaEmbeddingsClient(http, options.EmbedTimeout, options.EmbedMaxRetries);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<RagOptions>();
    var ollama = sp.GetRequiredService<OllamaEmbeddingsClient>();
    var llm = sp.GetRequiredService<LlmClientFactory>().Create();
    return new RagAnswerService(options, ollama, llm);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<RagOptions>();
    var llm = sp.GetRequiredService<LlmClientFactory>().Create();
    return new NoRagAnswerService(llm, options.LlmTimeout);
});
builder.Services.AddSingleton(sp =>
{
    var env = sp.GetRequiredService<IWebHostEnvironment>();
    var questionsPath = ResolveQuestionsPath(env.ContentRootPath, env.WebRootPath);
    var fallbackPath = ResolveLegacyQuestionsPath();
    var noRag = sp.GetRequiredService<NoRagAnswerService>();
    var rag = sp.GetRequiredService<RagAnswerService>();
    return new AutoTestRunner(noRag, rag, questionsPath, fallbackPath);
});
builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<RagOptions>();
    var rag = sp.GetRequiredService<RagAnswerService>();
    var noRag = sp.GetRequiredService<NoRagAnswerService>();
    return new RagQueryService(rag, noRag, options);
});
if (Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_DISABLE_SCHEDULER") != "1")
{
    builder.Services.AddHostedService(provider => provider.GetRequiredService<ScheduledTaskService>());
}

var app = builder.Build();
if (args is ["--run-day24-eval"])
{
    var runner = app.Services.GetRequiredService<AutoTestRunner>();
    var options = app.Services.GetRequiredService<RagOptions>();
    var cases = await runner.LoadCasesAsync(CancellationToken.None);
    var report = await runner.RunAggregateAsync(
        cases,
        baseline: options.ToProductionSettings(),
        enhanced: null,
        includeNoRag: false,
        progress: null,
        cancellationToken: CancellationToken.None);
    var runs = report.Runs.Select(r => new
    {
        r.Index, r.Question, r.Answer, r.Passed, r.SearchSucceeded,
        r.Abstained, r.AbstentionReason, r.ClarificationQuestion,
        r.QuoteCheckPassed, r.AnswerSupportedByQuotes,
        r.FactCheckPassed, r.SourceCheckPassed, r.SectionCheckPassed,
        r.ForbiddenTermsCheckPassed, r.Error,
        Sources = r.Sources.Select(s => new
        {
            s.ChunkId, s.Source, s.Section, s.PdfPage, s.Score
        }),
        Citations = r.CitationsSafe.Select(c => new
        {
            c.ChunkId, c.Source, c.Section, c.PdfPage, c.Quote, c.Verified
        })
    }).ToArray();
    var outputPath = Path.Combine(Path.GetDirectoryName(options.IndexPath)!,
        $"rag-evaluation-day24-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
    {
        Questions = cases.Count,
        Passed = report.Runs.Count(r => r.Passed),
        Results = runs
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Day 24 evaluation: {outputPath}");
    return;
}
if (args is ["--run-rag-eval"])
{
    var runner = app.Services.GetRequiredService<AutoTestRunner>();
    var ragOptions = app.Services.GetRequiredService<RagOptions>();
    var cases = await runner.LoadCasesAsync(CancellationToken.None);
    var aggregate = await runner.RunAggregateAsync(
        cases,
        baseline: ragOptions.ToBaselineSettings() with { PostFilterK = ragOptions.TopK },
        enhanced: ragOptions.ToEnhancedSettings(),
        includeNoRag: true,
        progress: null,
        cancellationToken: CancellationToken.None);

    var settings = new
    {
        baseline = new
        {
            aggregate.Baseline.RewriteEnabled,
            aggregate.Baseline.PreFilterK,
            aggregate.Baseline.ScoreThreshold,
            aggregate.Baseline.PostFilterK
        },
        enhanced = aggregate.Enhanced is null ? null : new
        {
            aggregate.Enhanced.RewriteEnabled,
            aggregate.Enhanced.PreFilterK,
            aggregate.Enhanced.ScoreThreshold,
            aggregate.Enhanced.PostFilterK
        },
        ragOptions = new
        {
            topK = ragOptions.TopK,
            preFilterK = ragOptions.PreFilterK,
            postFilterK = ragOptions.PostFilterK,
            scoreThreshold = ragOptions.ScoreThreshold,
            rewriteEnabled = ragOptions.RewriteEnabled,
            embedModel = ragOptions.EmbedModel,
            indexPath = ragOptions.IndexPath
        }
    };

    var summary = new
    {
        questions = cases.Count,
        startedAt = aggregate.StartedAt,
        completedAt = aggregate.CompletedAt,
        completed = aggregate.Runs.Count,
        errors = aggregate.Runs.Count(r => r.Error is not null),
        modes = aggregate.Aggregates.Select(a => new
        {
            a.Mode,
            a.Total,
            a.Passed,
            a.WithErrors,
            a.NegativeTotal,
            a.NegativePassed,
            a.AverageRetrieved
        }),
        negativeCases = aggregate.NegativeCases.Select(n => new
        {
            n.Index, n.Mode, n.Question,
            n.SearchSucceeded, n.RetrievedCount, n.FactCheckPassed,
            n.SourceCheckPassed, n.SectionCheckPassed,
            HasError = n.Error is not null
        }),
        aggregate.Error,
        results = aggregate.Runs.Select(run => new
        {
            run.Index,
            run.Mode,
            run.Question,
            expected = new
            {
                run.ExpectedSource,
                run.ExpectedPdfPage,
                run.ExpectedSectionContains,
                run.ExpectedNoEvidence
            },
            run.SearchSucceeded,
            run.RetrievedCount,
            run.FactCheckPassed,
            run.SourceCheckPassed,
            run.SectionCheckPassed,
            run.IsNegativeCase,
            FoundFactTerms = run.FoundFactTerms.Count,
            ExpectedFactTerms = run.ExpectedFactTerms.Count,
            run.Answer,
            Sources = run.Sources.Select(s => new
            {
                s.ChunkId, s.Source, s.Title, s.Section, s.PdfPage, s.Score
            }),
            trace = run.Trace is null ? null : new
            {
                originalQuestion = run.Trace.OriginalQuestion,
                searchQuery = run.Trace.SearchQuery,
                preFilterCount = run.Trace.PreFilterCount,
                postFilterCount = run.Trace.PostFilterCount,
                threshold = run.Trace.Threshold,
                postFilterLimit = run.Trace.PostFilterLimit,
                preFilterScores = run.Trace.PreFilterScores,
                rejected = run.Trace.Rejected.Select(r => new
                {
                    r.ChunkId, r.Source, r.Title, r.Section, r.PdfPage, r.Score, r.Reason
                })
            },
            HasError = run.Error is not null,
            run.Error
        }),
        settings
    };
    var path = Path.Combine(Path.GetDirectoryName(runner.QuestionsPath)!,
        $"rag-evaluation-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"RAG evaluation: {path}");
    return;
}
if (args is ["--run-scheduled", var taskIdText] && Guid.TryParse(taskIdText, out var taskId))
{
    await app.Services.GetRequiredService<ScheduledTaskService>().RunNowAsync(taskId);
    return;
}
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

static string ResolveQuestionsPath(string contentRoot, string webRoot)
{
    // 1. Явный override через env остаётся высшим приоритетом — это
    // используется CLI-режимом --run-rag-eval и тестами.
    var explicitPath = Environment.GetEnvironmentVariable("RAG_QUESTIONS_PATH");
    if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
    // 2. Dedicated Day 24 JSON в wwwroot/app-data — приоритетный набор.
    // Файл пакуется в Web output и доступен, когда приложение запущено
    // из каталога проекта (wwwroot резолвится через ContentRoot/WebRoot).
    var embeddedPath = Path.Combine(webRoot ?? contentRoot, "app-data", "questions-day24.json");
    if (File.Exists(embeddedPath)) return embeddedPath;
    var contentRootAltPath = Path.Combine(contentRoot, "wwwroot", "app-data", "questions-day24.json");
    if (File.Exists(contentRootAltPath)) return contentRootAltPath;
    // 3. Фолбэк: легаси-путь для совместимости.
    return ResolveLegacyQuestionsPath();
}

static string ResolveLegacyQuestionsPath()
{
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "Выгрузка страниц", "index_out", "questions.json");
}
