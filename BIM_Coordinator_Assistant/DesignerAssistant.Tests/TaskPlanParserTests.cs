using System.Text.Json;
using DesignerAssistant.Agent;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class TaskPlanParserTests
{
    [Fact]
    public void AcceptsKnownToolsAndSourcedRequiredArguments()
    {
        var plan = TaskPlanParser.ParseAndValidate(
            """{"summary":"Запись","steps":[{"action":"Записать","tool":"write","arguments":{"value":"Тест"},"argumentSources":{"ids":"шаг 1"}}]}""",
            [Tool("write", "ids", "value")]);

        Assert.Single(plan.Steps);
        Assert.Contains("Инструмент: write", plan.ToDisplayText());
    }

    [Fact]
    public void RejectsInventedTool()
    {
        var error = Assert.Throws<InvalidDataException>(() => TaskPlanParser.ParseAndValidate(
            """{"summary":"Запись","steps":[{"action":"Записать","tool":"invented_api","arguments":{},"argumentSources":{}}]}""",
            [Tool("write")]));

        Assert.Contains("отсутствует", error.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("NULL")]
    [InlineData("none")]
    [InlineData("")]
    public void TreatsTextualNullToolAsNoTool(string toolName)
    {
        var json = JsonSerializer.Serialize(new
        {
            summary = "Ответ",
            steps = new[] { new { action = "Ответить пользователю", tool = toolName, arguments = new { }, argumentSources = new { } } }
        });

        var plan = TaskPlanParser.ParseAndValidate(json, [Tool("write")]);

        Assert.Null(plan.Steps[0].Tool);
        Assert.DoesNotContain("Инструмент:", plan.ToDisplayText());
    }

    [Fact]
    public void RejectsMissingRequiredArgumentAndSource()
    {
        var error = Assert.Throws<InvalidDataException>(() => TaskPlanParser.ParseAndValidate(
            """{"summary":"Запись","steps":[{"action":"Записать","tool":"write","arguments":{"value":"Тест"},"argumentSources":{}}]}""",
            [Tool("write", "ids", "value")]));

        Assert.Contains("ids", error.Message);
    }

    [Fact]
    public void AcceptsClarificationInsteadOfExecutableSteps()
    {
        var plan = TaskPlanParser.ParseAndValidate(
            """{"summary":"Уточнить значение","clarification":"Какое значение записать?","steps":[]}""",
            [Tool("write")]);

        Assert.Empty(plan.Steps);
        Assert.Equal("[CLARIFY] Какое значение записать?", plan.ToDisplayText());
    }

    [Fact]
    public void RejectsTranslatedAbsolutePathFromUserQuery()
    {
        const string query = @"Открой C:\Users\Example\Desktop\_Тесты\Тестовый файл.rvt";
        const string json = """
            {"summary":"Открыть модель","steps":[{"action":"Открыть","tool":"open","arguments":{"path":"C:\\Users\\Example\\Desktop\\_Tests\\Тестовый файл.rvt"},"argumentSources":{}}]}
            """;

        var error = Assert.Throws<InvalidDataException>(() =>
            TaskPlanParser.ParseAndValidate(json, [Tool("open", "path")], query));

        Assert.Contains("изменён относительно запроса", error.Message);
    }

    [Fact]
    public void AcceptsExactCyrillicAbsolutePathFromUserQuery()
    {
        const string path = @"C:\Users\Example\Desktop\_Тесты\Тестовый файл.rvt";
        var json = JsonSerializer.Serialize(new
        {
            summary = "Открыть модель",
            steps = new[] { new { action = "Открыть", tool = "open", arguments = new { path }, argumentSources = new { } } }
        });

        var plan = TaskPlanParser.ParseAndValidate(json, [Tool("open", "path")], $"Открой модель {path}");

        Assert.Equal(path, plan.Steps[0].Arguments["path"].GetString());
    }

    [Fact]
    public void RemovesWorkspacePreflightForSameExternalModel()
    {
        const string path = @"C:\Models\Test.rvt";
        var json = JsonSerializer.Serialize(new
        {
            summary = "Открыть модель",
            steps = new object[]
            {
                new { action = "Проверить файл", tool = "workspace_path_exists", arguments = new { path }, argumentSources = new { } },
                new { action = "Открыть", tool = "revit_custom_open_model", arguments = new { path }, argumentSources = new { } }
            }
        });

        var plan = TaskPlanParser.ParseAndValidate(json,
            [Tool("workspace_path_exists", "path"), Tool("revit_custom_open_model", "path")], $"Открой {path}");

        Assert.Equal("revit_custom_open_model", Assert.Single(plan.Steps).Tool);
    }

    [Fact]
    public void RejectsExternalWorkspacePathWithoutMatchingModelOpen()
    {
        const string path = @"C:\Models\Test.rvt";
        var json = JsonSerializer.Serialize(new
        {
            summary = "Проверить файл",
            steps = new[] { new { action = "Проверить", tool = "workspace_path_exists", arguments = new { path }, argumentSources = new { } } }
        });

        var error = Assert.Throws<InvalidDataException>(() => TaskPlanParser.ParseAndValidate(json,
            [Tool("workspace_path_exists", "path")], $"Проверь {path}"));

        Assert.Contains("относительный путь", error.Message);
    }

    private static ToolDefinition Tool(string name, params string[] required) => new(
        name,
        "Тест",
        JsonSerializer.SerializeToElement(new { type = "object", required }));
}
