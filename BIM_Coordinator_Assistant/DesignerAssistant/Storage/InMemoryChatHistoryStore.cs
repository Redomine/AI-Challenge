using DesignerAssistant.Models;

namespace DesignerAssistant.Storage;

public sealed class InMemoryChatHistoryStore : IChatHistoryStore
{
    private readonly List<ChatMessage> _messages = [];

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<ChatMessage>> LoadAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(_messages.ToArray());

    public Task AppendAsync(
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        _messages.AddRange(messages);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _messages.Clear();
        return Task.CompletedTask;
    }
}
