using System.Net;
using System.Text;
using System.Text.Json;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class ModelAnalysisToolFlowTests
{
    [Fact]
    public async Task SequentialFiltersPassRealSelectionIdsAndOnlyBoundedToonReachesModel()
    {
        var handler = new ModelAnalysisHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        var provider = new ModelAnalysisProvider();

        var response = await client.GenerateWithToolsAsync(
            "Проверяй модель последовательно.",
            [new ChatMessage("user", "Проверь два параметра в 15 инженерных категориях и сохрани отчёт")],
            provider);

        Assert.Equal(new[]
        {
            "revit_custom_collect_mep_elements", "revit_custom_filter_selection",
            "revit_custom_filter_selection", "revit_custom_summarize_selection",
            "revit_custom_export_selection_json"
        }, provider.Invocations);
        Assert.Equal("Найдено 7 элементов. Отчёт сохранён.", response.Content);
        using var firstRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("auto", firstRequest.RootElement.GetProperty("function_call").GetString());
        Assert.Contains("selection-a", handler.ToolMessages[0]);
        Assert.Equal(4, firstRequest.RootElement.GetProperty("functions").GetArrayLength());
        Assert.All(handler.ToolMessages, content => Assert.Contains("TOON result", content));
        Assert.DoesNotContain(handler.RequestBodies, body => body.Contains("\"elements\":[", StringComparison.Ordinal));
        Assert.Contains("recordCount: 7", handler.ToolMessages[^1]);
    }

    [Fact]
    public void ExportContentCannotEnterModelContext()
    {
        var result = Result("revit_custom_export_selection_json", new
        {
            path = "report.json", elements = new[] { new { elementId = 12345 } }
        });
        Assert.Throws<InvalidDataException>(() => ModelAnalysisResultFormatter.Format(result));
    }

    [Fact]
    public void OversizedPageCannotEnterModelContext()
    {
        var result = Result("revit_custom_get_selection_page", new
        {
            items = Enumerable.Range(1, 21).Select(id => new { elementId = id }).ToArray()
        });
        Assert.Throws<InvalidDataException>(() => ModelAnalysisResultFormatter.Format(result));
    }

    [Fact]
    public void SelectionPageWithExactlyTwentyItemsIsAccepted()
    {
        var result = Result("revit_custom_get_selection_page", new
        {
            items = Enumerable.Range(1, 20).Select(id => new { elementId = id }).ToArray()
        });
        var formatted = ModelAnalysisResultFormatter.Format(result);
        Assert.Contains("TOON result", formatted);
        using var parsed = JsonDocument.Parse(formatted);
        Assert.Equal("TOON result", parsed.RootElement.GetProperty("format").GetString());
    }

    [Theory]
    [InlineData("nullOrEmpty", 18, 641, "emptyCount")]
    [InlineData("notNullOrEmpty", 641, 18, "filledCount")]
    public void FilterResultNamesEmptyAndFilledCounts(string operation, int empty, int filled, string categoryMeaning)
    {
        var result = new ToolResultEnvelope(
            true, "revit_custom_filter_selection",
            JsonSerializer.SerializeToElement(new { parameterName = "Параметр", @operator = operation }),
            JsonSerializer.SerializeToElement(new { matched = 18, notMatched = 641,
                matchedByCategory = new[] { new { builtInCategory = "OST_DuctFitting", count = 5 },
                    new { builtInCategory = "OST_PipeAccessory", count = 13 } } }),
            null, false, false, 1, true);

        using var formatted = JsonDocument.Parse(ModelAnalysisResultFormatter.Format(result));
        var interpretation = formatted.RootElement.GetProperty("interpretation");
        Assert.Equal(empty, interpretation.GetProperty("emptyCount").GetInt32());
        Assert.Equal(filled, interpretation.GetProperty("filledCount").GetInt32());
        Assert.Equal(categoryMeaning, interpretation.GetProperty("matchedByCategoryAppliesTo").GetString());
        var largest = interpretation.GetProperty(operation == "nullOrEmpty"
            ? "largestEmptyCategory" : "largestFilledCategory");
        Assert.Equal("OST_PipeAccessory", largest.GetProperty("builtInCategory").GetString());
        Assert.Equal(13, largest.GetProperty("count").GetInt32());
        Assert.Contains("matchedByCategory", formatted.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public async Task RefusalToCallFilterDoesNotClaimParameterCheckSucceeded()
    {
        var (handler, provider) = BuildAnswerOnlyHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));

        var response = await client.GenerateWithToolsAsync(
            "Проверяй модель.",
            [new ChatMessage("user", "Проверь заполненность параметра в 15 инженерных категориях")],
            provider);

        Assert.Equal(["revit_custom_collect_mep_elements"], provider.Invocations);
        Assert.Equal("tool_flow_incomplete", response.FinishReason);
        Assert.Contains("не состоялась", response.Content);
        Assert.Contains("TOON result", handler.RequestBodies[0]);
        Assert.DoesNotContain("Готово", response.Content);
        using var request = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("Проверяй модель.", request.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        var filter = request.RootElement.GetProperty("functions").EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "revit_custom_filter_selection");
        Assert.Contains("matchedByCategory", filter.GetProperty("description").GetString());
    }

    [Theory]
    [InlineData("Покажи текущую выборку")]
    [InlineData("Покажи выборку")]
    [InlineData("Покажи заполненность проекта")]
    [InlineData("Покажи текущую заполненность")]
    [InlineData("Покажи заполненность")]
    [InlineData("Что у меня сейчас отмечено в модели?")]
    public async Task ShowSelectionOrCompletenessDoesNotLimitCatalogue(string question)
    {
        var (handler, provider) = BuildAnswerOnlyHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));

        _ = await client.GenerateWithToolsAsync(
            "Системные инструкции.",
            [new ChatMessage("user", question)],
            provider);

        using var firstRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        var functionNames = firstRequest.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();
        Assert.Equal(8, functionNames.Length);
        Assert.Contains("revit_get_selected_elements", functionNames);
        Assert.Contains("revit_analyze_model_statistics", functionNames);
        Assert.Contains("revit_custom_collect_mep_elements", functionNames);
        Assert.Contains("revit_custom_export_selection_json", functionNames);
        Assert.Empty(provider.Invocations);
    }

    [Theory]
    [InlineData("Проверь два параметра в 15 инженерных категориях и сохрани отчёт")]
    [InlineData("Проверь параметры в инженерных категориях")]
    [InlineData("Собери инженерные категории")]
    [InlineData("Проанализируй проект по категориям")]
    [InlineData("Проверь заполненность параметров в категориях")]
    [InlineData("Во всех 15 инженерных категориях текущей модели проверь заполненность параметров")]
    public async Task BulkCheckVerbsStillRestrictCatalogueToModelAnalysis(string question)
    {
        var (handler, provider) = BuildAnswerOnlyHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));

        _ = await client.GenerateWithToolsAsync(
            "Системные инструкции.",
            [new ChatMessage("user", question)],
            provider);

        using var firstRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        var functionNames = firstRequest.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();
        Assert.Equal(6, functionNames.Length);
        Assert.DoesNotContain("revit_get_selected_elements", functionNames);
        Assert.DoesNotContain("revit_analyze_model_statistics", functionNames);
        Assert.Contains("revit_custom_collect_mep_elements", functionNames);
        Assert.Contains("revit_custom_collect_category_elements", functionNames);
        Assert.Contains("revit_custom_filter_selection", functionNames);
        Assert.Contains("revit_custom_summarize_selection", functionNames);
        Assert.Contains("revit_custom_get_selection_page", functionNames);
        Assert.Contains("revit_custom_export_selection_json", functionNames);
        Assert.Equal(question.Contains("15 инженерных категориях", StringComparison.Ordinal)
            ? ["revit_custom_collect_mep_elements"] : Array.Empty<string>(), provider.Invocations);
    }

    [Theory]
    [InlineData("Проверь параметры в инженерных категориях и запиши значения")]
    [InlineData("Проверь категории и измени значения")]
    [InlineData("Открой файл и проверь инженерные категории")]
    public async Task WriteKeywordsDisarmModelAnalysisRestriction(string question)
    {
        var (handler, provider) = BuildAnswerOnlyHandler();
        using var http = new HttpClient(handler);
        var client = new GigaChatClient(http, new AppOptions(
            "key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));

        _ = await client.GenerateWithToolsAsync(
            "Системные инструкции.",
            [new ChatMessage("user", question)],
            provider);

        using var firstRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        var functionNames = firstRequest.RootElement.GetProperty("functions")
            .EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString())
            .ToArray();
        Assert.Equal(8, functionNames.Length);
        Assert.Empty(provider.Invocations);
    }

    private static (RecordingHandler Handler, CountingProvider Provider) BuildAnswerOnlyHandler()
    {
        var provider = new CountingProvider();
        var handler = new RecordingHandler(provider);
        return (handler, provider);
    }

    private static ToolResultEnvelope Result(string tool, object payload) => new(
        true, tool, JsonSerializer.SerializeToElement(new { }), JsonSerializer.SerializeToElement(payload),
        null, false, false, 1, true);

    private sealed class ModelAnalysisProvider : IToolProvider
    {
        public List<string> Invocations { get; } = [];

        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>(
            [
                Tool("revit_custom_collect_mep_elements"), Tool("revit_custom_filter_selection"),
                Tool("revit_custom_summarize_selection"), Tool("revit_custom_export_selection_json")
            ]);

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add(name);
            var result = name switch
            {
                "revit_custom_collect_mep_elements" => new { selectionId = "selection-a", totalCount = 1000 },
                "revit_custom_filter_selection" when Invocations.Count == 2 =>
                    CheckFilter(arguments, "selection-a", "selection-b", 45),
                "revit_custom_filter_selection" when Invocations.Count == 3 =>
                    CheckFilter(arguments, "selection-b", "selection-c", 7),
                "revit_custom_summarize_selection" => CheckSummary(arguments),
                "revit_custom_export_selection_json" => CheckExport(arguments),
                _ => throw new InvalidOperationException(name)
            };
            return Task.FromResult(JsonSerializer.Serialize(result));
        }

        private static object CheckFilter(JsonElement arguments, string expectedSource, string newId, int count)
        {
            Assert.Equal(expectedSource, arguments.GetProperty("selectionId").GetString());
            return new { selectionId = newId, matched = count, missing = 0, ambiguous = 0 };
        }

        private static object CheckSummary(JsonElement arguments)
        {
            Assert.Equal("selection-c", arguments.GetProperty("selectionId").GetString());
            return new { selectionId = "selection-c", totalCount = 7 };
        }

        private static object CheckExport(JsonElement arguments)
        {
            Assert.Equal("selection-c", arguments.GetProperty("selectionId").GetString());
            Assert.Equal("check-1", arguments.GetProperty("taskId").GetString());
            return new { path = "C:\\Reports\\check-1\\selection.json", recordCount = 7, bytes = 1200, sha256 = "abc" };
        }

        private static ToolDefinition Tool(string name) =>
            new(name, name, JsonSerializer.SerializeToElement(new { type = "object" }));
    }

    private sealed class ModelAnalysisHandler : HttpMessageHandler
    {
        private int _chatCount;
        public List<string> RequestBodies { get; } = [];
        public List<string> ToolMessages { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("ngw.devices", StringComparison.Ordinal))
                return Json(new { access_token = "token", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() });

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            using var document = JsonDocument.Parse(body);
            ToolMessages.AddRange(document.RootElement.GetProperty("messages").EnumerateArray()
                .Where(item => item.GetProperty("role").GetString() == "function")
                .Select(item => item.GetProperty("content").GetString() ?? "")
                .Skip(ToolMessages.Count));
            _chatCount++;
            return _chatCount switch
            {
                1 => Call("revit_custom_filter_selection", new { selectionId = "selection-a", parameterName = "Имя системы", @operator = "notEquals", value = "Не определено" }),
                2 => Call("revit_custom_filter_selection", new { selectionId = "selection-b", parameterName = "Имя системы принудительное", @operator = "notNullOrEmpty" }),
                3 => Call("revit_custom_summarize_selection", new { selectionId = "selection-c" }),
                4 => Call("revit_custom_export_selection_json", new { selectionId = "selection-c", taskId = "check-1" }),
                _ => Json(new { choices = new[] { new { message = new { content = "Найдено 7 элементов. Отчёт сохранён." }, finish_reason = "stop" } }, usage = Usage() })
            };
        }

        private static HttpResponseMessage Call(string name, object arguments) => Json(new
        {
            choices = new[] { new { message = new { content = "", function_call = new { name, arguments } } } },
            usage = Usage()
        });

        private static object Usage() => new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 };
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    private sealed class CountingProvider : IToolProvider
    {
        public List<string> Invocations { get; } = [];

        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>(
            [
                Tool("revit_custom_collect_mep_elements"),
                Tool("revit_custom_collect_category_elements"),
                Tool("revit_custom_filter_selection"),
                Tool("revit_custom_summarize_selection"),
                Tool("revit_custom_get_selection_page"),
                Tool("revit_custom_export_selection_json"),
                Tool("revit_get_selected_elements"),
                Tool("revit_analyze_model_statistics")
            ]);

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            Invocations.Add(name);
            if (name == "revit_custom_collect_mep_elements")
                return Task.FromResult(JsonSerializer.Serialize(new { selectionId = "selection-a", totalCount = 10 }));
            throw new InvalidOperationException($"Catalog-restriction tests must not invoke tools; saw {name}.");
        }
    }

    private sealed class RecordingHandler(CountingProvider provider) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("ngw.devices", StringComparison.Ordinal))
                return Json(new { access_token = "token", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds() });

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            _ = provider;
            return Json(new
            {
                choices = new[] { new { message = new { content = "Готово." }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 5, completion_tokens = 3, total_tokens = 8 }
            });
        }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };
    }

    private static ToolDefinition Tool(string name) =>
        new(name, name, JsonSerializer.SerializeToElement(new { type = "object" }));
}
