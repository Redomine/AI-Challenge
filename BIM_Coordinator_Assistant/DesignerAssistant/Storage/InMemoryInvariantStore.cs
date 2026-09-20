namespace DesignerAssistant.Storage;

public sealed class InMemoryInvariantStore : IInvariantStore
{
    private string _text = "";
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_text);
    public Task SaveAsync(string text, CancellationToken cancellationToken = default)
    {
        _text = text ?? "";
        return Task.CompletedTask;
    }
}
