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

    [Theory]
    [InlineData("Какие элементы есть на активном виде?")]
    [InlineData("Что находится на активном виде?")]
    [InlineData("Покажи элементы текущего вида")]
    [InlineData("Перечисли объекты на открытом виде")]
    [InlineData("Сколько элементов на этом виде?")]
    [InlineData("Дай сводку по категориям на активном виде")]
    [InlineData("Какой состав элементов текущего вида?")]
    [InlineData("Что видно на этом виде из оборудования?")]
    [InlineData("Какие категории находятся на активном виде?")]
    [InlineData("Покажи воздуховоды и трубы на текущем виде")]
    public async Task RoutesViewInventoryQuestionsToCustomSummary(string question)
    {
        using var http = new HttpClient(new FakeHandler(Response("revit_get_element_details")));
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));

        var decision = await router.RouteAsync(
            [new ChatMessage("user", question)],
            [Tool("revit_get_current_view_info"), Tool("revit_custom_summarize_elements")]);

        Assert.Equal("revit_custom_summarize_elements", decision.ToolName);
        Assert.Equal(0, decision.BilledTokens);
    }

    [Fact]
    public async Task ViewInventoryQuestionDoesNotGuessWhenCustomToolIsUnavailable()
    {
        var handler = new FakeHandler(Response("revit_get_current_view_info"));
        using var http = new HttpClient(handler);
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));

        var decision = await router.RouteAsync(
            [new ChatMessage("user", "Какие элементы есть на активном виде?")],
            [Tool("revit_get_current_view_info")]);

        Assert.Equal("answer", decision.Action);
        Assert.Null(decision.ToolName);
        Assert.Contains("не буду угадывать", decision.DirectResponse);
        Assert.Equal(0, handler.RequestCount);
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
        Assert.Equal(0, decision.BilledTokens);
        Assert.Equal(0, handler.RequestCount);
        Assert.Contains("Получить сведения об элементе", decision.DirectResponse);
    }

    [Fact]
    public async Task RoutesMarkedElementsToSelectionWithoutLlmRoundTrip()
    {
        var handler = new FakeHandler(Response("revit_get_selected_elements"));
        using var http = new HttpClient(handler);
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));
        var decision = await router.RouteAsync(
            [new ChatMessage("user", "Что у меня сейчас отмечено в модели?")],
            [Tool("revit_get_selected_elements")]);

        Assert.Equal("call_tool", decision.Action);
        Assert.Equal("revit_get_selected_elements", decision.ToolName);
        Assert.Equal(0, decision.BilledTokens);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("Что за элементы выбраны?")]
    [InlineData("Какие объекты сейчас выделены?")]
    [InlineData("У меня выбрано 7 элементов. Запиши им комментарий")]
    [InlineData("Покажи текущий выбор в Revit")]
    [InlineData("Что находится в выборе?")]
    [InlineData("Какие элементы отмечены в модели?")]
    public async Task RoutesSelectionRequestsWithoutAskingForElementIds(string question)
    {
        var handler = new FakeHandler(Response("revit_get_element_details"));
        using var http = new HttpClient(handler);
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));

        var decision = await router.RouteAsync(
            [new ChatMessage("user", question)],
            [Tool("revit_get_selected_elements"), Tool("revit_set_element_parameter_values")]);

        Assert.Equal("call_tool", decision.Action);
        Assert.Equal("revit_get_selected_elements", decision.ToolName);
        Assert.Equal(0, handler.RequestCount);
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

    [Theory]
    [InlineData("Перечисли свой функционал", "Сейчас мне доступны")]
    [InlineData("Что ты умеешь?", "Сейчас мне доступны")]
    [InlineData("Какие инструменты доступны?", "Сейчас мне доступны")]
    [InlineData("Расскажи о своих возможностях", "Сейчас мне доступны")]
    [InlineData("Покажи список инструментов", "Сейчас мне доступны")]
    [InlineData("Какие у тебя есть возможности?", "Сейчас мне доступны")]
    [InlineData("Чем ты можешь помочь в Revit?", "Сейчас мне доступны")]
    [InlineData("Какой функционал тебе доступен?", "Сейчас мне доступны")]
    [InlineData("Опиши доступные операции", "Сейчас мне доступны")]
    [InlineData("Расскажи, какие команды ты поддерживаешь", "Сейчас мне доступны")]
    [InlineData("На какие действия ты способен?", "Сейчас мне доступны")]
    [InlineData("Что доступно из инструментов Revit?", "Сейчас мне доступны")]
    [InlineData("Что можно сделать с этим видом?", "С активным видом")]
    [InlineData("Что ты умеешь делать с активным видом?", "С активным видом")]
    [InlineData("Какие инструменты доступны для текущего вида?", "С активным видом")]
    [InlineData("Перечисли возможности работы с видом", "С активным видом")]
    [InlineData("Какие операции над активным видом ты поддерживаешь?", "С активным видом")]
    [InlineData("Чем ты можешь помочь на этом виде?", "С активным видом")]
    [InlineData("Покажи функционал для текущего вида", "С активным видом")]
    [InlineData("Что доступно для открытого вида через инструменты?", "С активным видом")]
    [InlineData("Расскажи, что умеешь делать с видом Revit", "С активным видом")]
    [InlineData("Какие у тебя есть возможности для этого вида?", "С активным видом")]
    [InlineData("Что можно сделать с этим элементом через инструменты?", "С элементами")]
    [InlineData("Что ты умеешь делать с элементами?", "С элементами")]
    [InlineData("Какие инструменты доступны для выбранного объекта?", "С элементами")]
    [InlineData("Перечисли возможности работы с элементом", "С элементами")]
    [InlineData("Какие операции над этим объектом ты поддерживаешь?", "С элементами")]
    [InlineData("Чем ты можешь помочь с выделенными элементами?", "С элементами")]
    [InlineData("Покажи функционал для элемента Revit", "С элементами")]
    [InlineData("Что доступно для этого объекта через твои инструменты?", "С элементами")]
    [InlineData("Расскажи, что умеешь делать с параметрами элемента", "С элементами")]
    [InlineData("Какие у тебя есть возможности для выбранных объектов?", "С элементами")]
    [InlineData("Какие действия можно выполнить с элементом?", "С элементами")]
    [InlineData("Опиши доступные инструменты для параметров объекта", "С элементами")]
    public async Task CapabilityQuestionsAreAnsweredLocally(
        string question,
        string expectedHeading)
    {
        var handler = new FakeHandler(AnswerResponse());
        using var http = new HttpClient(handler);
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));

        var decision = await router.RouteAsync(
            [new ChatMessage("user", question)],
            [
                Tool("revit_get_current_view_info"),
                Tool("revit_get_selected_elements"),
                Tool("revit_get_element_details"),
                Tool("revit_get_element_parameters"),
                Tool("revit_analyze_model_statistics")
            ]);

        Assert.Equal("answer", decision.Action);
        Assert.Null(decision.ToolName);
        Assert.Equal(0, decision.BilledTokens);
        Assert.Equal(0, handler.RequestCount);
        Assert.Contains(expectedHeading, decision.DirectResponse);
    }

    [Fact]
    public async Task AmbiguousWriteRequestReturnsClarification()
    {
        var handler = new FakeHandler(ClarifyResponse());
        using var http = new HttpClient(handler);
        var router = new ToolRouter(http, Options(), _ => Task.FromResult("token"));

        var decision = await router.RouteAsync(
            [new ChatMessage("user", "Поменяй это")],
            [Tool("revit_set_parameter")]);

        Assert.Equal("clarify", decision.Action);
        Assert.Null(decision.ToolName);
        Assert.Contains("что изменить", decision.DirectResponse);
    }

    [Fact]
    public void UnknownDiscoveredToolGetsReadableSafeDescription()
    {
        var capability = Assert.Single(ToolCapabilityCatalog.Describe(
            [Tool("revit_create_wall_by_points")]));

        Assert.True(capability.IsWrite);
        Assert.Equal("Изменение модели", capability.Category);
        Assert.Equal("Инструмент revit_create_wall_by_points", capability.Title);
        Assert.Equal("Получить сведения.", capability.Description);
    }

    [Fact]
    public void CurrentAssistantToolSurfaceHasCuratedReadableDescriptions()
    {
        string[] names =
        [
            "revit_list_available_targets", "revit_get_current_target", "revit_switch_target",
            "revit_get_current_view_info", "revit_get_selected_elements", "revit_get_element_details",
            "revit_get_element_parameters", "revit_get_type_parameters", "revit_list_worksets",
            "revit_analyze_model_statistics", "revit_ai_element_filter", "revit_get_available_family_types",
            "revit_get_material_quantities", "revit_get_element_relationships", "revit_list_groups",
            "revit_get_group_members", "revit_list_assemblies", "revit_get_assembly_members",
            "revit_list_project_parameters", "revit_custom_summarize_elements", "revit_custom_list_elements",
            "revit_create_line_based_element", "revit_create_point_based_element",
            "revit_create_surface_based_element", "revit_create_level", "revit_create_grid",
            "revit_create_room", "revit_create_group_from_elements", "revit_operate_element",
            "revit_color_elements", "revit_set_element_parameter_values", "revit_set_type_parameter_values",
            "revit_change_element_type", "revit_assign_elements_to_workset", "revit_delete_element",
            "revit_create_view", "revit_place_view_on_sheet", "revit_analyze_sheet_layout",
            "revit_capture_view_image", "revit_set_view_crop", "revit_set_view_scale",
            "revit_activate_view", "revit_show_element_in_view", "revit_analyze_usage_patterns",
            "revit_batch_execute", "revit_purge_unused", "revit_send_code_to_revit",
            "revit_set_project_info", "revit_show_message"
        ];

        var capabilities = ToolCapabilityCatalog.Describe(names.Select(Tool).ToArray());

        Assert.Equal(49, capabilities.Count);
        Assert.All(capabilities, capability =>
        {
            Assert.DoesNotContain("Инструмент revit_", capability.Title);
            Assert.DoesNotContain("Выполняет операцию", capability.Description);
            Assert.DoesNotContain("данные операции", capability.Description);
            Assert.DoesNotContain("элемент элемент", capability.Title, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(capability.Example));
        });
    }

    [Theory]
    [InlineData("revit_get_group_members")]
    [InlineData("revit_list_project_parameters")]
    [InlineData("revit_analyze_sheet_layout")]
    public void DiscoveredQueryToolsAreClassifiedAsReadOnly(string name)
    {
        Assert.False(ToolCapabilityCatalog.IsWriteTool(Tool(name)));
    }

    [Theory]
    [InlineData("revit_create_room")]
    [InlineData("revit_set_view_scale")]
    [InlineData("revit_operate_element")]
    [InlineData("revit_unrecognized_action")]
    public void DiscoveredActionToolsRequireConfirmation(string name)
    {
        Assert.True(ToolCapabilityCatalog.IsWriteTool(Tool(name)));
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

    private static string ClarifyResponse() => JsonSerializer.Serialize(new
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
                        arguments = new
                        {
                            action = "clarify",
                            reason = "Не определены объект и значение",
                            clarification = "Уточните, что изменить и у какого элемента?"
                        }
                    }
                }
            }
        },
        usage = new { prompt_tokens = 20, completion_tokens = 10, total_tokens = 30 }
    });

    private sealed class FakeHandler(string body) : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = "";
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
