using System.Net;
using System.Net.Http;
using System.Text.Json;
using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Storage;

namespace DesignerAssistant.Tests;

public sealed class PromptBuilderTests
{
    [Fact]
    public async Task PlanningInputKeepsOriginalUserQueryAndAddsUnderstanding()
    {
        var understanding = new PromptUnderstanding(
            OriginalQuery: "Заполни комментарии выбранных элементов",
            Goal: "Записать комментарий в выбранные элементы",
            Constraints: ["Не изменять модель без явного разрешения"],
            RequiredData: ["Список ElementId из выделения"],
            SuccessCriteria: ["Каждый элемент содержит непустой комментарий"],
            Model: "GigaChat-3-Ultra",
            UsedFallback: false);

        var llm = new CapturingLlm();
        var agent = new DesignAssistantAgent(
            llm,
            new InMemoryChatHistoryStore(),
            new InMemoryMemoryStore(),
            "Базовая инструкция",
            new AppOptions("key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10),
            new CatalogueProvider(),
            promptBuilder: new StaticBuilder(understanding));
        await agent.InitializeAsync();

        await agent.PlanTaskAsync("Заполни комментарии выбранных элементов", promptUnderstanding: understanding);

        var request = Assert.Single(llm.Requests);
        var planningMessage = Assert.Single(request.Messages.Where(message => message.Role == "user"));
        Assert.Contains("[PROMPT_UNDERSTANDING]", planningMessage.Content);
        Assert.Contains("GigaChat-3-Ultra", planningMessage.Content);
        Assert.Contains("[USER_QUERY]", planningMessage.Content);
        Assert.Contains("Заполни комментарии выбранных элементов", planningMessage.Content);
        Assert.DoesNotContain("переписывать", planningMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Заполни комментарии выбранных элементов", agent.GetHistory()[0].Content);
    }

    [Fact]
    public async Task AgentWithoutBuilderStoresDisabledFallbackUnderstanding()
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

        var understanding = await agent.BuildPromptAsync("Запрос");
        Assert.True(understanding.UsedFallback);
        Assert.Equal("disabled", understanding.Model);
        Assert.Equal("Запрос", understanding.OriginalQuery);
    }

    [Fact]
    public async Task PlanningGetsBuilderResultWhenAgentReceivesUnderstanding()
    {
        var understanding = new PromptUnderstanding(
            OriginalQuery: "Сложная задача",
            Goal: "Получить сведения о выбранных элементах",
            Constraints: ["Только чтение"],
            RequiredData: ["ElementId"],
            SuccessCriteria: ["Возвращён JSON с id"],
            Model: "GigaChat-3-Ultra",
            UsedFallback: false);

        var llm = new CapturingLlm();
        var agent = new DesignAssistantAgent(
            llm,
            new InMemoryChatHistoryStore(),
            new InMemoryMemoryStore(),
            "Базовая инструкция",
            new AppOptions("key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10),
            new CatalogueProvider(),
            promptBuilder: new StaticBuilder(understanding));
        await agent.InitializeAsync();

        await agent.PlanTaskAsync("Сложная задача", promptUnderstanding: understanding);

        var request = Assert.Single(llm.Requests);
        Assert.Contains("GigaChat-3-Ultra", request.Messages.First().Content);
        Assert.Contains("Сложная задача", request.Messages.First().Content);
    }

    [Fact]
    public async Task WorkflowRunsPromptBuilderBeforePlanning()
    {
        var builder = new RecordingBuilder();
        var runner = new BuilderAwareRunner(builder);
        var workflow = new TaskWorkflow(runner);

        await workflow.StartAsync("Спланируй запись комментариев");

        Assert.True(builder.Called);
        Assert.Equal(1, runner.PlanningCount);
        Assert.Equal(1, runner.BuilderCount);
        Assert.Equal("Спланируй запись комментариев", builder.LastQuery);
        Assert.NotNull(workflow.Context?.PromptUnderstanding);
        Assert.Equal(builder.Returned.Goal, workflow.Context!.PromptUnderstanding!.Goal);
    }

    [Fact]
    public async Task WorkflowRunsPromptBuilderInDirectModeAndKeepsUnderstanding()
    {
        var builder = new RecordingBuilder();
        var runner = new BuilderAwareRunner(builder);
        var workflow = new TaskWorkflow(runner);

        await workflow.StartDirectAsync("Покажи выбранные элементы");

        Assert.True(builder.Called);
        Assert.Equal(1, runner.BuilderCount);
        Assert.NotNull(workflow.Context?.PromptUnderstanding);
        Assert.Equal(TaskMode.Direct, workflow.Context?.Mode);
    }

    [Fact]
    public async Task BuilderFailureProducesFallbackUnderstandingAndStillProgresses()
    {
        var builder = new ThrowingBuilder(new InvalidOperationException("HTTP 404"));
        var runner = new BuilderAwareRunner(builder);
        var workflow = new TaskWorkflow(runner);

        await workflow.StartDirectAsync("Покажи выбранные элементы");

        Assert.NotNull(workflow.Context?.PromptUnderstanding);
        Assert.True(workflow.Context!.PromptUnderstanding!.UsedFallback);
        Assert.Contains("HTTP 404", workflow.Context.PromptUnderstanding.FailureReason);
        Assert.Equal(1, runner.BuilderCount);
    }

    [Fact]
    public void PromptUnderstandingFormatsToPromptBlockWithoutLosingUserText()
    {
        const string query = @"Покажи выбранные элементы в проекте C:\Проекты\ОВ\Модель.rvt";
        var understanding = new PromptUnderstanding(
            OriginalQuery: query,
            Goal: "Получить описание выделенных элементов в проекте C:\\Проекты\\ОВ\\Модель.rvt",
            Constraints: ["Не менять модель"],
            RequiredData: ["ElementId из выделения"],
            SuccessCriteria: ["Возвращён список с именами"],
            Model: "GigaChat-3-Ultra",
            UsedFallback: false);

        var block = understanding.ToPromptBlock();

        Assert.Contains("[PROMPT_UNDERSTANDING]", block);
        Assert.Contains("[/PROMPT_UNDERSTANDING]", block);
        Assert.Contains(@"C:\Проекты\ОВ\Модель.rvt", block);
    }

    [Fact]
    public async Task RefinementReachesBothBuilderAndPlanning()
    {
        var builder = new RecordingBuilder();
        var runner = new BuilderAwareRunner(builder);
        var workflow = new TaskWorkflow(runner);

        await workflow.StartAsync("Заполни параметр");
        Assert.Equal("Заполни параметр", builder.LastQuery);
        Assert.Null(builder.LastRevision);

        await workflow.RefinePlanAsync("Сначала покажи выбранные элементы");

        Assert.Equal("Заполни параметр", builder.LastQuery);
        Assert.NotNull(builder.LastRevision);
        Assert.Contains("Текущий план", builder.LastRevision);
        Assert.Contains("Сначала покажи выбранные элементы", builder.LastRevision);
        Assert.NotNull(runner.LastRevisionContext);
        Assert.Contains("Сначала покажи выбранные элементы", runner.LastRevisionContext);
    }

    [Fact]
    public async Task PlanningInstructionsForbidTreatingUnderstandingAsAuthorization()
    {
        var llm = new CapturingLlm();
        var understanding = new PromptUnderstanding(
            OriginalQuery: "Покажи выбранные элементы",
            Goal: "Получить id выделенных элементов",
            Constraints: [],
            RequiredData: [],
            SuccessCriteria: [],
            Model: "GigaChat-3-Ultra",
            UsedFallback: false);

        var agent = new DesignAssistantAgent(
            llm,
            new InMemoryChatHistoryStore(),
            new InMemoryMemoryStore(),
            "Базовая инструкция",
            new AppOptions("key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10),
            new CatalogueProvider(),
            promptBuilder: new StaticBuilder(understanding));
        await agent.InitializeAsync();

        await agent.PlanTaskAsync("Покажи выбранные элементы", promptUnderstanding: understanding);

        var request = Assert.Single(llm.Requests);
        Assert.Contains("PROMPT_UNDERSTANDING", request.Instructions);
        Assert.Contains("гипотеза", request.Instructions);
        Assert.Contains("не заменяет", request.Instructions);
        Assert.Contains("только из фактических результатов инструментов", request.Instructions);
    }

    [Fact]
    public async Task SanitizationStripsAskedForElementIdForSelectedElements()
    {
        var fakeContent = """
            {"goal":"Показать выбранные элементы","constraints":["Запрос подразумевает работу в Revit"],"requiredData":["Введите, пожалуйста, ID выбранных элементов.","Назовите путь к модели.","Что вы хотите делать с категориями?"],"successCriteria":["Элементы подсвечены в интерфейсе"]}
            """;
        var capturedSystem = new List<string>();
        var handler = new CapturingHandler(capturedSystem, fakeContent);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var builder = new GigaChatPromptBuilder(
            http,
            new AppOptions("auth", "scope", "GigaChat-2-Max", "GigaChat", 100, "test.db", 10000, 0, 10),
            new PromptBuilderOptions(Enabled: true, Model: "GigaChat-3-Ultra", MaxOutputTokens: 600, ProbeTimeoutSeconds: 5));
        var understanding = await builder.BuildAsync("Покажи выбранные элементы и опиши категории");

        Assert.False(understanding.UsedFallback, $"requests: {string.Join(", ", handler.Requests)}; reason={understanding.FailureReason}");
        Assert.DoesNotContain(understanding.RequiredData, item => item.Contains("ID", StringComparison.OrdinalIgnoreCase) && item.Contains("выбран", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(understanding.RequiredData, item => item.Contains("идентификатор", StringComparison.OrdinalIgnoreCase) && item.Contains("выбран", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(capturedSystem, system => system.Contains("revit_get_selected_elements"));
        Assert.Contains(capturedSystem, system => system.Contains("Не добавляй в requiredData просьбу ввести ElementId"));
    }

    [Fact]
    public async Task SanitizationKeepsCsvWhenUserExplicitlyAsksForCsv()
    {
        const string query = "Покажи выбранные элементы и выгрузи их список в CSV";
        var fakeContent = """
            {"goal":"Показать выбранные элементы и экспортировать их список","constraints":[],"requiredData":[],"successCriteria":["Список элементов выгружен в CSV"]}
            """;
        var capturedSystem = new List<string>();
        var handler = new CapturingHandler(capturedSystem, fakeContent);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var builder = new GigaChatPromptBuilder(
            http,
            new AppOptions("auth", "scope", "GigaChat-2-Max", "GigaChat", 100, "test.db", 10000, 0, 10),
            new PromptBuilderOptions(Enabled: true, Model: "GigaChat-3-Ultra", MaxOutputTokens: 600, ProbeTimeoutSeconds: 5));
        var understanding = await builder.BuildAsync(query);

        Assert.False(understanding.UsedFallback, $"requests: {string.Join(", ", handler.Requests)}; reason={understanding.FailureReason}");
        Assert.Contains(understanding.SuccessCriteria, item => item.Contains("CSV", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SanitizationDoesNothingWhenQueryDoesNotMentionSelectedElements()
    {
        const string query = "Заполни комментарий 'Проверено' для элемента 12345";
        var fakeContent = """
            {"goal":"Записать значение параметра","constraints":[],"requiredData":["Список ID выбранных элементов"],"successCriteria":[]}
            """;
        var capturedSystem = new List<string>();
        var handler = new CapturingHandler(capturedSystem, fakeContent);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var builder = new GigaChatPromptBuilder(
            http,
            new AppOptions("auth", "scope", "GigaChat-2-Max", "GigaChat", 100, "test.db", 10000, 0, 10),
            new PromptBuilderOptions(Enabled: true, Model: "GigaChat-3-Ultra", MaxOutputTokens: 600, ProbeTimeoutSeconds: 5));
        var understanding = await builder.BuildAsync(query);

        Assert.False(understanding.UsedFallback, $"requests: {string.Join(", ", handler.Requests)}; reason={understanding.FailureReason}");
        Assert.Single(understanding.RequiredData);
        Assert.Contains(understanding.RequiredData, item => item.Contains("ID", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuilderProbeReturnsFailureReasonWhenProbeReturns404()
    {
        var handler = new SequenceHandler(
            (HttpStatusCode.OK, """{"access_token":"abc","expires_at":4102444800}"""),
            (HttpStatusCode.NotFound, """{"message":"model not found"}"""));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var options = new AppOptions("auth", "scope", "GigaChat-2-Max", "GigaChat", 100, "test.db", 10000, 0, 10);
        var builderOptions = new PromptBuilderOptions(Enabled: true, Model: "GigaChat-3-Ultra", MaxOutputTokens: 600, ProbeTimeoutSeconds: 5);
        var builder = new GigaChatPromptBuilder(http, options, builderOptions);

        var understanding = await builder.BuildAsync("Покажи выбранные элементы");

        Assert.True(understanding.UsedFallback);
        Assert.Contains("404", understanding.FailureReason);
    }

    private static GigaChatPromptBuilder BuildPromptBuilder(string jsonContent, out List<string> capturedSystem)
    {
        capturedSystem = new List<string>();
        var handler = new CapturingHandler(capturedSystem, jsonContent);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var options = new AppOptions("auth", "scope", "GigaChat-2-Max", "GigaChat", 100, "test.db", 10000, 0, 10);
        var builderOptions = new PromptBuilderOptions(Enabled: true, Model: "GigaChat-3-Ultra", MaxOutputTokens: 600, ProbeTimeoutSeconds: 5);
        return new GigaChatPromptBuilder(http, options, builderOptions);
    }

    private static Task<GigaChatPromptBuilder> BuildPromptBuilderAsync(string jsonContent, out List<string> capturedSystem)
    {
        capturedSystem = new List<string>();
        return Task.FromResult(BuildPromptBuilder(jsonContent, out capturedSystem));
    }

    private sealed class CapturingHandler(List<string> captured, string json) : HttpMessageHandler
    {
        public List<string> Requests { get; } = new();
        private int _calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Requests.Add(path);
            if (path.Contains("/oauth", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"abc","expires_at":4102444800}""")
                };
            }
            if (path.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                _calls++;
                if (_calls == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""")
                    };
                }
                if (request.Content is not null)
                {
                    var body = await request.Content.ReadAsStringAsync(cancellationToken);
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.TryGetProperty("messages", out var messages))
                    {
                        foreach (var message in messages.EnumerateArray())
                        {
                            if (message.TryGetProperty("role", out var role) &&
                                role.GetString() == "system" &&
                                message.TryGetProperty("content", out var system))
                            {
                                captured.Add(system.GetString() ?? string.Empty);
                            }
                        }
                    }
                }
                var responseBody = JsonSerializer.Serialize(new
                {
                    choices = new[] { new { message = new { content = json }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 }
                });
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private int _index;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = _index < responses.Length ? responses[_index++] : responses[^1];
            return Task.FromResult(new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body)
            });
        }
    }

    [Fact]
    public async Task BuilderSystemPromptMentionsSelectedElementsRule()
    {
        const string query = "Покажи выбранные элементы и опиши категории";
        var jsonContent = """
            {"goal":"Показать выбранные элементы","constraints":[],"requiredData":[],"successCriteria":[]}
            """;
        var capturedSystem = new List<string>();
        var handler = new CapturingHandler(capturedSystem, jsonContent);
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        var options = new AppOptions("auth", "scope", "GigaChat-2-Max", "GigaChat", 100, "test.db", 10000, 0, 10);
        var builderOptions = new PromptBuilderOptions(Enabled: true, Model: "GigaChat-3-Ultra", MaxOutputTokens: 600, ProbeTimeoutSeconds: 5);
        var builder = new GigaChatPromptBuilder(http, options, builderOptions);
        var understanding = await builder.BuildAsync(query);

        Assert.False(understanding.UsedFallback, $"requests: {string.Join(", ", handler.Requests)}; reason={understanding.FailureReason}");
        var system = Assert.Single(capturedSystem);
        Assert.Contains("revit_get_selected_elements", system);
        Assert.Contains("Не добавляй в requiredData просьбу ввести ElementId", system);
        Assert.Contains("constraints — только то, что пользователь явно ограничил", system);
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
                {"summary":"План","steps":[
                  {"action":"Прочитать выбранные","tool":"revit_get_selected_elements","arguments":{},"argumentSources":{}}
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
                new("revit_get_selected_elements", "Получить выбранные элементы", JsonSerializer.SerializeToElement(new { type = "object" }))
            ]);

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Planning не должен вызывать инструменты.");
    }

    private sealed class StaticBuilder(PromptUnderstanding understanding) : IPromptBuilder
    {
        public string ModelName => understanding.Model;
        public bool IsAvailable => true;
        public string? AvailabilityError => null;
        public Task<PromptUnderstanding> BuildAsync(string query, CancellationToken cancellationToken = default, string? revisionContext = null)
        {
            understanding = understanding with { OriginalQuery = query };
            return Task.FromResult(understanding);
        }
    }

    private sealed class RecordingBuilder : IPromptBuilder
    {
        public bool Called { get; private set; }
        public string? LastQuery { get; private set; }
        public string? LastRevision { get; private set; }
        public PromptUnderstanding Returned { get; } = new(
            OriginalQuery: string.Empty,
            Goal: "Получить сведения о выбранных элементах",
            Constraints: ["Только чтение"],
            RequiredData: ["ElementId"],
            SuccessCriteria: ["Возвращён JSON с id"],
            Model: "GigaChat-3-Ultra",
            UsedFallback: false);

        public string ModelName => Returned.Model;
        public bool IsAvailable => true;
        public string? AvailabilityError => null;

        public Task<PromptUnderstanding> BuildAsync(string query, CancellationToken cancellationToken = default, string? revisionContext = null)
        {
            Called = true;
            LastQuery = query;
            LastRevision = revisionContext;
            return Task.FromResult(Returned with { OriginalQuery = query });
        }
    }

    private sealed class ThrowingBuilder(Exception exception) : IPromptBuilder
    {
        public string ModelName => "GigaChat-3-Ultra";
        public bool IsAvailable => false;
        public string? AvailabilityError => exception.Message;

        public Task<PromptUnderstanding> BuildAsync(string query, CancellationToken cancellationToken = default, string? revisionContext = null) =>
            Task.FromResult(new PromptUnderstanding(
                query,
                Goal: query,
                Constraints: [],
                RequiredData: [],
                SuccessCriteria: [],
                Model: ModelName,
                UsedFallback: true,
                FailureReason: exception.Message));
    }

    private sealed class BuilderAwareRunner(IPromptBuilder builder) : ITaskStageRunner
    {
        private int _executionCalls;
        private int _validationCalls;

        public int BuilderCount { get; private set; }
        public int PlanningCount { get; private set; }
        public string? LastRevisionContext { get; private set; }
        public PromptUnderstanding? LastUnderstanding { get; private set; }
        public TaskPlan? LastStructuredPlan => null;
        public PromptUnderstanding? LastPromptUnderstanding => LastUnderstanding;

        public Task<PromptUnderstanding> BuildPromptAsync(string query, CancellationToken cancellationToken = default, string? revisionContext = null)
        {
            BuilderCount++;
            return builder.BuildAsync(query, cancellationToken, revisionContext);
        }

        public Task<AgentResponse> PlanTaskAsync(string query, string? revisionContext = null, CancellationToken cancellationToken = default, string? storedUserMessage = null, PromptUnderstanding? promptUnderstanding = null)
        {
            PlanningCount++;
            LastRevisionContext = revisionContext;
            LastUnderstanding = promptUnderstanding;
            return Task.FromResult(new AgentResponse(
                new LlmResponse("1. Согласованный план", "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)),
                0, true, 0));
        }

        public Task<AgentResponse> ExecuteTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            _executionCalls++;
            return Task.FromResult(new AgentResponse(
                new LlmResponse("[EXECUTED] Готово", "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)),
                0, true, 0));
        }

        public Task<AgentResponse> ValidateTaskAsync(TaskContext context, CancellationToken cancellationToken = default)
        {
            _validationCalls++;
            return Task.FromResult(new AgentResponse(
                new LlmResponse("[PASS] Проверено", "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)),
                0, true, 0));
        }
    }
}