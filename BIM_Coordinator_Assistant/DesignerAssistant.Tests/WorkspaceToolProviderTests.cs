using System.Text.Json;
using DesignerAssistant.Models;
using DesignerAssistant.Tools;

namespace DesignerAssistant.Tests;

public sealed class WorkspaceToolProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"designer-assistant-tools-{Guid.NewGuid():N}");

    public WorkspaceToolProviderTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sample.cs"), "class Sample { }\n// Искомая строка");
        File.WriteAllText(Path.Combine(_root, ".env"), "TOKEN=secret");
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "bin", "generated.cs"), "generated");
    }

    [Fact]
    public async Task ReadsAndSearchesUtf8TextInsideWorkspace()
    {
        var provider = new WorkspaceToolProvider(_root);

        using var read = JsonDocument.Parse(await provider.InvokeAsync(
            "workspace_read_text_file", JsonSerializer.SerializeToElement(new { path = "sample.cs" })));
        using var search = JsonDocument.Parse(await provider.InvokeAsync(
            "workspace_search_text", JsonSerializer.SerializeToElement(new { query = "искомая", pattern = "*.cs" })));

        Assert.True(read.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("Искомая строка", read.RootElement.GetProperty("content").GetString());
        Assert.Single(search.RootElement.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task CatalogueExposesOnlyReviewedWorkspaceTools()
    {
        var provider = new WorkspaceToolProvider(_root);
        var names = (await provider.GetToolsAsync()).Select(tool => tool.Name).ToArray();

        Assert.Equal(9, names.Length);
        Assert.Contains("workspace_dotnet_build", names);
        Assert.Contains("workspace_dotnet_test", names);
        Assert.Contains("workspace_run_command_recipe", names);
        Assert.DoesNotContain("run_powershell", names);
    }

    [Fact]
    public async Task RejectsTraversalAndSecretFiles()
    {
        var provider = new WorkspaceToolProvider(_root);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.InvokeAsync(
            "workspace_read_text_file", JsonSerializer.SerializeToElement(new { path = "../outside.txt" })));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => provider.InvokeAsync(
            "workspace_read_text_file", JsonSerializer.SerializeToElement(new { path = ".env" })));
    }

    [Fact]
    public async Task FileListingSkipsGeneratedDirectories()
    {
        var provider = new WorkspaceToolProvider(_root);
        using var result = JsonDocument.Parse(await provider.InvokeAsync(
            "workspace_list_files", JsonSerializer.SerializeToElement(new { pattern = "*.cs" })));
        var files = result.RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString()).ToArray();

        Assert.Contains("sample.cs", files);
        Assert.DoesNotContain("bin/generated.cs", files);
    }

    [Fact]
    public async Task CompositeProviderRoutesInvocationToToolOwner()
    {
        var first = new FakeProvider("first");
        var second = new FakeProvider("second");
        var composite = new CompositeToolProvider(first, second);
        await composite.GetToolsAsync();

        var result = await composite.InvokeAsync("second", JsonSerializer.SerializeToElement(new { }));

        Assert.Equal("second-result", result);
        Assert.Equal(0, first.InvocationCount);
        Assert.Equal(1, second.InvocationCount);
    }

    [Fact]
    public async Task DotnetBuildReturnsActualSuccessfulExitCode()
    {
        File.WriteAllText(Path.Combine(_root, "Smoke.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(_root, "Class1.cs"), "public sealed class Class1 { }");
        var provider = new WorkspaceToolProvider(_root);

        using var result = JsonDocument.Parse(await provider.InvokeAsync(
            "workspace_dotnet_build",
            JsonSerializer.SerializeToElement(new { project = "Smoke.csproj", noRestore = false, timeoutSeconds = 60 })));

        Assert.True(result.RootElement.GetProperty("ok").GetBoolean(), result.RootElement.GetProperty("stderr").GetString());
        Assert.Equal(0, result.RootElement.GetProperty("ExitCode").GetInt32());
    }

    [Fact]
    public async Task CommandRecipesRejectUnknownCommands()
    {
        var provider = new WorkspaceToolProvider(_root);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.InvokeAsync(
            "workspace_run_command_recipe",
            JsonSerializer.SerializeToElement(new { recipe = "Remove-Item" })));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeProvider(string name) : IToolProvider
    {
        public int InvocationCount { get; private set; }
        public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolDefinition>>([new(name, name, JsonSerializer.SerializeToElement(new { type = "object" }))]);
        public Task<string> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return Task.FromResult($"{toolName}-result");
        }
    }
}
