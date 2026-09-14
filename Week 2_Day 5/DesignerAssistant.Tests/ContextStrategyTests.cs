using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Storage;

namespace DesignerAssistant.Tests;

public sealed class ContextStrategyTests
{
    [Fact]
    public async Task SlidingWindowSendsOnlyRecentMessages()
    {
        var client = new FakeLlmClient();
        var agent = CreateAgent(client, recentMessages: 2);
        await agent.InitializeAsync();

        await agent.AskAsync("Первый вопрос");
        await agent.AskAsync("Второй вопрос");
        await agent.AskAsync("Третий вопрос");

        var sent = client.LastMainMessages;
        Assert.Equal(3, sent.Count);
        Assert.DoesNotContain(sent, message => message.Content == "Первый вопрос");
        Assert.Equal("Третий вопрос", sent[^1].Content);
    }

    [Fact]
    public async Task StickyFactsUpdatesAndInjectsFactsIntoInstructions()
    {
        var client = new FakeLlmClient();
        var agent = CreateAgent(client);
        await agent.InitializeAsync();
        await agent.SetStrategyAsync(ContextStrategy.StickyFacts);

        var response = await agent.AskAsync("Расход воздуха 4800 м³/ч");

        Assert.Equal(2, response.FactsCount);
        Assert.Contains("расход воздуха", client.LastMainInstructions);
        Assert.Contains("4800 м³/ч", client.LastMainInstructions);
        Assert.Contains(@"C:\x\duct", client.LastMainInstructions);
        Assert.Equal(1, client.FactsCallCount);
    }

    [Fact]
    public async Task StickyFactsKeepsDialogRunningWhenJsonCannotBeRepaired()
    {
        var client = new FakeLlmClient { FactsContent = "не JSON" };
        var agent = CreateAgent(client);
        await agent.InitializeAsync();
        await agent.SetStrategyAsync(ContextStrategy.StickyFacts);

        var response = await agent.AskAsync("Запомни ограничение");

        Assert.Equal(0, response.FactsCount);
        Assert.Equal(2, client.FactsCallCount);
        Assert.Equal("Ответ", response.ModelResponse.Content);
    }

    [Fact]
    public async Task BranchesContinueIndependentlyFromCheckpoint()
    {
        var client = new FakeLlmClient();
        var agent = CreateAgent(client);
        await agent.InitializeAsync();
        await agent.AskAsync("Общая исходная точка");
        await agent.SetStrategyAsync(ContextStrategy.Branching);
        await agent.CreateCheckpointAsync();

        await agent.CreateBranchAsync("a");
        await agent.AskAsync("Решение ветки A");
        await agent.CreateBranchAsync("b");
        await agent.AskAsync("Решение ветки B");
        await agent.SwitchBranchAsync("a");
        await agent.AskAsync("Продолжение A");

        Assert.Contains(
            client.LastMainMessages,
            message => message.Content == "Решение ветки A");
        Assert.DoesNotContain(
            client.LastMainMessages,
            message => message.Content == "Решение ветки B");
        Assert.Equal("a", agent.GetContextStatus().ActiveBranch);
    }

    private static DesignAssistantAgent CreateAgent(
        FakeLlmClient client,
        int recentMessages = 10)
    {
        var options = new AppOptions(
            "key",
            "scope",
            "model",
            "tokenizer",
            100,
            "test.db",
            10_000,
            0,
            recentMessages);
        return new DesignAssistantAgent(
            client,
            new InMemoryChatHistoryStore(),
            DesignerAssistantPrompt.Text,
            options);
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public int FactsCallCount { get; private set; }
        public string FactsContent { get; init; } =
            """{\"расход воздуха\":\"4800 м³/ч\",\"путь\":\"C:\\x\\duct\"}""";
        public string LastMainInstructions { get; private set; } = string.Empty;
        public IReadOnlyList<ChatMessage> LastMainMessages { get; private set; } = [];

        public Task<TokenCountResult> CountTextTokensAsync(
            IReadOnlyCollection<string> texts,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TokenCountResult(texts.Sum(text => text.Length), true));

        public Task<LlmResponse> GenerateAsync(
            string instructions,
            IReadOnlyCollection<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var isFactsCall = instructions == FactsExtractionPrompt.Text;
            if (isFactsCall)
            {
                FactsCallCount++;
            }
            else
            {
                LastMainInstructions = instructions;
                LastMainMessages = messages.ToArray();
            }

            var content = isFactsCall
                ? FactsContent
                : "Ответ";
            return Task.FromResult(new LlmResponse(
                content,
                "stop",
                new TokenUsage(1, 2, 3, 3, 0, 1, 4, true)));
        }
    }
}
