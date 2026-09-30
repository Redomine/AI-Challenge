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
builder.Services.AddSingleton<RagQueryService>();
if (Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_DISABLE_SCHEDULER") != "1")
{
    builder.Services.AddHostedService(provider => provider.GetRequiredService<ScheduledTaskService>());
}

var app = builder.Build();
if (args is ["--run-rag-eval"])
{
    var runner = app.Services.GetRequiredService<AutoTestRunner>();
    var cases = await runner.LoadCasesAsync(CancellationToken.None);
    var report = await runner.RunAsync(cases, null, CancellationToken.None);
    var summary = new
    {
        questions = cases.Count,
        completed = report.Runs.Count,
        noRagPassed = report.NoRagRuns.Count(run => run.FactCheckPassed && run.Error is null),
        ragPassed = report.RagRuns.Count(run => run.FactCheckPassed && run.SourceCheckPassed && run.SectionCheckPassed && run.Error is null),
        errors = report.WithErrors,
        report.Error,
        results = report.Runs.Select(run => new
        {
            run.Index, run.Mode, run.FactCheckPassed, run.SourceCheckPassed,
            run.SectionCheckPassed, HasError = run.Error is not null
        })
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
