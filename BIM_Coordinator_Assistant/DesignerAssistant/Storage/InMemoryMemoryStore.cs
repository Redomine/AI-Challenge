using DesignerAssistant.Models;

namespace DesignerAssistant.Storage;

public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly List<MemoryEntry> _entries = [];
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<MemoryEntry>> LoadAsync(MemoryLayer layer, string taskId = "current", CancellationToken cancellationToken = default)
    {
        var scope = layer == MemoryLayer.LongTerm ? "global" : taskId;
        return Task.FromResult<IReadOnlyList<MemoryEntry>>(_entries.Where(entry => entry.Layer == layer && entry.TaskId == scope).OrderBy(entry => entry.Key).ToArray());
    }

    public Task UpsertAsync(MemoryLayer layer, string key, string value, MemorySource source, EvidenceStatus status, string taskId = "current", CancellationToken cancellationToken = default)
    {
        var scope = layer == MemoryLayer.LongTerm ? "global" : taskId;
        _entries.RemoveAll(entry => entry.Layer == layer && entry.TaskId == scope && entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        var now = DateTimeOffset.UtcNow;
        _entries.Add(new MemoryEntry(_entries.Count + 1, layer, key, value, source, status, scope, now, now));
        return Task.CompletedTask;
    }

    public Task ClearAsync(MemoryLayer layer, string taskId = "current", CancellationToken cancellationToken = default)
    {
        var scope = layer == MemoryLayer.LongTerm ? "global" : taskId;
        _entries.RemoveAll(entry => entry.Layer == layer && entry.TaskId == scope);
        return Task.CompletedTask;
    }
}
