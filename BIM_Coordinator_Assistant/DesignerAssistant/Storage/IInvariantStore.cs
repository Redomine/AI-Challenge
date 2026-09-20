namespace DesignerAssistant.Storage;

public interface IInvariantStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<string> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(string text, CancellationToken cancellationToken = default);
}
