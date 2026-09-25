using System.Text.Json;
using System.Net;
using System.Text;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Tools;

namespace DesignerAssistant.Tests;

public sealed class MultiServerMcpFlowTests
{
    [Fact]
    public async Task AgentStopsAfterOperationalToolFailure()
    {
        var handler = new FailingFlowHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        var provider = new FailingOpsProvider();

        var response = await client.GenerateWithToolsAsync(
            "Call operational tools in sequence.",
            [new ChatMessage("user", "Проверь heartbeat, затем журнал")], provider);

        Assert.Equal("tool_error", response.FinishReason);
        Assert.Equal(new[] { "ops_heartbeat" }, provider.Calls);
        Assert.Equal(1, handler.ChatRequests);
    }

    [Fact]
    public async Task AgentStopsAtEightToolCalls()
    {
        var handler = new FailingFlowHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        var provider = new RepeatingOpsProvider();

        await Assert.ThrowsAsync<ToolCallLimitExceededException>(() => client.GenerateWithToolsAsync(
            "Call tools as needed.", [new ChatMessage("user", "Проверь heartbeat")], provider));

        Assert.Equal(8, provider.Calls);
        Assert.Equal(9, handler.ChatRequests);
    }

    [Fact]
    public async Task RevitResultFlowsThroughLogAndOpsServersInOrder()
    {
        var auditPath = Path.Combine(Path.GetTempPath(), $"assistant-audit-{Guid.NewGuid():N}.jsonl");
        var previous = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_AUDIT_PATH");
        Environment.SetEnvironmentVariable("DESIGN_ASSISTANT_AUDIT_PATH", auditPath);
        try
        {
            var dll = typeof(OpsState).Assembly.Location;
            await using var log = new StdioMcpToolProvider("log", dll);
            await using var ops = new StdioMcpToolProvider("ops", dll);
            var revit = new FakeRevit();
            var combined = new CompositeToolProvider(revit, log, ops);
            var tools = await combined.GetToolsAsync();
            Assert.Contains(tools, tool => tool.Name == "log_append_event");
            Assert.Contains(tools, tool => tool.Name == "log_recent_events");
            Assert.Contains(tools, tool => tool.Name == "ops_heartbeat");
            Assert.Contains(tools, tool => tool.Name == "ops_send_notification");

            var policy = new ToolExecutionPolicy(combined, tools);
            var selection = await policy.ExecuteAsync("revit_get_selected_elements", Json("{}"));
            Assert.True(selection.Ok);
            var id = selection.Result!.Value.GetProperty("elementIds")[0].GetInt32();
            var logged = await policy.ExecuteAsync("log_append_event", Json(JsonSerializer.Serialize(new
            {
                taskId = "flow-test", eventType = "selection_read", source = "revit_get_selected_elements",
                summary = $"Element {id} read", ok = true, durationMs = 4
            })));
            Assert.True(logged.Ok, logged.Error?.Message);
            var heartbeat = await policy.ExecuteAsync("ops_heartbeat", Json("{}"));
            Assert.True(heartbeat.Ok, heartbeat.Error?.Message);
            var recent = await policy.ExecuteAsync("log_recent_events", Json("{\"limit\":10}"));
            Assert.True(recent.Ok, recent.Error?.Message);
            Assert.Contains("Element 42 read", recent.Result!.Value.GetRawText());
            Assert.Equal(new[] { "revit_get_selected_elements" }, revit.Calls);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DESIGN_ASSISTANT_AUDIT_PATH", previous);
            if (File.Exists(auditPath)) File.Delete(auditPath);
        }
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class FailingOpsProvider : IToolProvider
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>([
                new ToolDefinition("ops_heartbeat", "Heartbeat", Json("{\"type\":\"object\"}")),
                new ToolDefinition("log_recent_events", "Recent events", Json("{\"type\":\"object\"}"))
            ]);
        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(name);
            return Task.FromResult("{\"ok\":false,\"message\":\"Service unavailable\"}");
        }
    }

    private sealed class RepeatingOpsProvider : IToolProvider
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>([
                new ToolDefinition("ops_heartbeat", "Heartbeat", Json("{\"type\":\"object\"}"))
            ]);
        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult("{\"ok\":true}");
        }
    }

    private sealed class FailingFlowHandler : HttpMessageHandler
    {
        public int ChatRequests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object body;
            if (request.RequestUri!.Host.Contains("ngw.devices", StringComparison.Ordinal))
                body = new { access_token = "token", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() };
            else
            {
                ChatRequests++;
                body = new
                {
                    choices = new[] { new { message = new { content = "", function_call = new { name = "ops_heartbeat", arguments = new { } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FakeRevit : IToolProvider
    {
        public List<string> Calls { get; } = [];
        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>([
                new ToolDefinition("revit_get_selected_elements", "Read selection", Json("{\"type\":\"object\"}"))
            ]);

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Calls.Add(name);
            return Task.FromResult("{\"elementIds\":[42]}");
        }
    }
}
