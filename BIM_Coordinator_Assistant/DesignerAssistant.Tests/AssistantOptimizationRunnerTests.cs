using System.Text.Json;
using DesignerAssistant.Models;
using DesignerAssistant.Web.Services;

namespace DesignerAssistant.Tests;

public sealed class AssistantOptimizationRunnerTests
{
    [Fact]
    public void RejectsPlanThatChangesUserPath()
    {
        var plan = Plan("revit_custom_open_model", "C:\\wrong.rvt");

        Assert.NotNull(AssistantOptimizationRunner.ValidatePlan(plan, "revit_custom_open_model", "C:\\correct.rvt"));
    }

    [Fact]
    public void RejectsExtraMutationInPlan()
    {
        var step = Plan("revit_custom_open_model", "C:\\correct.rvt").Steps[0];
        var plan = new TaskPlan("test", [step, step]);

        Assert.NotNull(AssistantOptimizationRunner.ValidatePlan(plan, "revit_custom_open_model", "C:\\correct.rvt"));
    }

    [Fact]
    public void ReadsConfirmedSyncAndCloseFromToolPayload()
    {
        using var document = JsonDocument.Parse("""{"success":true,"data":{"synchronized":true,"closePosted":true}}""");

        Assert.True(AssistantOptimizationRunner.HasBoolean(document.RootElement, "synchronized"));
        Assert.True(AssistantOptimizationRunner.HasBoolean(document.RootElement, "closePosted"));
        Assert.False(AssistantOptimizationRunner.HasBoolean(document.RootElement, "closed"));
    }

    [Fact]
    public void AutoConfirmsOnlyExpectedOpenCall()
    {
        const string path = "C:\\Tests\\model.rvt";
        var valid = Proposal("revit_custom_open_model", 42,
            """{"path":"C:\\Tests\\model.rvt","detach":false,"worksetMode":"all"}""");
        var wrongPath = Proposal("revit_custom_open_model", 42, """{"path":"C:\\other.rvt"}""");
        var detached = Proposal("revit_custom_open_model", 42,
            """{"path":"C:\\Tests\\model.rvt","detach":true}""");

        Assert.True(AssistantOptimizationRunner.IsAutoConfirmableProposal(valid, "revit_custom_open_model", path, 42));
        Assert.False(AssistantOptimizationRunner.IsAutoConfirmableProposal(wrongPath, "revit_custom_open_model", path, 42));
        Assert.False(AssistantOptimizationRunner.IsAutoConfirmableProposal(detached, "revit_custom_open_model", path, 42));
        Assert.False(AssistantOptimizationRunner.IsAutoConfirmableProposal(valid, "revit_custom_open_model", path, 43));
    }

    [Fact]
    public void AutoConfirmsOnlyExpectedSyncCloseCall()
    {
        var valid = Proposal("revit_custom_sync_relinquish_and_close", 42, """{"comment":"test"}""");
        var unexpected = Proposal("revit_custom_sync_relinquish_and_close", 42,
            """{"comment":"test","path":"C:\\other.rvt"}""");

        Assert.True(AssistantOptimizationRunner.IsAutoConfirmableProposal(valid,
            "revit_custom_sync_relinquish_and_close", "C:\\Tests\\model.rvt", 42));
        Assert.False(AssistantOptimizationRunner.IsAutoConfirmableProposal(unexpected,
            "revit_custom_sync_relinquish_and_close", "C:\\Tests\\model.rvt", 42));
        Assert.False(AssistantOptimizationRunner.IsAutoConfirmableProposal(valid,
            "revit_custom_open_model", "C:\\Tests\\model.rvt", 42));
    }

    private static string Proposal(string tool, int processId, string arguments) =>
        $"Инструмент: {tool}\nЦель Revit: REVIT-2022 [Test], ID {processId}\nАргументы: {arguments}";

    private static TaskPlan Plan(string tool, string path) => new("test",
        [new TaskPlanStep("open", tool,
            new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement(path) },
            new Dictionary<string, string>())]);
}
