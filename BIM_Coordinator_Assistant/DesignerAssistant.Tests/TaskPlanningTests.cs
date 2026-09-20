using System.Text.Json;
using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Storage;

namespace DesignerAssistant.Tests;

public sealed class TaskPlanningTests
{
    [Fact]
    public async Task PlanningReceivesCurrentToolCatalogueWithoutExposingToolCalls()
    {
        var llm = new CapturingLlm();
        var agent = new DesignAssistantAgent(
            llm,
            new InMemoryChatHistoryStore(),
            new InMemoryMemoryStore(),
            "Базовая инструкция",
            new AppOptions("key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10),
            new CatalogueProvider());
        await agent.InitializeAsync();

        await agent.PlanTaskAsync("Заполни комментарии выбранных элементов");

        var request = Assert.Single(llm.Requests);
        var planningMessage = Assert.Single(request.Messages.Where(message => message.Role == "user"));
        Assert.Contains("revit_get_selected_elements", planningMessage.Content);
        Assert.Contains("revit_set_element_parameter_values", planningMessage.Content);
        Assert.Contains("valueType", planningMessage.Content);
        Assert.Contains("проверь его JSON-сигнатуру", planningMessage.Content);
        Assert.Contains("если подходящего инструмента нет", planningMessage.Content);
        Assert.Contains("только на стадии PLANNING", request.Instructions);
        Assert.DoesNotContain("TOOL_CATALOGUE", agent.GetHistory()[0].Content);
        Assert.Equal("Заполни комментарии выбранных элементов", agent.GetHistory()[0].Content);
    }

    private sealed class CapturingLlm : ILlmClient
    {
        public List<(string Instructions, IReadOnlyCollection<ChatMessage> Messages)> Requests { get; } = [];

        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TokenCountResult(0, true));

        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Requests.Add((instructions, messages));
            return Task.FromResult(new LlmResponse(
                """
                {"summary":"Заполнить комментарии","steps":[
                  {"action":"Получить выбранные элементы","tool":"revit_get_selected_elements","arguments":{},"argumentSources":{}},
                  {"action":"Записать комментарий","tool":"revit_set_element_parameter_values","arguments":{"parameterName":"Комментарии","value":"Тест","valueType":"string"},"argumentSources":{"elementIds":"результат шага 1"}}
                ]}
                """,
                "stop",
                new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)));
        }
    }

    private sealed class CatalogueProvider : IToolProvider
    {
        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>(
            [
                new("revit_get_selected_elements", "Получить выбранные элементы", JsonSerializer.SerializeToElement(new { type = "object" })),
                new("revit_set_element_parameter_values", "Записать параметр", JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    required = new[] { "elementIds", "parameterName", "value", "valueType" }
                }))
            ]);

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Planning не должен вызывать инструменты.");
    }
}
