namespace DesignerAssistant.Configuration;

public sealed record AppOptions(
    string AuthorizationKey,
    string Scope,
    string Model,
    string TokenizerModel,
    int MaxOutputTokens,
    string DatabasePath,
    int ContextLimitTokens,
    decimal PricePerMillionTokens)
{
    public static AppOptions FromEnvironment()
    {
        var authorizationKey = Environment.GetEnvironmentVariable("GIGACHAT_AUTH_KEY");
        if (string.IsNullOrWhiteSpace(authorizationKey))
        {
            throw new InvalidOperationException(
                "Не задан GIGACHAT_AUTH_KEY. " +
                "Укажите ключ авторизации GigaChat в переменной окружения и запустите приложение повторно.");
        }

        var scope = Environment.GetEnvironmentVariable("GIGACHAT_SCOPE")
            ?? "GIGACHAT_API_PERS";

        var model = Environment.GetEnvironmentVariable("GIGACHAT_MODEL")
            ?? "GigaChat-2";
        var tokenizerModel = Environment.GetEnvironmentVariable("GIGACHAT_TOKENIZER_MODEL")
            ?? "GigaChat";

        var maxOutputTokensValue = Environment.GetEnvironmentVariable(
            "DESIGN_ASSISTANT_MAX_OUTPUT_TOKENS");
        var maxOutputTokens = int.TryParse(maxOutputTokensValue, out var parsed)
            ? parsed
            : 1200;

        if (maxOutputTokens <= 0)
        {
            throw new InvalidOperationException(
                "DESIGN_ASSISTANT_MAX_OUTPUT_TOKENS должно быть положительным числом.");
        }

        var databasePath = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_DB_PATH");
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            databasePath = Path.Combine(Environment.CurrentDirectory, "designer-assistant.db");
        }

        databasePath = Path.GetFullPath(databasePath);

        var contextLimitValue = Environment.GetEnvironmentVariable(
            "GIGACHAT_CONTEXT_LIMIT_TOKENS");
        var contextLimitTokens = int.TryParse(contextLimitValue, out var parsedLimit)
            ? parsedLimit
            : 128_000;
        if (contextLimitTokens <= 0)
        {
            throw new InvalidOperationException(
                "GIGACHAT_CONTEXT_LIMIT_TOKENS должно быть положительным числом.");
        }

        var priceValue = Environment.GetEnvironmentVariable(
            "GIGACHAT_PRICE_PER_MILLION_TOKENS");
        var pricePerMillionTokens = decimal.TryParse(
            priceValue,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsedPrice)
                ? parsedPrice
                : 65m;
        if (pricePerMillionTokens < 0)
        {
            throw new InvalidOperationException(
                "GIGACHAT_PRICE_PER_MILLION_TOKENS не может быть отрицательным.");
        }

        return new AppOptions(
            authorizationKey,
            scope,
            model,
            tokenizerModel,
            maxOutputTokens,
            databasePath,
            contextLimitTokens,
            pricePerMillionTokens);
    }
}
