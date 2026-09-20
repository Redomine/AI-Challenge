using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Storage;

public sealed class SqliteInvariantStore : IInvariantStore
{
    private readonly string _connectionString;

    public SqliteInvariantStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS assistant_invariants (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                text TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM assistant_invariants WHERE id=1;";
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? "";
    }

    public async Task SaveAsync(string text, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO assistant_invariants(id, text, updated_at) VALUES(1, $text, $now)
            ON CONFLICT(id) DO UPDATE SET text=excluded.text, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$text", text ?? "");
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
