using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Storage;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Tests;

public sealed class UserProfileTests
{
    [Fact]
    public async Task AgentDoesNotCreateDefaultProfile()
    {
        var profileStore = new InMemoryUserProfileStore();
        var llm = new CapturingLlm();
        var agent = CreateAgent(llm, profileStore);
        await agent.InitializeAsync();

        Assert.Null(await agent.GetProfileAsync());
        await agent.AskAsync("Проверка");

        Assert.Contains("Not configured", llm.Instructions);
    }

    [Fact]
    public async Task DifferentProfilesProduceDifferentAutomaticInstructions()
    {
        var conciseLlm = new CapturingLlm();
        var conciseAgent = CreateAgent(conciseLlm, new InMemoryUserProfileStore());
        await conciseAgent.InitializeAsync();
        await conciseAgent.SaveProfileAsync(Profile("Отвечай кратко и формально.", "Revit 2022"));
        await conciseAgent.AskAsync("Опиши проверку");

        var detailedLlm = new CapturingLlm();
        var detailedAgent = CreateAgent(detailedLlm, new InMemoryUserProfileStore());
        await detailedAgent.InitializeAsync();
        await detailedAgent.SaveProfileAsync(Profile("Отвечай подробно и разговорно.", "Revit 2024 + pyRevit"));
        await detailedAgent.AskAsync("Опиши проверку");

        Assert.Contains("Отвечай кратко и формально.", conciseLlm.Instructions);
        Assert.Contains("Revit 2022", conciseLlm.Instructions);
        Assert.Contains("Отвечай подробно и разговорно.", detailedLlm.Instructions);
        Assert.Contains("Revit 2024 + pyRevit", detailedLlm.Instructions);
        Assert.NotEqual(conciseLlm.Instructions, detailedLlm.Instructions);
    }

    [Fact]
    public async Task ProfileLanguageIsDeclaredHigherPriorityThanBaseDefault()
    {
        var llm = new CapturingLlm();
        var agent = CreateAgent(llm, new InMemoryUserProfileStore());
        await agent.InitializeAsync();
        await agent.SaveProfileAsync(Profile(
            "English",
            "Отвечай подробно и в разговорном стиле. Используй английский язык.",
            "Revit 2022"));

        await agent.AskAsync("Что за элемент?");

        Assert.Contains("profile instruction takes priority", llm.Instructions);
        Assert.Contains("Используй английский язык", llm.Instructions);
        Assert.Contains("инструкции активного USER_PROFILE имеют приоритет", llm.Instructions);
        Assert.True(
            llm.Instructions.IndexOf("[WORKING_MEMORY", StringComparison.Ordinal) <
            llm.Instructions.IndexOf("[ACTIVE_USER_PROFILE", StringComparison.Ordinal));
        Assert.True(
            llm.Instructions.IndexOf("[ACTIVE_USER_PROFILE", StringComparison.Ordinal) <
            llm.Instructions.IndexOf("[INVARIANTS", StringComparison.Ordinal));
        Assert.EndsWith("[/INVARIANTS]", llm.Instructions.Trim());
    }

    [Fact]
    public async Task EveryResponseWithProfileRunsThroughGenericProfileEnforcer()
    {
        var llm = new SequencedLlm("Черновик", "Final response in English");
        var agent = CreateAgent(llm, new InMemoryUserProfileStore());
        await agent.InitializeAsync();
        await agent.SaveProfileAsync(Profile(
            "English",
            "Use English and answer as a concise table.",
            "Do not suggest programming examples."));

        var response = await agent.AskAsync("Расскажи о результате");

        Assert.Equal("Final response in English", response.ModelResponse.Content);
        Assert.Equal(2, llm.Calls.Count);
        Assert.Contains("Apply every instruction", llm.Calls[1].Instructions);
        Assert.Contains("Use English and answer as a concise table.", llm.Calls[1].Instructions);
        Assert.Contains("Черновик", llm.Calls[1].Message);
        Assert.Contains(response.ToolTraces!, trace => trace.StartsWith("Profile enforcement:"));
    }

    [Fact]
    public async Task SqliteProfileCanBeSavedLoadedAndDeleted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteUserProfileStore(path);
            await store.InitializeAsync();
            var profile = Profile("Основной", "Пиши по-русски.", "Revit 2022/2024");

            await store.SaveAsync(profile);
            Assert.Equal(profile, await store.LoadAsync());

            await store.DeleteAsync(profile.Name);
            Assert.Null(await store.LoadAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SqliteStoreRemembersLastSelectedProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"profile-selection-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteUserProfileStore(path);
            await store.InitializeAsync();
            await store.SaveAsync(Profile("Краткий", "Отвечай кратко.", "Revit 2022"));
            await store.SaveAsync(Profile("Подробный", "Отвечай подробно.", "Revit 2024"));

            Assert.True(await store.SelectAsync("Краткий"));

            var reopenedStore = new SqliteUserProfileStore(path);
            await reopenedStore.InitializeAsync();
            Assert.Equal("Краткий", (await reopenedStore.LoadAsync())?.Name);
            Assert.Equal(2, (await reopenedStore.ListAsync()).Count);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    private static UserProfile Profile(string style, string tooling) => Profile("Тестовый", style, tooling);

    private static UserProfile Profile(string name, string style, string tooling) => new(
        name,
        style,
        $"{tooling}. Только бесплатные инструменты.",
        "BIM-координатор тестирует модель и плагины.");

    private static DesignAssistantAgent CreateAgent(ILlmClient llm, IUserProfileStore profileStore) => new(
        llm,
        new InMemoryChatHistoryStore(),
        new InMemoryMemoryStore(),
        DesignerAssistantPrompt.Text,
        new AppOptions("key", "scope", "model", "tokenizer", 100, "test.db", 10000, 0, 20),
        profileStore: profileStore);

    private sealed class CapturingLlm : ILlmClient
    {
        public string Instructions { get; private set; } = "";

        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            if (Instructions.Length == 0) Instructions = instructions;
            return Task.FromResult(new LlmResponse("ok", "stop", new TokenUsage(1, 1, 2, 2, 0, 0, 2, true)));
        }

        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TokenCountResult(1, true));
    }

    private sealed class SequencedLlm(params string[] responses) : ILlmClient
    {
        private int _index;
        public List<(string Instructions, string Message)> Calls { get; } = [];

        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            Calls.Add((instructions, messages.Last().Content));
            var content = responses[Math.Min(_index++, responses.Length - 1)];
            return Task.FromResult(new LlmResponse(content, "stop", new TokenUsage(1, 1, 2, 2, 0, 1, 3, true)));
        }

        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default) =>
            Task.FromResult(new TokenCountResult(1, true));
    }
}
