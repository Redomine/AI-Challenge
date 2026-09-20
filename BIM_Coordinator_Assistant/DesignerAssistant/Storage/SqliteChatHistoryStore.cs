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
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = "ALTER TABLE chat_messages ADD COLUMN stage TEXT NULL;";
        try { await command.ExecuteNonQueryAsync(cancellationToken); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { }
    }

    public async Task<IReadOnlyList<ChatMessage>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var messages = new List<ChatMessage>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT role, content, stage FROM chat_messages ORDER BY id;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            TaskState? stage = reader.IsDBNull(2) ? null : Enum.Parse<TaskState>(reader.GetString(2));
            messages.Add(new ChatMessage(reader.GetString(0), reader.GetString(1), stage));
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
        await InsertMessagesAsync(connection, transaction, messages, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM chat_messages;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertMessagesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO chat_messages (role, content, stage) VALUES ($role, $content, $stage);";
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$content", message.Content);
            command.Parameters.AddWithValue("$stage", message.Stage?.ToString() ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
