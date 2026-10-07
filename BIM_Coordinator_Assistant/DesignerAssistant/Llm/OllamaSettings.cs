namespace DesignerAssistant.Llm;

public static class OllamaSettings
{
    public static string Model => Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_OLLAMA_MODEL") is { Length: > 0 } model
        ? model : "qwen3:4b";

    public static Uri BaseAddress
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_OLLAMA_URL") ?? "http://127.0.0.1:11434";
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttp || !uri.IsLoopback)
                throw new InvalidOperationException("DESIGN_ASSISTANT_OLLAMA_URL должен указывать на локальный HTTP-сервер Ollama.");
            return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        }
    }
}
