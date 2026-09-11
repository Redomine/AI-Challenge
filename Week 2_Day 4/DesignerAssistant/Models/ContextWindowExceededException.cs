namespace DesignerAssistant.Models;

public sealed class ContextWindowExceededException : InvalidOperationException
{
    public ContextWindowExceededException(
        int contextTokens,
        int reservedOutputTokens,
        int limitTokens,
        bool isEstimated)
        : base(CreateMessage(
            contextTokens,
            reservedOutputTokens,
            limitTokens,
            isEstimated))
    {
    }

    private static string CreateMessage(
        int contextTokens,
        int reservedOutputTokens,
        int limitTokens,
        bool isEstimated)
    {
        var mark = isEstimated ? "≈" : string.Empty;
        var overflow = contextTokens + reservedOutputTokens - limitTokens;

        return
            "Лимит контекстного окна превышен. Запрос не отправлен модели. " +
            $"Контекст: {mark}{contextTokens:N0}, " +
            $"резерв ответа: {reservedOutputTokens:N0}, " +
            $"лимит: {limitTokens:N0}, превышение: {mark}{overflow:N0} токенов. " +
            "При реальном переполнении API отклоняет весь запрос; " +
            "это не обрезка ответа. Очистите или сократите историю либо уменьшите резерв ответа.";
    }
}
