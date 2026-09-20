using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Revit;
using DesignerAssistant.Storage;

var startedAt = DateTimeOffset.Now;
var repetitions = int.TryParse(Environment.GetEnvironmentVariable("SOAK_COUNT"), out var configuredCount) ? configuredCount : 30;
var timeoutMinutes = int.TryParse(Environment.GetEnvironmentVariable("SOAK_TIMEOUT_MINUTES"), out var configuredTimeout) ? configuredTimeout : 30;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
var artifacts = Path.Combine(root, "artifacts");
Directory.CreateDirectory(artifacts);
var runId = startedAt.ToString("yyyyMMdd-HHmmss");
var logPath = Path.Combine(artifacts, $"live-soak-{runId}.jsonl");
var databasePath = Path.Combine(artifacts, $"live-soak-{runId}.db");

var sourceDatabase = Path.Combine(root, "DesignerAssistant", "designer-assistant.db");
var options = AppOptions.FromEnvironment() with { DatabasePath = databasePath };
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
var llm = new GigaChatClient(http, options);
var profileStore = new InMemoryUserProfileStore();
var invariantStore = new InMemoryInvariantStore();
await profileStore.InitializeAsync(deadline.Token);
await invariantStore.InitializeAsync(deadline.Token);
if (File.Exists(sourceDatabase))
{
    var sourceProfiles = new SqliteUserProfileStore(sourceDatabase);
    var sourceInvariants = new SqliteInvariantStore(sourceDatabase);
    await sourceProfiles.InitializeAsync(deadline.Token);
    await sourceInvariants.InitializeAsync(deadline.Token);
    var profile = await sourceProfiles.LoadAsync(deadline.Token);
    if (profile is not null) await profileStore.SaveAsync(profile, deadline.Token);
    await invariantStore.SaveAsync(await sourceInvariants.LoadAsync(deadline.Token), deadline.Token);
}

await using var revit = new RevitMcpClient(confirmWriteAsync: _ => Task.FromResult(false));
var agent = new DesignAssistantAgent(
    llm,
    new SqliteChatHistoryStore(databasePath),
    new SqliteMemoryStore(databasePath),
    DesignerAssistantPrompt.Text,
    options,
    revit,
    profileStore,
    invariantStore);
await agent.InitializeAsync(deadline.Token);

var results = new List<SoakResult>();
var scenarios = new[]
{
    new Scenario("identity", "Кто ты?", repetitions),
    new Scenario("active_view", "Что на активном виде", repetitions)
};

foreach (var scenario in scenarios)
{
    for (var index = 1; index <= scenario.Count && !deadline.IsCancellationRequested; index++)
    {
        var pauses = (index % 4) switch
        {
            0 => new TaskPauseOptions(true, false, false),
            1 => new TaskPauseOptions(true, true, false),
            2 => new TaskPauseOptions(true, false, true),
            _ => new TaskPauseOptions(true, true, true)
        };
        var watch = Stopwatch.StartNew();
        var workflow = new TaskWorkflow(agent) { PauseOptions = pauses };
        string? error = null;
        var validationRetries = 0;
        try
        {
            await workflow.StartAsync(scenario.Question, deadline.Token);
            await workflow.ApprovePlanAsync(deadline.Token);
            for (var guard = 0; workflow.Context?.State != TaskState.Done && guard < 20; guard++)
            {
                if (workflow.AwaitingPlanApproval)
                    await workflow.ApprovePlanAsync(deadline.Token);
                else if (workflow.ValidationFailed)
                {
                    validationRetries++;
                    if (validationRetries > 2)
                        throw new InvalidOperationException("Validation не завершилась после двух пользовательских повторов.");
                    await workflow.RetryValidationAsync(deadline.Token);
                }
                else if (workflow.IsPaused)
                    await workflow.ContinueAsync(deadline.Token);
                else
                    throw new InvalidOperationException("Workflow остановился без доступного перехода.");
            }
            if (workflow.Context?.State != TaskState.Done)
                throw new InvalidOperationException("Workflow не достиг Done за 20 переходов.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = exception.ToString();
        }

        watch.Stop();
        var execution = workflow.Context?.ExecutionResult ?? "";
        var result = new SoakResult(
            scenario.Name,
            index,
            pauses.AfterExecution,
            pauses.AfterValidation,
            error is null && workflow.Context?.State == TaskState.Done,
            workflow.Context?.State.ToString(),
            execution,
            Fingerprint(execution),
            workflow.Context?.ValidationResult ?? "",
            validationRetries,
            error,
            watch.ElapsedMilliseconds,
            DateTimeOffset.Now);
        results.Add(result);
        await File.AppendAllTextAsync(logPath, JsonSerializer.Serialize(result) + Environment.NewLine, Encoding.UTF8);
        Console.WriteLine($"{scenario.Name} {index:00}/{scenario.Count}: {(result.Success ? "PASS" : "FAIL")} {watch.Elapsed.TotalSeconds:F1}s fingerprint={result.Fingerprint[..Math.Min(10, result.Fingerprint.Length)]}");
    }
}

var summary = new
{
    startedAt,
    finishedAt = DateTimeOffset.Now,
    timedOut = deadline.IsCancellationRequested,
    planned = scenarios.Sum(item => item.Count),
    completed = results.Count,
    succeeded = results.Count(item => item.Success),
    failed = results.Count(item => !item.Success),
    groups = results.GroupBy(item => item.Scenario).Select(group => new
    {
        scenario = group.Key,
        completed = group.Count(),
        succeeded = group.Count(item => item.Success),
        distinctExecutionAnswers = group.Where(item => item.Success).Select(item => item.Fingerprint).Distinct().Count(),
        fingerprints = group.Where(item => item.Success).GroupBy(item => item.Fingerprint).Select(item => new { fingerprint = item.Key, count = item.Count() })
    })
};
var summaryPath = Path.Combine(artifacts, $"live-soak-{runId}-summary.json");
await File.WriteAllTextAsync(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"LOG={logPath}");
Console.WriteLine($"SUMMARY={summaryPath}");

static string Fingerprint(string value) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));

internal sealed record Scenario(string Name, string Question, int Count);
internal sealed record SoakResult(
    string Scenario,
    int Index,
    bool PauseAfterExecution,
    bool PauseAfterValidation,
    bool Success,
    string? FinalState,
    string ExecutionResponse,
    string Fingerprint,
    string ValidationResponse,
    int ValidationRetries,
    string? Error,
    long ElapsedMilliseconds,
    DateTimeOffset FinishedAt);
