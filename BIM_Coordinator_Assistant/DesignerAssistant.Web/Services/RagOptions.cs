namespace DesignerAssistant.Web.Services;

/// <summary>Конфигурация RAG-поиска по локальному индексу.</summary>
public sealed record RagOptions(
    string IndexPath,
    string OllamaUrl,
    string EmbedModel,
    int TopK,
    int MaxContextChars,
    TimeSpan EmbedTimeout,
    int EmbedMaxRetries,
    TimeSpan LlmTimeout)
{
    public const string DefaultOllamaUrl = "http://127.0.0.1:11434";
    public const string DefaultEmbedModel = "qwen3-embedding:0.6b";
    public const int DefaultTopK = 4;
    public const int DefaultMaxContextChars = 6000;

    public static RagOptions FromEnvironment()
    {
        var indexPath = Environment.GetEnvironmentVariable("RAG_INDEX_PATH");
        if (string.IsNullOrWhiteSpace(indexPath))
        {
            // По умолчанию — каталог, который поставляется отдельно от репозитория.
            indexPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "Выгрузка страниц", "index_out", "structural.sqlite3");
        }

        var ollamaUrl = Environment.GetEnvironmentVariable("RAG_OLLAMA_URL")
            ?? DefaultOllamaUrl;
        var embedModel = Environment.GetEnvironmentVariable("RAG_EMBED_MODEL")
            ?? DefaultEmbedModel;
        var topK = ReadPositiveInt("RAG_TOP_K", DefaultTopK);
        var maxContextChars = ReadPositiveInt("RAG_MAX_CONTEXT_CHARS", DefaultMaxContextChars);
        var embedTimeoutSeconds = ReadPositiveInt("RAG_EMBED_TIMEOUT_SECONDS", 60);
        var embedMaxRetries = ReadPositiveInt("RAG_EMBED_MAX_RETRIES", 2);
        var llmTimeoutSeconds = ReadPositiveInt("RAG_LLM_TIMEOUT_SECONDS", 90);

        return new RagOptions(
            IndexPath: indexPath,
            OllamaUrl: ollamaUrl,
            EmbedModel: embedModel,
            TopK: topK,
            MaxContextChars: maxContextChars,
            EmbedTimeout: TimeSpan.FromSeconds(embedTimeoutSeconds),
            EmbedMaxRetries: embedMaxRetries,
            LlmTimeout: TimeSpan.FromSeconds(llmTimeoutSeconds));
    }

    private static int ReadPositiveInt(string name, int defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"{name} должен быть положительным целым числом, получено: {raw}");
        }
        if (parsed <= 0)
        {
            throw new InvalidOperationException(
                $"{name} должен быть положительным, получено: {parsed}");
        }
        return parsed;
    }
}