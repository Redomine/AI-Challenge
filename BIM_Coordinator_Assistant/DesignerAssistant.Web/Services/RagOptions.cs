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
    TimeSpan LlmTimeout,
    bool RewriteEnabled = false,
    TimeSpan RewriteTimeout = default,
    int PreFilterK = 12,
    float ScoreThreshold = 0f,
    int PostFilterK = -1)
{
    public const string DefaultOllamaUrl = "http://127.0.0.1:11434";
    public const string DefaultEmbedModel = "qwen3-embedding:0.6b";
    public const int DefaultTopK = 4;
    public const int DefaultMaxContextChars = 6000;
    public const int DefaultPreFilterK = 12;
    public const float DefaultScoreThreshold = 0.5f;
    public const int DefaultPostFilterK = 4;
    public const int DefaultRewriteTimeoutSeconds = 20;

    public int EffectivePostFilterK =>
        PostFilterK > 0 ? PostFilterK : TopK;

    public TimeSpan EffectiveRewriteTimeout =>
        RewriteTimeout > TimeSpan.Zero
            ? RewriteTimeout
            : TimeSpan.FromSeconds(DefaultRewriteTimeoutSeconds);

    /// <summary>
    /// Настройки baseline-прогона: оригинальный вопрос, TopK, без
    /// переписывания и без score-порога. ScoreThreshold=-1f означает
    /// «не фильтровать по порогу» — сохраняем оригинальную top-K по
    /// косинусу, включая отрицательные оценки (валидный диапазон
    /// cosine ∈ [-1, 1]). Делегирует существующему конструктору без
    /// побочных эффектов.
    /// </summary>
    public RagRunSettings ToBaselineSettings() => new(
        RewriteEnabled: false,
        PreFilterK: TopK,
        ScoreThreshold: -1f,
        PostFilterK: TopK,
        RewriteTimeout: EffectiveRewriteTimeout);

    /// <summary>
    /// Настройки расширенного прогона: переписывание включено,
    /// pre-K = <see cref="PreFilterK"/>, post-K = <see cref="PostFilterK"/>,
    /// порог = <see cref="ScoreThreshold"/>. При выходе post-K за pre-K —
    /// — post ограничивается до pre-K с сохранением исходного
    /// значения в настройках (валидация отдельная, см. TryCreate...).
    /// </summary>
    public RagRunSettings ToEnhancedSettings() => new(
        RewriteEnabled: true,
        PreFilterK: PreFilterK,
        ScoreThreshold: ScoreThreshold,
        PostFilterK: Math.Min(EffectivePostFilterK, PreFilterK),
        RewriteTimeout: EffectiveRewriteTimeout);

    /// <summary>
    /// Валидирует пользовательские параметры и собирает
    /// <see cref="RagRunSettings"/> для одиночного запроса.
    /// Диапазоны: pre-K и post-K ≥ 1, post-K ≤ pre-K,
    /// threshold ∈ [-1, 1]. Без мутации shared state — это
    /// чистая функция от переданных значений.
    /// </summary>
    public static RagRunSettings TryCreateSettings(
        bool rewriteEnabled,
        int preFilterK,
        float scoreThreshold,
        int postFilterK,
        TimeSpan? rewriteTimeout = null)
    {
        if (preFilterK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(preFilterK),
                "Pre-K должен быть положительным.");
        }
        if (postFilterK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(postFilterK),
                "Post-K должен быть положительным.");
        }
        if (postFilterK > preFilterK)
        {
            throw new ArgumentException(
                $"Post-K ({postFilterK}) не может быть больше Pre-K ({preFilterK}).",
                nameof(postFilterK));
        }
        if (scoreThreshold < -1f || scoreThreshold > 1f || float.IsNaN(scoreThreshold))
        {
            throw new ArgumentOutOfRangeException(nameof(scoreThreshold),
                "Score threshold должен быть в диапазоне [-1, 1].");
        }
        return new RagRunSettings(
            RewriteEnabled: rewriteEnabled,
            PreFilterK: preFilterK,
            ScoreThreshold: scoreThreshold,
            PostFilterK: postFilterK,
            RewriteTimeout: rewriteTimeout is { } t && t > TimeSpan.Zero
                ? t
                : TimeSpan.FromSeconds(DefaultRewriteTimeoutSeconds));
    }

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
        var rewriteEnabled = ReadBool("RAG_REWRITE_ENABLED", false);
        var rewriteTimeoutSeconds = ReadNonNegativeInt("RAG_REWRITE_TIMEOUT_SECONDS", DefaultRewriteTimeoutSeconds);
        var preFilterK = ReadPositiveInt("RAG_PRE_FILTER_K", DefaultPreFilterK);
        var scoreThreshold = ReadScoreThreshold("RAG_SCORE_THRESHOLD", DefaultScoreThreshold);
        var postFilterK = ReadPositiveInt("RAG_POST_FILTER_K", DefaultPostFilterK);

        if (postFilterK > preFilterK)
        {
            throw new InvalidOperationException(
                $"RAG_POST_FILTER_K ({postFilterK}) не может быть больше RAG_PRE_FILTER_K ({preFilterK}).");
        }

        return new RagOptions(
            IndexPath: indexPath,
            OllamaUrl: ollamaUrl,
            EmbedModel: embedModel,
            TopK: topK,
            MaxContextChars: maxContextChars,
            EmbedTimeout: TimeSpan.FromSeconds(embedTimeoutSeconds),
            EmbedMaxRetries: embedMaxRetries,
            LlmTimeout: TimeSpan.FromSeconds(llmTimeoutSeconds),
            RewriteEnabled: rewriteEnabled,
            RewriteTimeout: TimeSpan.FromSeconds(rewriteTimeoutSeconds),
            PreFilterK: preFilterK,
            ScoreThreshold: scoreThreshold,
            PostFilterK: postFilterK);
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

    private static int ReadNonNegativeInt(string name, int defaultValue)
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
                $"{name} должен быть неотрицательным целым числом, получено: {raw}");
        }
        if (parsed < 0)
        {
            throw new InvalidOperationException(
                $"{name} должен быть неотрицательным, получено: {parsed}");
        }
        return parsed;
    }

    private static float ReadScoreThreshold(string name, float defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (!float.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            throw new InvalidOperationException(
                $"{name} должен быть числом в диапазоне [-1, 1], получено: {raw}");
        }
        if (parsed < -1f || parsed > 1f)
        {
            throw new InvalidOperationException(
                $"{name} должен быть в диапазоне [-1, 1], получено: {raw}");
        }
        return parsed;
    }

    private static bool ReadBool(string name, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }
        if (bool.TryParse(raw, out var parsed))
        {
            return parsed;
        }
        if (raw == "1") return true;
        if (raw == "0") return false;
        throw new InvalidOperationException(
            $"{name} должен быть true/false/1/0, получено: {raw}");
    }
}
