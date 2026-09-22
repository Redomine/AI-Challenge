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
    public async Task PlanningDoesNotResendGeneralChatHistory()
    {
        var history = new InMemoryChatHistoryStore();
        await history.AppendAsync([
            new ChatMessage("user", "Предыдущий запрос"),
            new ChatMessage("assistant", new string('X', 40_000))
        ]);
        var llm = new CapturingLlm();
        var agent = new DesignAssistantAgent(
            llm,
            history,
            new InMemoryMemoryStore(),
            "Базовая инструкция",
            new AppOptions("key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10),
            new CatalogueProvider());
        await agent.InitializeAsync();

        await agent.PlanTaskAsync("Заполни комментарии выбранных элементов");

        var request = Assert.Single(llm.Requests);
        var message = Assert.Single(request.Messages);
        Assert.Contains("Заполни комментарии", message.Content);
        Assert.DoesNotContain(new string('X', 100), message.Content);
    }

    [Fact]
    public async Task PushbuttonRequestWithoutExecutionAdapterAsksToCreateAdapterLocally()
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

        var response = await agent.PlanTaskAsync(@"Запусти C:\Commands\Расчет.pushbutton для выбранной системы");

        Assert.Empty(llm.Requests);
        Assert.StartsWith("[CLARIFY]", response.ModelResponse.Content);
        Assert.Contains("нет зарегистрированного MCP-инструмента", response.ModelResponse.Content);
        Assert.Contains("текущим выделением", response.ModelResponse.Content);
        Assert.Equal("pyrevit_adapter_required", response.ModelResponse.FinishReason);
        Assert.Equal(2, agent.GetHistory().Count);
    }

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
        Assert.Contains("точное parameterName", request.Instructions);
        Assert.Contains("revit_get_element_parameters", request.Instructions);
        Assert.DoesNotContain("TOOL_CATALOGUE", agent.GetHistory()[0].Content);
        Assert.Equal("Заполни комментарии выбранных элементов", agent.GetHistory()[0].Content);
        Assert.NotNull(agent.LastStructuredPlan);
        Assert.Equal(3, agent.LastStructuredPlan.Steps.Count);
        Assert.Equal("revit_get_element_parameters", agent.LastStructuredPlan.Steps[1].Tool);

        await agent.ExecuteTaskAsync(new TaskContext(
            "Заполни комментарии выбранных элементов",
            TaskState.Execution,
            StructuredPlan: agent.LastStructuredPlan,
            PlanApproved: true));
        Assert.Contains("Единственное совпадение без учёта регистра", llm.Requests[1].Instructions);
        Assert.Contains("После ошибки отсутствующего параметра не повторяй то же имя", llm.Requests[1].Instructions);
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
                  {"action":"Получить точные имена параметров","tool":"revit_get_element_parameters","arguments":{},"argumentSources":{"elementIds":"результат шага 1"}},
                  {"action":"Записать комментарий","tool":"revit_set_element_parameter_values","arguments":{"value":"Тест","valueType":"string"},"argumentSources":{"elementIds":"результат шага 1","parameterName":"точное имя из результата шага 2"}}
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
                new("revit_get_element_parameters", "Получить параметры", JsonSerializer.SerializeToElement(new
                {
                    type = "object",
                    required = new[] { "elementIds" }
                })),
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
