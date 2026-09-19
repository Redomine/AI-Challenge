using DesignerAssistant.Models;

namespace DesignerAssistant.Storage;

public sealed class InMemoryUserProfileStore : IUserProfileStore
{
    private readonly Dictionary<string, UserProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeName;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<UserProfile?> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_activeName is not null && _profiles.TryGetValue(_activeName, out var profile) ? profile : null);
    public Task<IReadOnlyList<UserProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserProfile>>(_profiles.Values.OrderBy(profile => profile.Name).ToArray());
    public Task SaveAsync(UserProfile profile, CancellationToken cancellationToken = default)
    {
        _profiles[profile.Name] = profile;
        _activeName = profile.Name;
        return Task.CompletedTask;
    }
    public Task<bool> SelectAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_profiles.ContainsKey(name)) return Task.FromResult(false);
        _activeName = name;
        return Task.FromResult(true);
    }
    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        _profiles.Remove(name);
        if (_activeName?.Equals(name, StringComparison.OrdinalIgnoreCase) == true) _activeName = null;
        return Task.CompletedTask;
    }
}
