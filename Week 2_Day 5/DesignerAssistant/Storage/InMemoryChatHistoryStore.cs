using DesignerAssistant.Models;

namespace DesignerAssistant.Storage;

public sealed class InMemoryChatHistoryStore : IChatHistoryStore
{
    private readonly List<ChatMessage> _messages = [];
    private ContextState _contextState = ContextState.CreateDefault();

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

    public Task ReplaceAsync(
        IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        _messages.Clear();
        _messages.AddRange(messages);
        return Task.CompletedTask;
    }

    public Task<ContextState> LoadContextStateAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_contextState);

    public Task SaveContextStateAsync(
        ContextState state,
        CancellationToken cancellationToken = default)
    {
        _contextState = state;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _messages.Clear();
        _contextState = ContextState.CreateDefault();
        return Task.CompletedTask;
    }
}
