using System.Text.Json;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public enum LlmProvider { Ollama, GigaChat }

public sealed class SwitchableLlmClient : IToolCallingLlmClient, IStructuredLlmClient
{
    private readonly OllamaLlmClient _ollama;
    private readonly GigaChatClient _gigaChat;
    private readonly bool _hasGigaChatKey;

    public SwitchableLlmClient(OllamaLlmClient ollama, GigaChatClient gigaChat, bool hasGigaChatKey)
    {
        _ollama = ollama;
        _gigaChat = gigaChat;
        _hasGigaChatKey = hasGigaChatKey;
    }

    public LlmProvider Provider { get; private set; } = LlmProvider.Ollama;
    public string? LastReasoning { get; private set; }
    public bool HasResponse { get; private set; }
    public event Action? Changed;

    public void Select(LlmProvider provider)
    {
        if (provider == LlmProvider.GigaChat && !_hasGigaChatKey)
            throw new InvalidOperationException("Для GigaChat задайте GIGACHAT_AUTH_KEY и перезапустите приложение.");
        Provider = provider;
        LastReasoning = null;
        HasResponse = false;
        Changed?.Invoke();
    }

    private IToolCallingLlmClient Current => Provider == LlmProvider.Ollama ? _ollama : _gigaChat;
    private IStructuredLlmClient CurrentStructured => Provider == LlmProvider.Ollama ? _ollama : _gigaChat;

    public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts,
        CancellationToken cancellationToken = default) => Current.CountTextTokensAsync(texts, cancellationToken);

    public async Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages,
        CancellationToken cancellationToken = default) =>
        Capture(await Current.GenerateAsync(instructions, messages, cancellationToken));

    public async Task<LlmResponse> GenerateStructuredAsync(string instructions, IReadOnlyCollection<ChatMessage> messages,
        JsonElement schema, CancellationToken cancellationToken = default) =>
        Capture(await CurrentStructured.GenerateStructuredAsync(instructions, messages, schema, cancellationToken));

    public async Task<LlmResponse> GenerateWithToolsAsync(string instructions, IReadOnlyCollection<ChatMessage> messages,
        IToolProvider toolProvider, Action<string>? trace = null, CancellationToken cancellationToken = default) =>
        Capture(await Current.GenerateWithToolsAsync(instructions, messages, toolProvider, trace, cancellationToken));

    private LlmResponse Capture(LlmResponse response)
    {
        LastReasoning = response.Reasoning;
        HasResponse = true;
        Changed?.Invoke();
        return response;
    }
}
