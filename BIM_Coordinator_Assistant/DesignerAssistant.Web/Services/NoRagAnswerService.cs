using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Сервис ответа без RAG: чистый вызов LLM без поискового контекста
/// и без инструментов Revit. Используется для оценки «голой» модели
/// и как контрастный режим в автотесте.
/// </summary>
public sealed class NoRagAnswerService
{
    private const string Instructions = """
        Ты отвечаешь на вопрос пользователя. У тебя нет доступа к
        локальной базе знаний и инструментам Revit. Если вопрос про
        конкретный документ/страницу — прямо скажи, что без базы
        знаний подтвердить факт невозможно. Не выдумывай данные.
        """;

    private readonly ILlmClient _llm;
    private readonly TimeSpan _timeout;

    public NoRagAnswerService(ILlmClient llm, TimeSpan timeout)
    {
        _llm = llm;
        _timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(90) : timeout;
    }

    public async Task<NoRagAnswer> AskAsync(string question, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new NoRagAnswer(question, "", "Пустой вопрос.");
        }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            var response = await _llm.GenerateAsync(
                Instructions,
                new[] { new ChatMessage("user", question) },
                cts.Token);
            return new NoRagAnswer(question, response.Content, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NoRagAnswer(
                question,
                $"LLM превысил таймаут {_timeout.TotalSeconds:N0} с.",
                "timeout");
        }
        catch (Exception ex)
        {
            return new NoRagAnswer(question, $"Ошибка: {ex.Message}", ex.Message);
        }
    }
}