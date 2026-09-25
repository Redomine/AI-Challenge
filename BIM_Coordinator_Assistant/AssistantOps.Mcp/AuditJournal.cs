using System.Text.Json;
using ModelContextProtocol.Server;
using System.ComponentModel;

public sealed class AuditJournal
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public string PathName { get; } = System.IO.Path.GetFullPath(
        Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_AUDIT_PATH") ??
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesignerAssistant", "audit.jsonl"));

    public async Task<object> AppendAsync(string taskId, string eventType, string source, string summary,
        bool ok, long durationMs, CancellationToken cancellationToken)
    {
        if (taskId.Length > 100 || eventType.Length > 80 || source.Length > 120 || summary.Length > 2000)
            throw new ArgumentException("Audit field exceeds size limit.");
        var record = new { timestamp = DateTimeOffset.UtcNow, taskId, eventType, source, summary, ok, durationMs };
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName)!);
            await File.AppendAllTextAsync(PathName, JsonSerializer.Serialize(record) + "\n", cancellationToken);
        }
        finally { _gate.Release(); }
        return new { ok = true, path = PathName };
    }

    public async Task<object> RecentAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(PathName)) return new { ok = true, entries = Array.Empty<JsonElement>() };
            var lines = await File.ReadAllLinesAsync(PathName, cancellationToken);
            return new { ok = true, entries = lines.TakeLast(limit).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray() };
        }
        finally { _gate.Release(); }
    }
}

[McpServerToolType]
public sealed class LogTools(AuditJournal journal)
{
    [McpServerTool(Name = "log_append_event")]
    [Description("Append a bounded operational event to the local JSONL audit journal. Do not include secrets or model parameters.")]
    public Task<object> AppendEvent(string taskId, string eventType, string source, string summary,
        bool ok, long durationMs, CancellationToken cancellationToken) =>
        journal.AppendAsync(taskId, eventType, source, summary, ok, durationMs, cancellationToken);

    [McpServerTool(Name = "log_recent_events", ReadOnly = true)]
    [Description("Read up to 100 most recent operational audit events.")]
    public Task<object> RecentEvents(int limit, CancellationToken cancellationToken) => journal.RecentAsync(limit, cancellationToken);
}
