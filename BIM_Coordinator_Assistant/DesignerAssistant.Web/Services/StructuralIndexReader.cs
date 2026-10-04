using System.Buffers.Binary;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Чтение индекса docindexing (стратегия structural). Не модифицирует
/// данные. Схема фиксируется контрактом пакета:
/// <c>chunks(chunk_id, source, source_title, section, text, pdf_page, embedding, embed_dim)</c>.
/// Косинус считается в памяти Python-совместимым способом: little-endian
/// float32, норма — обычная L2, скалярное произведение нормируется.
/// </summary>
public sealed class StructuralIndexReader : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly int _embedDim;
    private readonly string _embedModel;

    public StructuralIndexReader(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("Путь к индексу не задан.", nameof(dbPath));
        }
        if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException(
                $"Индекс не найден: {dbPath}", dbPath);
        }
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        _connection = new SqliteConnection(connectionString);
        _connection.Open();

        _embedDim = ReadMetaInt("embed_dim")
            ?? throw new InvalidOperationException(
                "Индекс не содержит embed_dim в метаданных.");
        _embedModel = ReadMetaString("embed_model")
            ?? throw new InvalidOperationException(
                "Индекс не содержит embed_model в метаданных.");
    }

    public int EmbedDim => _embedDim;
    public string EmbedModel => _embedModel;

    public IReadOnlyList<RagSource> Search(float[] query, int topK, string? exactTerm = null)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (query.Length == 0)
        {
            throw new ArgumentException("Вектор запроса пуст.", nameof(query));
        }
        if (query.Length != _embedDim)
        {
            throw new ArgumentException(
                $"Размерность запроса {query.Length} не совпадает с индексом {_embedDim}.",
                nameof(query));
        }
        if (topK <= 0) return Array.Empty<RagSource>();

        var qn = 0.0;
        foreach (var x in query)
        {
            qn += (double)x * x;
        }
        qn = Math.Sqrt(qn);
        if (qn == 0.0) qn = 1.0;

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT chunk_id, source, source_title, section, text, pdf_page, embedding
            FROM chunks
            WHERE embedding IS NOT NULL
              AND ($term IS NULL OR instr(lower(section), lower($term)) > 0
                   OR instr(lower(text), lower($term)) > 0)
            """;
        cmd.Parameters.AddWithValue("$term", (object?)exactTerm ?? DBNull.Value);
        using var reader = cmd.ExecuteReader();
        var scored = new List<(float Score, RagSource Hit)>();
        while (reader.Read())
        {
            var blob = (byte[])reader.GetValue(6);
            if (blob.Length != _embedDim * 4)
            {
                // Несовместимый эмбеддинг — пропускаем без фейковых метрик.
                continue;
            }
            double dot = 0.0, nn = 0.0;
            for (var i = 0; i < _embedDim; i++)
            {
                var v = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * 4, 4));
                dot += (double)query[i] * v;
                nn += (double)v * v;
            }
            var denom = qn * Math.Sqrt(nn);
            if (denom == 0.0) continue;
            var score = (float)(dot / denom);
            int? page = reader.IsDBNull(5) ? null : reader.GetInt32(5);
            var hit = new RagSource(
                ChunkId: reader.GetString(0),
                Source: reader.GetString(1),
                Title: reader.GetString(2),
                Section: reader.GetString(3),
                PdfPage: page,
                Text: reader.GetString(4),
                Score: score);
            scored.Add((score, hit));
        }
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var result = new List<RagSource>(Math.Min(topK, scored.Count));
        for (var i = 0; i < scored.Count && i < topK; i++)
        {
            result.Add(scored[i].Hit);
        }
        return result;
    }

    private string? ReadMetaString(string key)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        var result = cmd.ExecuteScalar();
        return result is null || result == DBNull.Value ? null : (string)result;
    }

    private int? ReadMetaInt(string key)
    {
        var raw = ReadMetaString(key);
        if (raw is null) return null;
        return int.TryParse(raw, out var n) ? n : null;
    }

    public void Dispose() => _connection.Dispose();
}
