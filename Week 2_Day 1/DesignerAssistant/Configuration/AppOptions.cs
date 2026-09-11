namespace DesignerAssistant.Configuration;

public sealed record AppOptions(
    string AuthorizationKey,
    string Scope,
    string Model,
    int MaxOutputTokens)
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

        return new AppOptions(authorizationKey, scope, model, maxOutputTokens);
    }
}
