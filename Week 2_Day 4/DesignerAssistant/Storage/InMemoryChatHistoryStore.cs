using DesignerAssistant.Models;

namespace DesignerAssistant.Storage;

public sealed class InMemoryChatHistoryStore : IChatHistoryStore
{
    private readonly List<ChatMessage> _messages = [];
    private CompressionState _compressionState = new(true, string.Empty, 0);

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

    public Task<CompressionState> LoadCompressionStateAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_compressionState);

    public Task SaveCompressionStateAsync(
        CompressionState state,
        CancellationToken cancellationToken = default)
    {
        _compressionState = state;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        _messages.Clear();
        _compressionState = _compressionState with
        {
            Summary = string.Empty,
            SummarizedMessageCount = 0
        };
        return Task.CompletedTask;
    }
}
