using DesignerAssistant.Models;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Storage;

public sealed class SqliteChatHistoryStore : IChatHistoryStore
{
    private readonly string _connectionString;

    public SqliteChatHistoryStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Путь к базе данных не задан.", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS chat_messages (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                role TEXT NOT NULL,
                content TEXT NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS compression_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                is_enabled INTEGER NOT NULL,
                summary TEXT NOT NULL,
                summarized_message_count INTEGER NOT NULL
            );

            INSERT OR IGNORE INTO compression_state
                (id, is_enabled, summary, summarized_message_count)
            VALUES (1, 1, '', 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT role, content FROM chat_messages ORDER BY id;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new ChatMessage(reader.GetString(0), reader.GetString(1)));
        }

        return messages;
    }

    public async Task AppendAsync(
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();

        foreach (var message in messages)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO chat_messages (role, content) VALUES ($role, $content);";
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$content", message.Content);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM chat_messages;
            UPDATE compression_state
            SET summary = '', summarized_message_count = 0
            WHERE id = 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CompressionState> LoadCompressionStateAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT is_enabled, summary, summarized_message_count
            FROM compression_state
            WHERE id = 1;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new CompressionState(true, string.Empty, 0);
        }

        return new CompressionState(
            reader.GetInt32(0) != 0,
            reader.GetString(1),
            reader.GetInt32(2));
    }

    public async Task SaveCompressionStateAsync(
        CompressionState state,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE compression_state
            SET is_enabled = $isEnabled,
                summary = $summary,
                summarized_message_count = $messageCount
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$isEnabled", state.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$summary", state.Summary);
        command.Parameters.AddWithValue("$messageCount", state.SummarizedMessageCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
