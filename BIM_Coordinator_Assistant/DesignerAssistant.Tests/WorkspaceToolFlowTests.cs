using System.Net;
using System.Text;
using System.Text.Json;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Tools;

namespace DesignerAssistant.Tests;

public sealed class WorkspaceToolFlowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"designer-assistant-flow-{Guid.NewGuid():N}");

    public WorkspaceToolFlowTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "result.txt"), "Фактический результат теста: успешно");
    }

    [Fact]
    public async Task ExistenceResultFeedsFileReadAndFinalGroundedAnswer()
    {
        var handler = new WorkspaceFlowHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 200, "test.db", 10000, 0, 10));
        var provider = new WorkspaceToolProvider(_root);
        var traces = new List<string>();

        var response = await client.GenerateWithToolsAsync(
            "Проверяй результаты только инструментами.",
            [new ChatMessage("user", "Получи сохранённый отчёт result.txt из workspace")],
            provider,
            traces.Add);

        Assert.Contains("успешно", response.Content);
        Assert.Contains(traces, item => item.Contains("workspace_path_exists", StringComparison.Ordinal));
        Assert.Contains(traces, item => item.Contains("workspace_read_text_file", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class WorkspaceFlowHandler : HttpMessageHandler
    {
        private int _chatRequest;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("ngw.devices", StringComparison.Ordinal))
                return Task.FromResult(Json(new { access_token = "token", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }));

            _chatRequest++;
            return Task.FromResult(_chatRequest switch
            {
                1 => Json(new
                {
                    choices = new[] { new { message = new { content = "", function_call = new { name = "workspace_path_exists", arguments = new { path = "result.txt" } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                2 => Json(new
                {
                    choices = new[] { new { message = new { content = "", function_call = new { name = "workspace_read_text_file", arguments = new { path = "result.txt" } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                _ => Json(new
                {
                    choices = new[] { new { message = new { content = "Фактический результат теста: успешно" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                })
            });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }
}
