using DesignerAssistant.Models;

namespace DesignerAssistant.Storage;

public interface IUserProfileStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<UserProfile?> LoadAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserProfile>> ListAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(UserProfile profile, CancellationToken cancellationToken = default);
    Task<bool> SelectAsync(string name, CancellationToken cancellationToken = default);
    Task DeleteAsync(string name, CancellationToken cancellationToken = default);
}
