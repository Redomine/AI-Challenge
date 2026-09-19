using System.Net;
using System.Text;
using System.Text.Json;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class GigaChatToolFlowTests
{
    [Fact]
    public async Task DisablesAdditionalToolsAfterRoutedToolResult()
    {
        var handler = new ToolFlowHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        var provider = new CountingToolProvider();

        var response = await client.GenerateWithToolsAsync(
            "instructions",
            [new ChatMessage("user", "Что сейчас открыто в Revit?")],
            provider);

        Assert.Equal(1, provider.InvocationCount);
        Assert.Equal("Активный вид прочитан.", response.Content);
        using var finalRequest = JsonDocument.Parse(handler.RequestBodies[^1]);
        Assert.Equal("none", finalRequest.RootElement.GetProperty("function_call").GetString());
    }

    private sealed class CountingToolProvider : IToolProvider
    {
        public int InvocationCount { get; private set; }

        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>(
                [new("revit_get_current_view_info", "Get active view", JsonSerializer.SerializeToElement(new { type = "object" }))]);

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult("{\"viewName\":\"Test\"}");
        }
    }

    private sealed class ToolFlowHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];
        private int _chatRequest;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            if (request.RequestUri!.Host.Contains("ngw.devices", StringComparison.Ordinal))
            {
                return Json(new { access_token = "token", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() });
            }

            _chatRequest++;
            return _chatRequest switch
            {
                1 => Json(new
                {
                    choices = new[] { new { message = new { function_call = new { name = "route_tool_request", arguments = new { action = "call_tool", tool = "revit_get_current_view_info", reason = "Нужны данные" } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                2 => Json(new
                {
                    choices = new[] { new { message = new { content = "", function_call = new { name = "revit_get_current_view_info", arguments = new { } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                _ => Json(new
                {
                    choices = new[] { new { message = new { content = "Активный вид прочитан." }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                })
            };
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}
