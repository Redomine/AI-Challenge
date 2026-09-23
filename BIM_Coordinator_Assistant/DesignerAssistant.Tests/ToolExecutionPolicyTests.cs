using System.Text.Json;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tests;

public sealed class ToolExecutionPolicyTests
{
    [Fact]
    public async Task UnknownToolIsBlockedWithoutInvocation()
    {
        var provider = new StubProvider();
        var policy = new ToolExecutionPolicy(provider, await provider.GetToolsAsync());

        var result = await policy.ExecuteAsync("missing_tool", Json("{}"));

        Assert.False(result.Ok);
        Assert.Equal("unknown_tool", result.Error?.Code);
        Assert.Equal(0, provider.InvocationCount);
    }

    [Fact]
    public async Task RequiredEmptyArgumentIsBlocked()
    {
        var provider = new StubProvider(required: true);
        var policy = new ToolExecutionPolicy(provider, await provider.GetToolsAsync());

        var result = await policy.ExecuteAsync("revit_get_current_view_info", Json("{\"path\":\"\"}"));

        Assert.Equal("invalid_arguments", result.Error?.Code);
        Assert.Equal(0, provider.InvocationCount);
    }

    [Fact]
    public async Task CyrillicAbsolutePathIsPassedUnchanged()
    {
        var provider = new StubProvider();
        var policy = new ToolExecutionPolicy(provider, await provider.GetToolsAsync());
        const string path = @"C:\Проекты\ОВ\Модель.rvt";

        var result = await policy.ExecuteAsync("revit_get_current_view_info", Json(JsonSerializer.Serialize(new { path })));

        Assert.True(result.Ok);
        Assert.Equal(path, provider.LastArguments.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("posted")]
    [InlineData("running")]
    public async Task AsyncIntermediateStatusIsNotCompleted(string status)
    {
        var provider = new StubProvider(result: JsonSerializer.Serialize(new { status, runId = "run-1" }));
        var policy = new ToolExecutionPolicy(provider, await provider.GetToolsAsync());

        var result = await policy.ExecuteAsync("revit_get_current_view_info", Json("{}"));

        Assert.True(result.Ok);
        Assert.False(result.Completed);
    }

    [Fact]
    public async Task FailedCallCannotRepeatWithSameArguments()
    {
        var provider = new StubProvider(result: "{\"ok\":false,\"message\":\"boom\"}");
        var policy = new ToolExecutionPolicy(provider, await provider.GetToolsAsync());

        var first = await policy.ExecuteAsync("revit_get_current_view_info", Json("{}"));
        var second = await policy.ExecuteAsync("revit_get_current_view_info", Json("{}"));

        Assert.Equal("tool_error", first.Error?.Code);
        Assert.Equal("duplicate_failed_call", second.Error?.Code);
        Assert.Equal(1, provider.InvocationCount);
    }

    [Fact]
    public async Task CancelledConfirmationDoesNotInvokeMutation()
    {
        var provider = new StubProvider(toolName: "revit_custom_execute_pyrevit_command", confirm: false);
        var policy = new ToolExecutionPolicy(provider, await provider.GetToolsAsync());

        var result = await policy.ExecuteAsync(
            "revit_custom_execute_pyrevit_command",
            Json("{\"commandPath\":\"C:\\\\Команды\\\\Run.pushbutton\"}"));

        Assert.Equal("cancelled", result.Error?.Code);
        Assert.Equal(0, provider.InvocationCount);
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class StubProvider(
        string toolName = "revit_get_current_view_info",
        bool required = false,
        string result = "{\"ok\":true}",
        bool confirm = true) : IToolProvider, IToolConfirmationProvider
    {
        public int InvocationCount { get; private set; }
        public JsonElement LastArguments { get; private set; }

        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>([new(toolName, "test", JsonSerializer.SerializeToElement(new
            {
                type = "object",
                required = required ? new[] { "path" } : Array.Empty<string>()
            }))]);

        public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            LastArguments = arguments.Clone();
            return Task.FromResult(result);
        }

        public Task<bool> ConfirmAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult(confirm);
    }
}
