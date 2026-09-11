using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Storage;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Tests;

public sealed class HistoryCompressionTests
{
    [Fact]
    public async Task AgentSummarizesOldBatchAndKeepsRecentMessages()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"designer-assistant-compression-{Guid.NewGuid():N}.db");

        try
        {
            var options = new AppOptions(
                "test-key",
                "test-scope",
                "test-model",
                "test-tokenizer",
                100,
                databasePath,
                10_000,
                0,
                10,
                10);
            var store = new SqliteChatHistoryStore(databasePath);
            var client = new FakeLlmClient();
            var agent = new DesignAssistantAgent(
                client,
                store,
                DesignerAssistantPrompt.Text,
                options);
            await agent.InitializeAsync();

            for (var index = 1; index <= 10; index++)
            {
                await agent.AskAsync($"Вопрос {index}");
            }

            var status = agent.GetCompressionStatus();
            Assert.True(status.IsEnabled);
            Assert.Equal(10, status.SummarizedMessageCount);
            Assert.Equal(10, status.RecentMessageCount);
            Assert.Equal(1, client.SummaryCallCount);

            await agent.SetCompressionEnabledAsync(false);
            var reopenedStore = new SqliteChatHistoryStore(databasePath);
            await reopenedStore.InitializeAsync();
            var savedState = await reopenedStore.LoadCompressionStateAsync();
            Assert.False(savedState.IsEnabled);
            Assert.Equal(10, savedState.SummarizedMessageCount);
            Assert.False(string.IsNullOrWhiteSpace(savedState.Summary));
            Assert.Equal(20, (await reopenedStore.LoadAsync()).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        public int SummaryCallCount { get; private set; }

        public Task<TokenCountResult> CountTextTokensAsync(
            IReadOnlyCollection<string> texts,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TokenCountResult(texts.Sum(text => text.Length), true));

        public Task<LlmResponse> GenerateAsync(
            string instructions,
            IReadOnlyCollection<ChatMessage> messages,
            CancellationToken cancellationToken = default)
        {
            var isSummary = instructions == HistorySummaryPrompt.Text;
            if (isSummary)
            {
                SummaryCallCount++;
            }

            var content = isSummary ? "Сжатая ранняя история" : "Ответ";
            return Task.FromResult(new LlmResponse(
                content,
                "stop",
                new TokenUsage(1, 2, 3, 3, 0, 1, 4, true)));
        }
    }
}
