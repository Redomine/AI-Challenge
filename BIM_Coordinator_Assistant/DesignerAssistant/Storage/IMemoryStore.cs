using DesignerAssistant.Models;

namespace DesignerAssistant.Storage;

public interface IMemoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemoryEntry>> LoadAsync(MemoryLayer layer, string taskId = "current", CancellationToken cancellationToken = default);
    Task UpsertAsync(MemoryLayer layer, string key, string value, MemorySource source, EvidenceStatus status, string taskId = "current", CancellationToken cancellationToken = default);
    Task ClearAsync(MemoryLayer layer, string taskId = "current", CancellationToken cancellationToken = default);
}
