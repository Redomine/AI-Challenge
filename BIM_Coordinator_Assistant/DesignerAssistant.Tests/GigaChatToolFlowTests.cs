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
    public async Task StructuredResponseRetriesTwoEmptyAnswers()
    {
        var handler = new EmptyStructuredHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));

        var response = await client.GenerateStructuredAsync(
            "instructions",
            [new ChatMessage("user", "Составь план")],
            JsonSerializer.SerializeToElement(new { type = "object" }));

        Assert.Equal("{\"summary\":\"План\"}", response.Content);
        Assert.Equal(3, handler.ChatRequestCount);
    }

    [Fact]
    public async Task ToolResultIsReturnedAsGroundedReport()
    {
        var handler = new ToolFlowHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        var provider = new GenericCountingToolProvider();

        var response = await client.GenerateWithToolsAsync(
            "instructions",
            [new ChatMessage("user", "Прочитай тестовые данные")],
            provider);

        Assert.Equal(1, provider.InvocationCount);
        Assert.Contains("Инструмент: revit_generic_read", response.Content);
        Assert.Contains("\"value\":\"Test\"", response.Content);
        using var finalRequest = JsonDocument.Parse(handler.RequestBodies[^1]);
        Assert.Equal("auto", finalRequest.RootElement.GetProperty("function_call").GetString());
    }

    [Fact]
    public async Task SelectedElementsCanFeedAConfirmedWriteTool()
    {
        var handler = new SelectionWriteFlowHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        var provider = new SelectionWriteToolProvider();

        var response = await client.GenerateWithToolsAsync(
            "Если пользователь говорит о выбранных элементах, сначала прочитай выбор, затем продолжи операцию.",
            [new ChatMessage("user", "У меня выбрано 2 элемента. Запиши им в параметр Комментарии текст Проверка агента")],
            provider);

        Assert.Equal(
            new[] { "revit_get_selected_elements", "revit_set_element_parameter_values" },
            provider.Invocations);
        Assert.Contains("Инструмент: revit_get_selected_elements", response.Content);
        Assert.Contains("\"total_count\":2", response.Content);
        Assert.Contains("Инструмент: revit_set_element_parameter_values", response.Content);
        Assert.Contains("\"updatedCount\":2", response.Content);
    }

    [Fact]
    public async Task EmptyResponseAfterToolRetriesFinalTextWithoutRepeatingTool()
    {
        var handler = new EmptyAfterToolHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        var provider = new GenericCountingToolProvider();
        var traces = new List<string>();

        var response = await client.GenerateWithToolsAsync(
            "instructions",
            [new ChatMessage("user", "Прочитай тестовые данные")],
            provider,
            traces.Add);

        Assert.Contains("Инструмент: revit_generic_read", response.Content);
        Assert.Contains("\"value\":\"Test\"", response.Content);
        Assert.Equal(1, provider.InvocationCount);
        Assert.Equal(2, traces.Count(trace => trace.Contains("Finalization retry", StringComparison.Ordinal)));
        foreach (var body in handler.RequestBodies.TakeLast(2))
        {
            using var request = JsonDocument.Parse(body);
            Assert.Equal("none", request.RootElement.GetProperty("function_call").GetString());
        }
    }

    [Fact]
    public async Task EmptyResponseBeforeToolRetriesWithoutLosingForcedTool()
    {
        var handler = new EmptyBeforeToolHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        var provider = new CountingToolProvider();
        var traces = new List<string>();

        var response = await client.GenerateWithToolsAsync(
            "instructions",
            [new ChatMessage("user", "Что сейчас открыто в Revit?")],
            provider,
            traces.Add);

        Assert.Contains("Инструмент: revit_get_current_view_info", response.Content);
        Assert.Contains("\"viewName\":\"Test\"", response.Content);
        Assert.Equal(1, provider.InvocationCount);
        Assert.Equal(2, traces.Count(trace => trace.Contains("Initial retry", StringComparison.Ordinal)));
        foreach (var body in handler.RequestBodies.Skip(1).Take(3))
        {
            using var request = JsonDocument.Parse(body);
            Assert.Equal(
                "revit_get_current_view_info",
                request.RootElement.GetProperty("function_call").GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task RepeatedEmptyResponsesDirectlyInvokeRoutedToolWithoutRequiredArguments()
    {
        var handler = new DirectRoutedFallbackHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        var provider = new CountingToolProvider();
        var traces = new List<string>();

        var response = await client.GenerateWithToolsAsync(
            "instructions",
            [new ChatMessage("user", "Что сейчас открыто в Revit?")],
            provider,
            traces.Add);

        Assert.Contains("Инструмент: revit_get_current_view_info", response.Content);
        Assert.Contains("\"viewName\":\"Test\"", response.Content);
        Assert.Equal(1, provider.InvocationCount);
        Assert.Contains(traces, trace => trace.Contains("Direct routed fallback", StringComparison.Ordinal));
    }

    private sealed class SelectionWriteToolProvider : IToolProvider
    {
        public List<string> Invocations { get; } = [];

        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>(
            [
                new("revit_get_selected_elements", "Get selected elements", JsonSerializer.SerializeToElement(new { type = "object" })),
                new("revit_set_element_parameter_values", "Set instance parameter values", JsonSerializer.SerializeToElement(new { type = "object" }))
            ]);

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add(name);
            return Task.FromResult(name == "revit_get_selected_elements"
                ? "{\"total_count\":2,\"elements\":[{\"elementId\":101},{\"elementId\":102}]}"
                : "{\"updatedCount\":2,\"failedCount\":0}");
        }
    }

    private sealed class EmptyStructuredHandler : HttpMessageHandler
    {
        public int ChatRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("ngw.devices", StringComparison.Ordinal))
                return Task.FromResult(Json(new { access_token = "token", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() }));
            if (request.RequestUri.AbsolutePath.Contains("tokens/count", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            ChatRequestCount++;
            return Task.FromResult(Json(new
            {
                choices = new[] { new { message = new { content = ChatRequestCount < 3 ? "" : "{\"summary\":\"План\"}" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
            }));
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    private sealed class SelectionWriteFlowHandler : HttpMessageHandler
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
                    choices = new[] { new { message = new { content = "", function_call = new { name = "revit_get_selected_elements", arguments = new { } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                2 => Json(new
                {
                    choices = new[] { new { message = new { content = "", function_call = new { name = "revit_set_element_parameter_values", arguments = new { elementIds = new[] { 101, 102 }, parameterName = "Комментарии", value = "Проверка агента", valueType = "string" } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                _ => Json(new
                {
                    choices = new[] { new { message = new { content = "Параметр записан в 2 элемента." }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                })
            });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
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

    private sealed class GenericCountingToolProvider : IToolProvider
    {
        public int InvocationCount { get; private set; }

        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>(
                [new("revit_generic_read", "Read test data", JsonSerializer.SerializeToElement(new { type = "object" }))]);

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult("{\"value\":\"Test\"}");
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
                    choices = new[] { new { message = new { function_call = new { name = "route_tool_request", arguments = new { action = "call_tool", tool = "revit_generic_read", reason = "Нужны данные" } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                2 => Json(new
                {
                    choices = new[] { new { message = new { content = "", function_call = new { name = "revit_generic_read", arguments = new { } } } } },
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

    private sealed class EmptyAfterToolHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];
        private int _chatRequest;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.Host.Contains("ngw.devices", StringComparison.Ordinal))
                return Json(new { access_token = "token", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() });

            RequestBodies.Add(body);
            _chatRequest++;
            return _chatRequest switch
            {
                1 => Json(new
                {
                    choices = new[] { new { message = new { function_call = new { name = "route_tool_request", arguments = new { action = "call_tool", tool = "revit_generic_read", reason = "Нужны данные" } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                2 => Json(new
                {
                    choices = new[] { new { message = new { content = "", function_call = new { name = "revit_generic_read", arguments = new { } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                3 or 4 => Json(new
                {
                    choices = new[] { new { message = new { content = "" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 0, total_tokens = 10 }
                }),
                _ => Json(new
                {
                    choices = new[] { new { message = new { content = "Данные прочитаны после повтора." }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                })
            };
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    private sealed class EmptyBeforeToolHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];
        private int _chatRequest;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.RequestUri!.Host.Contains("ngw.devices", StringComparison.Ordinal))
                return Json(new { access_token = "token", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() });

            RequestBodies.Add(body);
            _chatRequest++;
            return _chatRequest switch
            {
                1 => Json(new
                {
                    choices = new[] { new { message = new { function_call = new { name = "route_tool_request", arguments = new { action = "call_tool", tool = "revit_get_current_view_info", reason = "Нужны данные" } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                2 or 3 => Json(new
                {
                    choices = new[] { new { message = new { content = "" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 0, total_tokens = 10 }
                }),
                4 => Json(new
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

    private sealed class DirectRoutedFallbackHandler : HttpMessageHandler
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
                    choices = new[] { new { message = new { function_call = new { name = "route_tool_request", arguments = new { action = "call_tool", tool = "revit_get_current_view_info", reason = "Нужны данные" } } } } },
                    usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
                }),
                2 or 3 or 4 => Json(new
                {
                    choices = new[] { new { message = new { content = "" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 10, completion_tokens = 0, total_tokens = 10 }
                }),
                _ => Json(new
                {
                    choices = new[] { new { message = new { content = "Активный вид прочитан напрямую." }, finish_reason = "stop" } },
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
