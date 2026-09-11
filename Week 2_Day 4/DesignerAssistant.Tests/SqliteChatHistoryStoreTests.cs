using DesignerAssistant.Models;
using DesignerAssistant.Storage;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Tests;

public sealed class SqliteChatHistoryStoreTests
{
    [Fact]
    public async Task HistorySurvivesStoreRecreationAndCanBeCleared()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"designer-assistant-{Guid.NewGuid():N}.db");

        try
        {
            var firstStore = new SqliteChatHistoryStore(databasePath);
            await firstStore.InitializeAsync();
            await firstStore.AppendAsync(
            [
                new ChatMessage("user", "Шаг креплений 2 м"),
                new ChatMessage("assistant", "Принято")
            ]);

            var reopenedStore = new SqliteChatHistoryStore(databasePath);
            await reopenedStore.InitializeAsync();
            var restored = await reopenedStore.LoadAsync();

            Assert.Equal(2, restored.Count);
            Assert.Equal("user", restored[0].Role);
            Assert.Equal("Шаг креплений 2 м", restored[0].Content);
            Assert.Equal("assistant", restored[1].Role);
            Assert.Equal("Принято", restored[1].Content);

            await reopenedStore.ClearAsync();
            Assert.Empty(await reopenedStore.LoadAsync());
            var compressionState = await reopenedStore.LoadCompressionStateAsync();
            Assert.Empty(compressionState.Summary);
            Assert.Equal(0, compressionState.SummarizedMessageCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }
}
