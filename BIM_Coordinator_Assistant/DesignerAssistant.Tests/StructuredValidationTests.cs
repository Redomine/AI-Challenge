using System.Text.Json;
using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Storage;

namespace DesignerAssistant.Tests;

public sealed class StructuredValidationTests
{
    [Fact]
    public async Task ValidationUsesStructuredStatusAndAddsReliableMarker()
    {
        var llm = new StructuredLlm("""{"status":"PASS","report":"Everything matches the plan."}""");
        var agent = new DesignAssistantAgent(
            llm,
            new InMemoryChatHistoryStore(),
            new InMemoryMemoryStore(),
            "instructions",
            new AppOptions("key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        await agent.InitializeAsync();

        var response = await agent.ValidateTaskAsync(new TaskContext(
            "query", TaskState.Validation, "plan", "execution", PlanApproved: true));

        Assert.Equal("[PASS] Everything matches the plan.", response.ModelResponse.Content);
        Assert.True(llm.StructuredWasUsed);
        Assert.Equal(TaskState.Validation, agent.GetHistory().Last().Stage);
    }

    [Fact]
    public async Task ValidationRetriesAfterEmptyModelResponse()
    {
        var llm = new RetryingStructuredLlm();
        var agent = new DesignAssistantAgent(
            llm,
            new InMemoryChatHistoryStore(),
            new InMemoryMemoryStore(),
            "instructions",
            new AppOptions("key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10));
        await agent.InitializeAsync();

        var response = await agent.ValidateTaskAsync(new TaskContext(
            "query", TaskState.Validation, "plan", "execution", PlanApproved: true));

        Assert.Equal("[PASS] Проверено со второй попытки.", response.ModelResponse.Content);
        Assert.Equal(1, llm.StructuredCallCount);
        Assert.Equal(1, llm.RegularCallCount);
    }

    private sealed class StructuredLlm(string response) : IStructuredLlmClient
    {
        public bool StructuredWasUsed { get; private set; }
        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TokenCountResult(0, true));
        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Validation должна использовать structured output.");
        public Task<LlmResponse> GenerateStructuredAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, JsonElement schema, CancellationToken cancellationToken = default)
        {
            StructuredWasUsed = true;
            return Task.FromResult(new LlmResponse(response, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)));
        }
    }

    private sealed class RetryingStructuredLlm : IStructuredLlmClient
    {
        public int StructuredCallCount { get; private set; }
        public int RegularCallCount { get; private set; }

        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TokenCountResult(0, true));

        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            RegularCallCount++;
            return Task.FromResult(new LlmResponse(
                """{"status":"PASS","report":"Проверено со второй попытки."}""",
                "stop",
                new TokenUsage(0, 0, 0, 0, 0, 0, 0, true)));
        }

        public Task<LlmResponse> GenerateStructuredAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, JsonElement schema, CancellationToken cancellationToken = default)
        {
            StructuredCallCount++;
            throw new InvalidOperationException("В ответе GigaChat не найден текст модели.");
        }
    }
}
