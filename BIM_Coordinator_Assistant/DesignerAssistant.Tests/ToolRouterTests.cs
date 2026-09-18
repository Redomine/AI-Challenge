using System.Net;
using System.Text;
using System.Text.Json;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class ToolRouterTests
{
    [Fact]
    public async Task RoutesArbitraryWordingToKnownTool()
    {
        using var http = new HttpClient(new FakeHandler(Response("revit_get_element_details")));
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));
        var decision = await router.RouteAsync(
            [new ChatMessage("user", "Посмотри на объект с номером 6772117")],
            [Tool("revit_get_element_details")]);

        Assert.Equal("call_tool", decision.Action);
        Assert.Equal("revit_get_element_details", decision.ToolName);
        Assert.Equal(30, decision.BilledTokens);
    }

    [Fact]
    public async Task RejectsToolOutsideCurrentMcpCatalogue()
    {
        using var http = new HttpClient(new FakeHandler(Response("revit_send_code_to_revit")));
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => router.RouteAsync(
            [new ChatMessage("user", "Сделай что-нибудь")],
            [Tool("revit_get_element_details")]));
    }

    private static ToolDefinition Tool(string name) => new(
        name, "Получить сведения", JsonSerializer.SerializeToElement(new { type = "object" }));

    private static AppOptions Options() => new(
        "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10);

    private static string Response(string tool) => JsonSerializer.Serialize(new
    {
        choices = new[]
        {
            new
            {
                message = new
                {
                    function_call = new
                    {
                        name = "route_tool_request",
                        arguments = new { action = "call_tool", tool, reason = "Нужны данные Revit" }
                    }
                }
            }
        },
        usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 }
    });

    private sealed class FakeHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
