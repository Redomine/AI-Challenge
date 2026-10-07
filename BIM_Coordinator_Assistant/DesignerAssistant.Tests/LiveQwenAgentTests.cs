using DesignerAssistant.Agent;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Web.Services;
using Xunit.Abstractions;

namespace DesignerAssistant.Tests;

public sealed class LiveQwenAgentTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task QwenOpensModelThroughRealAgent()
    {
        if (Environment.GetEnvironmentVariable("RUN_LIVE_QWEN_AGENT") != "1") return;
        var modelPath = Environment.GetEnvironmentVariable("LIVE_QWEN_MODEL_PATH");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            throw new InvalidOperationException("Для живого теста задайте LIVE_QWEN_MODEL_PATH на существующий RVT-файл.");

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "DesignerAssistant.Web"));
        var database = Path.Combine(Path.GetTempPath(), "AIChallengeDay27", "qwen-live-agent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        Environment.SetEnvironmentVariable("DESIGN_ASSISTANT_DB_PATH", database);
        await using var session = new AssistantSession(new LiveHttpClientFactory());
        await session.InitializeAsync(root, allowInteractiveConfirmation: false,
            autoApproveRevitChanges: true, automaticNotifications: false);
        session.SelectProvider(LlmProvider.Ollama);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var read = await session.AskAsync("Какая модель сейчас открыта в Revit? Проверь инструментом.", timeout.Token);
        output.WriteLine($"READ: {read.ModelResponse.Content}");
        foreach (var trace in read.ToolTraces ?? []) output.WriteLine(trace);

        await session.StartTaskAsync(
            $"Открой модель {modelPath} без отсоединения.",
            new TaskPauseOptions(true, false, false), timeout.Token);
        output.WriteLine($"PLAN STATE: {session.CurrentTask?.State}");
        output.WriteLine($"PLAN: {session.CurrentTask?.Plan}");
        output.WriteLine($"PLAN ERROR: {session.CurrentTask?.FailureReason}");
        if (session.AwaitingPlanApproval)
        {
            await session.ApprovePlanAsync(timeout.Token);
            output.WriteLine($"FINAL STATE: {session.CurrentTask?.State}");
            output.WriteLine($"EXECUTION: {session.CurrentTask?.ExecutionResult}");
            output.WriteLine($"ERROR: {session.CurrentTask?.FailureReason}");
            foreach (var trace in session.CurrentTask?.DiagnosticTraces ?? []) output.WriteLine(trace);
        }

        Assert.Equal(TaskState.Done, session.CurrentTask?.State);
        Assert.Contains(session.AgentActivity, entry => entry.Stage == TaskState.Planning && entry.Title == "План модели");
        Assert.Contains(session.AgentActivity, entry => entry.Stage == TaskState.Execution &&
            entry.Title == "Инструмент: revit_custom_open_model" && entry.Content.Contains("\"success\": true", StringComparison.Ordinal));
        Assert.Contains(session.AgentActivity, entry => entry.Stage == TaskState.Validation && entry.Title == "Проверка модели");
    }

    private sealed class LiveHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
