using System.Text.Json;
using DesignerAssistant.Models;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Storage;

public sealed class SqliteUserProfileStore : IUserProfileStore
{
    private readonly string _connectionString;

    public SqliteUserProfileStore(string databasePath)
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
            CREATE TABLE IF NOT EXISTS user_profiles (
                name TEXT PRIMARY KEY COLLATE NOCASE,
                json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS profile_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                active_name TEXT NULL
            );

            -- Migrate the former singleton profile without creating one on a fresh database.
            INSERT OR IGNORE INTO user_profiles(name, json, updated_at)
            SELECT 'default', json, updated_at FROM user_profile WHERE id=1;
            INSERT OR IGNORE INTO profile_settings(id, active_name)
            SELECT 1, 'default' WHERE EXISTS(SELECT 1 FROM user_profile WHERE id=1);
            """;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && exception.Message.Contains("user_profile", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = "INSERT OR IGNORE INTO profile_settings(id, active_name) VALUES(1, NULL);";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<UserProfile?> LoadAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.name, p.json
            FROM profile_settings s
            JOIN user_profiles p ON p.name=s.active_name
            WHERE s.id=1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? Deserialize(reader.GetString(0), reader.GetString(1))
            : null;
    }

    public async Task<IReadOnlyList<UserProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var profiles = new List<UserProfile>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, json FROM user_profiles ORDER BY name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            profiles.Add(Deserialize(reader.GetString(0), reader.GetString(1)));
        }
        return profiles;
    }

    public async Task SaveAsync(UserProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO user_profiles(name, json, updated_at) VALUES($name, $json, $now)
            ON CONFLICT(name) DO UPDATE SET json=excluded.json, updated_at=excluded.updated_at;
            INSERT INTO profile_settings(id, active_name) VALUES(1, $name)
            ON CONFLICT(id) DO UPDATE SET active_name=excluded.active_name;
            """;
        command.Parameters.AddWithValue("$name", profile.Name.Trim());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(profile));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> SelectAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO profile_settings(id, active_name)
            SELECT 1, name FROM user_profiles WHERE name=$name
            ON CONFLICT(id) DO UPDATE SET active_name=excluded.active_name;
            """;
        command.Parameters.AddWithValue("$name", name.Trim());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE profile_settings SET active_name=NULL WHERE id=1 AND active_name=$name COLLATE NOCASE;
            DELETE FROM user_profiles WHERE name=$name;
            """;
        command.Parameters.AddWithValue("$name", name.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static UserProfile Deserialize(string name, string json)
    {
        var profile = JsonSerializer.Deserialize<UserProfile>(json)
            ?? throw new InvalidDataException($"Не удалось прочитать профиль '{name}'.");
        return profile with { Name = name };
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
