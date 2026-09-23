using System.Text.Json;
using DesignerAssistant.Revit;

namespace DesignerAssistant.Tests;

public sealed class RevitOperationCoordinatorTests
{
    [Fact]
    public async Task PollsUntilCompletedAndUnwrapsResult()
    {
        var replies = new Queue<string>(
        [
            "{\"status\":\"running\",\"runId\":\"run-1\"}",
            "{\"status\":\"completed\",\"runId\":\"run-1\",\"result\":{\"localPath\":\"C:\\\\Models\\\\Local.rvt\"}}"
        ]);
        var calls = 0;
        var coordinator = new RevitOperationCoordinator((name, arguments, _) =>
        {
            calls++;
            Assert.Equal("revit_custom_get_bridge_operation", name);
            Assert.Equal("run-1", JsonSerializer.SerializeToElement(arguments).GetProperty("runId").GetString());
            return Task.FromResult(replies.Dequeue());
        }, TimeSpan.FromMilliseconds(1));

        var result = await coordinator.WaitForCompletionAsync(
            "revit_custom_open_model",
            "{\"status\":\"queued\",\"runId\":\"run-1\"}");

        Assert.Equal(2, calls);
        Assert.Equal(@"C:\Models\Local.rvt", JsonDocument.Parse(result).RootElement.GetProperty("localPath").GetString());
    }

    [Fact]
    public async Task FailedBridgeStateBecomesToolFailure()
    {
        var coordinator = new RevitOperationCoordinator((_, _, _) =>
            Task.FromResult("{\"status\":\"failed\",\"message\":\"open failed\"}"), TimeSpan.FromMilliseconds(1));

        var result = await coordinator.WaitForCompletionAsync(
            "revit_custom_open_model",
            "{\"status\":\"queued\",\"runId\":\"run-2\"}");
        var root = JsonDocument.Parse(result).RootElement;

        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Equal("open failed", root.GetProperty("message").GetString());
    }

    [Fact]
    public async Task UsesConfiguredTimeout()
    {
        var coordinator = new RevitOperationCoordinator((_, _, _) =>
            Task.FromResult("{\"status\":\"running\"}"), TimeSpan.FromMilliseconds(1))
        {
            Timeout = TimeSpan.FromMilliseconds(10)
        };

        var result = await coordinator.WaitForCompletionAsync(
            "revit_custom_open_model",
            "{\"status\":\"queued\",\"runId\":\"run-3\"}");

        var root = JsonDocument.Parse(result).RootElement;
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.Contains("не завершилась", root.GetProperty("message").GetString());
    }
}
