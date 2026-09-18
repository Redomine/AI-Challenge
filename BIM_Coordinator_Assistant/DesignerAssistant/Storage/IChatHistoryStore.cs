using DesignerAssistant.Models;

namespace DesignerAssistant.Storage;

public interface IChatHistoryStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatMessage>> LoadAsync(
        CancellationToken cancellationToken = default);

    Task AppendAsync(
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
