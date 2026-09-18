using DesignerAssistant.Models;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Storage;

public sealed class SqliteMemoryStore : IMemoryStore
{
    private readonly string _connectionString;

    public SqliteMemoryStore(string databasePath)
    {
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                layer TEXT NOT NULL,
                key TEXT NOT NULL,
                value TEXT NOT NULL,
                source TEXT NOT NULL,
                status TEXT NOT NULL,
                task_id TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                UNIQUE(layer, key, task_id)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MemoryEntry>> LoadAsync(MemoryLayer layer, string taskId = "current", CancellationToken cancellationToken = default)
    {
        var entries = new List<MemoryEntry>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, key, value, source, status, task_id, created_at, updated_at FROM memory_entries WHERE layer=$layer AND task_id=$task ORDER BY key;";
        command.Parameters.AddWithValue("$layer", layer.ToString());
        command.Parameters.AddWithValue("$task", Scope(layer, taskId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new MemoryEntry(reader.GetInt64(0), layer, reader.GetString(1), reader.GetString(2), Enum.Parse<MemorySource>(reader.GetString(3)), Enum.Parse<EvidenceStatus>(reader.GetString(4)), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)), DateTimeOffset.Parse(reader.GetString(7))));
        }
        return entries;
    }

    public async Task UpsertAsync(MemoryLayer layer, string key, string value, MemorySource source, EvidenceStatus status, string taskId = "current", CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_entries(layer,key,value,source,status,task_id,created_at,updated_at)
            VALUES($layer,$key,$value,$source,$status,$task,$now,$now)
            ON CONFLICT(layer,key,task_id) DO UPDATE SET value=excluded.value, source=excluded.source, status=excluded.status, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$layer", layer.ToString());
        command.Parameters.AddWithValue("$key", key.Trim());
        command.Parameters.AddWithValue("$value", value.Trim());
        command.Parameters.AddWithValue("$source", source.ToString());
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$task", Scope(layer, taskId));
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ClearAsync(MemoryLayer layer, string taskId = "current", CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_entries WHERE layer=$layer AND task_id=$task;";
        command.Parameters.AddWithValue("$layer", layer.ToString());
        command.Parameters.AddWithValue("$task", Scope(layer, taskId));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Scope(MemoryLayer layer, string taskId) => layer == MemoryLayer.LongTerm ? "global" : taskId;
    private async Task<SqliteConnection> OpenAsync(CancellationToken token) { var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(token); return connection; }
}
