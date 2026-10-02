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
    var questionsPath = ResolveQuestionsPath(
        sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath);
    var noRag = sp.GetRequiredService<NoRagAnswerService>();
    var rag = sp.GetRequiredService<RagAnswerService>();
    return new AutoTestRunner(noRag, rag, questionsPath);
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

static string ResolveQuestionsPath(string contentRoot)
{
    var explicitPath = Environment.GetEnvironmentVariable("RAG_QUESTIONS_PATH");
    if (!string.IsNullOrWhiteSpace(explicitPath)) return explicitPath;
    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "Выгрузка страниц", "index_out", "questions.json");
}
