using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Storage;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Tests;

public sealed class InvariantTests
{
    [Fact]
    public async Task SqliteStorePersistsGlobalInvariantText()
    {
        var path = Path.Combine(Path.GetTempPath(), $"invariants-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteInvariantStore(path);
            await store.InitializeAsync();
            Assert.Equal("", await store.LoadAsync());

            await store.SaveAsync("Никогда не изменяй модель без подтверждения.");

            var reopened = new SqliteInvariantStore(path);
            await reopened.InitializeAsync();
            Assert.Equal("Никогда не изменяй модель без подтверждения.", await reopened.LoadAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AgentPlacesInvariantsAfterProfileWithHighestPriorityDirective()
    {
        var llm = new CapturingLlm();
        var invariants = new InMemoryInvariantStore();
        var agent = new DesignAssistantAgent(
            llm,
            new InMemoryChatHistoryStore(),
            new InMemoryMemoryStore(),
            "Базовый prompt",
            new AppOptions("key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 10),
            profileStore: new InMemoryUserProfileStore(),
            invariantStore: invariants);
        await agent.InitializeAsync();
        await agent.SaveProfileAsync(new UserProfile("Рабочий", "Отвечай кратко", "", ""));
        await agent.SaveInvariantsAsync("Не утверждай, что инструмент вызван, без результата.");

        await agent.AskAsync("Проверка");

        Assert.Contains("priority=highest", llm.Instructions);
        Assert.Contains("Не утверждай, что инструмент вызван, без результата.", llm.Instructions);
        Assert.True(
            llm.Instructions.IndexOf("[ACTIVE_USER_PROFILE", StringComparison.Ordinal) <
            llm.Instructions.IndexOf("[INVARIANTS", StringComparison.Ordinal));
    }

    private sealed class CapturingLlm : ILlmClient
    {
        public string Instructions { get; private set; } = "";
        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TokenCountResult(1, true));
        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            if (Instructions.Length == 0) Instructions = instructions;
            return Task.FromResult(new LlmResponse("ok", "stop", new TokenUsage(1, 1, 2, 2, 0, 0, 2, true)));
        }
    }
}
