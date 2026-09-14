using System.Text.Json;
using System.Text.Json.Serialization;
using DesignerAssistant.Models;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Storage;

public sealed class SqliteChatHistoryStore : IChatHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

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

            CREATE TABLE IF NOT EXISTS context_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                state_json TEXT NOT NULL
            );
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
        await InsertMessagesAsync(connection, transaction, messages, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReplaceAsync(
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM chat_messages;";
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertMessagesAsync(connection, transaction, messages, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<ContextState> LoadContextStateAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_json FROM context_state WHERE id = 1;";
        var json = await command.ExecuteScalarAsync(cancellationToken) as string;
        return string.IsNullOrWhiteSpace(json)
            ? ContextState.CreateDefault()
            : JsonSerializer.Deserialize<ContextState>(json, JsonOptions)
              ?? ContextState.CreateDefault();
    }

    public async Task SaveContextStateAsync(
        ContextState state,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO context_state (id, state_json)
            VALUES (1, $stateJson)
            ON CONFLICT(id) DO UPDATE SET state_json = excluded.state_json;
            """;
        command.Parameters.AddWithValue("$stateJson", json);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM chat_messages; DELETE FROM context_state;";
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
                "INSERT INTO chat_messages (role, content) VALUES ($role, $content);";
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$content", message.Content);
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
