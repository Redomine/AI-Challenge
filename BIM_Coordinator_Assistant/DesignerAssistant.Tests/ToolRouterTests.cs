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
    public async Task RoutesExplicitElementIdWithoutLlmRoundTrip()
    {
        using var http = new HttpClient(new FakeHandler(Response("revit_get_current_view_info")));
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));

        var decision = await router.RouteAsync(
            [new ChatMessage("user", "5133824 что за элемент?")],
            [Tool("revit_get_element_details"), Tool("revit_get_current_view_info")]);

        Assert.Equal("call_tool", decision.Action);
        Assert.Equal("revit_get_element_details", decision.ToolName);
        Assert.Equal(0, decision.BilledTokens);
    }

    [Fact]
    public async Task RoutesActiveViewQuestionToCurrentViewInfo()
    {
        using var http = new HttpClient(new FakeHandler(Response("revit_get_element_details")));
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));

        var decision = await router.RouteAsync(
            [new ChatMessage("user", "Что за элементы есть на активном виде?")],
            [Tool("revit_get_element_details"), Tool("revit_get_current_view_info")]);

        Assert.Equal("revit_get_current_view_info", decision.ToolName);
        Assert.Equal(0, decision.BilledTokens);
    }

    [Fact]
    public async Task ToolCatalogueQuestionIsPresentedAsAnswerOnlyIntent()
    {
        var handler = new FakeHandler(AnswerResponse());
        using var http = new HttpClient(handler);
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));

        var decision = await router.RouteAsync(
            [new ChatMessage("user", "Дай название этих инструментов в виде команд для их вызова")],
            [Tool("revit_delete_element"), Tool("revit_get_element_details")]);

        Assert.Equal("answer", decision.Action);
        Assert.Null(decision.ToolName);
        using var request = JsonDocument.Parse(handler.LastRequestBody);
        var routerInstructions = request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.Contains("для них всегда выбирай answer", routerInstructions);
        Assert.Contains("revit_delete_element", handler.LastRequestBody);
    }

    [Fact]
    public async Task RoutesArbitraryWordingToKnownTool()
    {
        using var http = new HttpClient(new FakeHandler(Response("revit_get_selected_elements")));
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));
        var decision = await router.RouteAsync(
            [new ChatMessage("user", "Что у меня сейчас отмечено в модели?")],
            [Tool("revit_get_selected_elements")]);

        Assert.Equal("call_tool", decision.Action);
        Assert.Equal("revit_get_selected_elements", decision.ToolName);
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

    private static string AnswerResponse() => JsonSerializer.Serialize(new
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
                        arguments = new { action = "answer", reason = "Ответ уже есть в каталоге" }
                    }
                }
            }
        },
        usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 }
    });

    private sealed class FakeHandler(string body) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
