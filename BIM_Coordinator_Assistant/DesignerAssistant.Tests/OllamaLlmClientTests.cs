using System.Net;
using System.Text;
using System.Text.Json;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class OllamaLlmClientTests
{
    [Fact]
    public async Task GeneratesLocalAnswerAndRemovesThinkingBlock()
    {
        var handler = new FakeHandler(
            """{"message":{"content":"Four","thinking":"hidden reasoning"},"prompt_eval_count":12,"eval_count":4,"done_reason":"stop"}""");
        var client = Client(handler);

        var answer = await client.GenerateAsync("Answer briefly", [new ChatMessage("user", "2+2?")]);

        Assert.Equal("Four", answer.Content);
        Assert.Equal("hidden reasoning", answer.Reasoning);
        Assert.Equal(4, answer.Usage.CompletionTokens);
        Assert.Contains("\"model\":\"qwen3:4b\"", handler.Requests[0]);
        Assert.Contains("\"think\":true", handler.Requests[0]);
        Assert.DoesNotContain("api.giga.chat", handler.Requests[0]);
    }

    [Fact]
    public async Task SeparatesEmbeddedThinkingFromAnswer()
    {
        var handler = new FakeHandler(
            """{"message":{"content":"work without opening tag</think>Four"},"done_reason":"stop"}""");

        var answer = await Client(handler).GenerateAsync("Answer", [new ChatMessage("user", "2+2?")]);

        Assert.Equal("Four", answer.Content);
        Assert.Equal("work without opening tag", answer.Reasoning);
    }

    [Fact]
    public async Task RagClientCanDisableThinkingForStrictAnswerFormat()
    {
        var handler = new FakeHandler("""{"message":{"content":"ANSWER: Проверено\nQUOTES:"},"done_reason":"stop"}""");
        var client = new OllamaLlmClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") },
            thinkingEnabled: false);

        var answer = await client.GenerateAsync("Use strict format", [new ChatMessage("user", "Check")]);

        Assert.StartsWith("ANSWER:", answer.Content);
        Assert.Contains("\"think\":false", handler.Requests[0]);
    }

    [Fact]
    public async Task PassesJsonSchemaToLocalApi()
    {
        var handler = new FakeHandler("""{"message":{"content":"{\"status\":\"PASS\"}"},"done_reason":"stop"}""");
        var client = Client(handler);
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"status":{"type":"string"}}}""");

        var answer = await client.GenerateStructuredAsync("Validate", [new ChatMessage("user", "Check")], schema.RootElement);

        Assert.Equal("{\"status\":\"PASS\"}", answer.Content);
        Assert.Contains("\"format\":{\"type\":\"object\"", handler.Requests[0]);
        Assert.Contains("\"think\":false", handler.Requests[0]);
    }

    [Fact]
    public async Task SendsToolResultBackToLocalModel()
    {
        var handler = new FakeHandler(
            """{"message":{"content":"","tool_calls":[{"type":"function","function":{"name":"sample_read","arguments":{"id":7}}}]},"prompt_eval_count":10}""",
            """{"message":{"content":"Element 7 is ready"},"prompt_eval_count":15,"eval_count":6}""");
        var provider = new FakeToolProvider();
        var client = Client(handler);

        var answer = await client.GenerateWithToolsAsync("Use tools", [new ChatMessage("user", "Read element 7")], provider);

        Assert.Equal("Element 7 is ready", answer.Content);
        Assert.Single(answer.ToolResults!);
        Assert.Equal(7, provider.LastId);
        Assert.Contains("\"tool_name\":\"sample_read\"", handler.Requests[1]);
        Assert.Contains("\"id\":7", handler.Requests[1]);
        Assert.Contains("\"think\":false", handler.Requests[0]);
    }

    [Fact]
    public void CloudSelectionRequiresKey()
    {
        var http = new HttpClient(new FakeHandler());
        var options = new DesignerAssistant.Configuration.AppOptions("", "", "", "", 1200, "db", 128000, 0, 10);
        var client = new SwitchableLlmClient(Client(new FakeHandler()), new GigaChatClient(http, options), false);

        Assert.Equal(LlmProvider.Ollama, client.Provider);
        Assert.Throws<InvalidOperationException>(() => client.Select(LlmProvider.GigaChat));
        Assert.Equal(LlmProvider.Ollama, client.Provider);
    }

    [Fact]
    public async Task SwitchingProviderClearsPreviousModelsReasoning()
    {
        var handler = new FakeHandler(
            """{"message":{"content":"4","thinking":"Qwen reasoning"},"done_reason":"stop"}""");
        using var gigaHttp = new HttpClient(new FakeHandler());
        var options = new DesignerAssistant.Configuration.AppOptions("key", "scope", "model", "tokenizer", 1200, "db", 128000, 0, 10);
        var client = new SwitchableLlmClient(Client(handler), new GigaChatClient(gigaHttp, options), true);

        await client.GenerateAsync("Answer", [new ChatMessage("user", "2+2?")]);
        Assert.True(client.HasResponse);
        Assert.Equal("Qwen reasoning", client.LastReasoning);

        client.Select(LlmProvider.GigaChat);
        Assert.False(client.HasResponse);
        Assert.Null(client.LastReasoning);
    }

    [Fact]
    public async Task SelectedElementsRequireTheirMcpTool()
    {
        var handler = new FakeHandler();
        var answer = await Client(handler).GenerateWithToolsAsync(
            "Use Revit tools", [new ChatMessage("user", "Покажи выделенные элементы")], new FakeToolProvider());

        Assert.Contains("revit_get_selected_elements", answer.Content);
        Assert.Equal("missing_tool", answer.FinishReason);
        Assert.Empty(handler.Requests);
    }

    private static OllamaLlmClient Client(FakeHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:11434/") });

    private sealed class FakeHandler(params string[] responses) : HttpMessageHandler
    {
        private int _next;
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("http://127.0.0.1:11434/api/chat", request.RequestUri?.ToString());
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responses[_next++], Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeToolProvider : IToolProvider, IToolConfirmationProvider
    {
        public int LastId { get; private set; }

        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ToolDefinition> tools =
            [new("sample_read", "Read sample", JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { id = new { type = "integer" } },
                required = new[] { "id" }
            }))];
            return Task.FromResult(tools);
        }

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            LastId = arguments.GetProperty("id").GetInt32();
            return Task.FromResult("""{"ok":true,"id":7}""");
        }

        public Task<bool> ConfirmAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
