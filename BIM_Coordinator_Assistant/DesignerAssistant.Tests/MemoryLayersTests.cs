using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Storage;

namespace DesignerAssistant.Tests;

public sealed class MemoryLayersTests
{
    [Fact]
    public async Task NewSessionKeepsExplicitMemoryAndTaskCompletionClearsOnlyWorking()
    {
        var memory = new InMemoryMemoryStore();
        var agent = CreateAgent(memory);
        await agent.InitializeAsync();
        await agent.RememberAsync(MemoryLayer.Working, "model", "test.rvt");
        await agent.RememberAsync(MemoryLayer.LongTerm, "role", "BIM coordinator");
        await agent.AskAsync("Проверка");

        await agent.ClearHistoryAsync();
        var afterSession = await agent.GetMemoryAsync();
        Assert.Empty(afterSession.ShortTerm);
        Assert.Single(afterSession.Working);
        Assert.Single(afterSession.LongTerm);

        await agent.CompleteTaskAsync();
        var afterTask = await agent.GetMemoryAsync();
        Assert.Empty(afterTask.Working);
        Assert.Single(afterTask.LongTerm);
    }

    [Fact]
    public async Task PromptContainsSeparateMemoryBlocksWithEvidence()
    {
        var memory = new InMemoryMemoryStore();
        var llm = new CapturingLlm();
        var agent = CreateAgent(memory, llm);
        await agent.InitializeAsync();
        await agent.RememberAsync(MemoryLayer.Working, "version", "Revit 2022");
        await agent.RememberAsync(MemoryLayer.LongTerm, "purpose", "plugin support");
        await agent.AskAsync("Что проверяем?");

        Assert.Contains("[LONG_TERM_MEMORY]", llm.Instructions);
        Assert.Contains("purpose: plugin support", llm.Instructions);
        Assert.Contains("[WORKING_MEMORY task=current]", llm.Instructions);
        Assert.Contains("version: Revit 2022", llm.Instructions);
        Assert.Contains("status=Confirmed", llm.Instructions);
    }

    private static DesignAssistantAgent CreateAgent(IMemoryStore memory, ILlmClient? llm = null) => new(
        llm ?? new CapturingLlm(), new InMemoryChatHistoryStore(), memory,
        DesignerAssistantPrompt.Text,
        new AppOptions("key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 20));

    private sealed class CapturingLlm : ILlmClient
    {
        public string Instructions { get; private set; } = "";
        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Instructions = instructions;
            return Task.FromResult(new LlmResponse("ok", "stop", new TokenUsage(1, 1, 2, 2, 0, 0, 2, true)));
        }
        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default) => Task.FromResult(new TokenCountResult(1, true));
    }
}
