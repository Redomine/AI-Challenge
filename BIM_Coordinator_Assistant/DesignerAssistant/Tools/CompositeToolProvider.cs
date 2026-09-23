using System.Text.Json;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tools;

public sealed class CompositeToolProvider(params IToolProvider[] providers) : IToolProvider, IToolConfirmationProvider, IToolOperationCoordinator
{
    private readonly IToolProvider[] _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    private IReadOnlyDictionary<string, IToolProvider> _owners = new Dictionary<string, IToolProvider>();

    public async Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default)
    {
        var definitions = new List<ToolDefinition>();
        var owners = new Dictionary<string, IToolProvider>(StringComparer.Ordinal);
        foreach (var provider in _providers)
        {
            foreach (var definition in await provider.GetToolsAsync(cancellationToken))
            {
                if (!owners.TryAdd(definition.Name, provider))
                    throw new InvalidOperationException($"Инструмент '{definition.Name}' зарегистрирован несколькими провайдерами.");
                definitions.Add(definition);
            }
        }
        _owners = owners;
        return definitions;
    }

    public Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default) =>
        _owners.TryGetValue(name, out var provider)
            ? provider.InvokeAsync(name, arguments, cancellationToken)
            : throw new InvalidOperationException($"Инструмент '{name}' отсутствует в текущем каталоге.");

    public Task<bool> ConfirmAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default) =>
        _owners.TryGetValue(name, out var provider) && provider is IToolConfirmationProvider confirming
            ? confirming.ConfirmAsync(name, arguments, cancellationToken)
            : Task.FromResult(false);

    public Task<string> WaitForCompletionAsync(string name, string initialResult, CancellationToken cancellationToken = default) =>
        _owners.TryGetValue(name, out var provider) && provider is IToolOperationCoordinator coordinator
            ? coordinator.WaitForCompletionAsync(name, initialResult, cancellationToken)
            : Task.FromResult(initialResult);
}
