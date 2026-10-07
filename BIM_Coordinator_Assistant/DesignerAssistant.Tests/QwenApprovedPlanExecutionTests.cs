using System.Text.Json;
using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Storage;

namespace DesignerAssistant.Tests;

public sealed class QwenApprovedPlanExecutionTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task ExecutesResolvedStepsInOrderAndStopsOnFailure(bool firstFails, int expectedCalls)
    {
        var provider = new StepProvider(firstFails);
        using var ollamaHttp = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:11434/") };
        using var gigaHttp = new HttpClient();
        var options = new AppOptions("", "", "", "", 1200, "test.db", 10000, 0, 10);
        var llm = new SwitchableLlmClient(
            new OllamaLlmClient(ollamaHttp), new GigaChatClient(gigaHttp, options), false);
        var agent = new DesignAssistantAgent(llm, new InMemoryChatHistoryStore(),
            new InMemoryMemoryStore(), "Инструкция", options, provider);
        await agent.InitializeAsync();
        var plan = new TaskPlan("Прочитать состояние", [
            new TaskPlanStep("Первый шаг", "revit_get_current_target", new Dictionary<string, JsonElement>(), new Dictionary<string, string>()),
            new TaskPlanStep("Второй шаг", "revit_get_current_view_info", new Dictionary<string, JsonElement>(), new Dictionary<string, string>())
        ]);

        var response = await agent.ExecuteTaskAsync(new TaskContext("Проверь состояние", TaskState.Execution,
            StructuredPlan: plan, PlanApproved: true));

        Assert.Equal(expectedCalls, provider.Calls.Count);
        Assert.Equal("revit_get_current_target", provider.Calls[0]);
        if (!firstFails) Assert.Equal("revit_get_current_view_info", provider.Calls[1]);
        Assert.Equal(expectedCalls, response.ModelResponse.ToolResults?.Count);
        Assert.Equal(!firstFails, response.ModelResponse.ToolResults?.All(result => result.Ok));
    }

    private sealed class StepProvider(bool firstFails) : IToolProvider
    {
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>([
                new("revit_get_current_target", "test", JsonSerializer.SerializeToElement(new { type = "object" })),
                new("revit_get_current_view_info", "test", JsonSerializer.SerializeToElement(new { type = "object" }))
            ]);

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(name);
            return Task.FromResult(firstFails && Calls.Count == 1
                ? "{\"ok\":false,\"message\":\"first failed\"}"
                : "{\"ok\":true}");
        }
    }
}
