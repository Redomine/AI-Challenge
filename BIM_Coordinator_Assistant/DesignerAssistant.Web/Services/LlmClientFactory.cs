using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Создаёт независимый LLM-клиент для RAG и автотеста.
/// Использует свой <see cref="HttpClient"/> с
/// увеличенным таймаутом, чтобы долгие RAG-запросы не
/// конкурировали с основной сессией.
/// </summary>
public sealed class LlmClientFactory
{
    private readonly IHttpClientFactory _factory;

    public LlmClientFactory(IHttpClientFactory factory)
    {
        _factory = factory;
    }

    public ILlmClient Create()
    {
        var provider = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_RAG_PROVIDER");
        if (string.Equals(provider, "gigachat", StringComparison.OrdinalIgnoreCase))
            return Create(LlmProvider.GigaChat);
        if (!string.IsNullOrWhiteSpace(provider) && !string.Equals(provider, "ollama", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("DESIGN_ASSISTANT_RAG_PROVIDER должен быть ollama или gigachat.");
        return Create(LlmProvider.Ollama);
    }

    public ILlmClient Create(LlmProvider provider)
    {
        var http = _factory.CreateClient("rag-llm");
        http.Timeout = TimeSpan.FromMinutes(5);
        if (provider == LlmProvider.GigaChat)
            return new GigaChatClient(http, AppOptions.FromEnvironment());
        http.BaseAddress = OllamaSettings.BaseAddress;
        return new OllamaLlmClient(http, OllamaSettings.Model, thinkingEnabled: false);
    }
}
