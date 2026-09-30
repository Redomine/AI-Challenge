using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Режим RAG: включает локальный поиск по индексу + LLM с контекстом,
/// либо обходится только LLM. Эти режимы — контрастная пара для
/// автотеста и для UI-переключателя в Home.razor.
/// </summary>
public enum RagMode
{
    Rag,
    NoRag,
}

/// <summary>
/// Объединённый ответ независимо от режима. Режим и текст ошибки
/// возвращаются всегда, чтобы UI мог показать «что произошло».
/// </summary>
public sealed record RagQueryResult(
    string Question,
    RagMode Mode,
    string Answer,
    bool SearchSucceeded,
    int RetrievedCount,
    IReadOnlyList<RagSource> Sources,
    string? Error);

/// <summary>
/// Фасад для UI и автотеста. Не содержит состояния: при каждом
/// вызове создаёт результат заново, делегируя работу сервисам.
/// Прогресс передаётся в <see cref="IProgress{T}"/> — это
/// единственный канал обратной связи, чтобы UI мог обновляться
/// по мере обработки вопросов в автотесте.
/// </summary>
public sealed class RagQueryService
{
    private readonly RagAnswerService _rag;
    private readonly NoRagAnswerService _noRag;

    public RagQueryService(RagAnswerService rag, NoRagAnswerService noRag)
    {
        _rag = rag;
        _noRag = noRag;
    }

    public RagAnswerService Rag => _rag;
    public NoRagAnswerService NoRag => _noRag;

    public async Task<RagQueryResult> QueryAsync(
        string question,
        RagMode mode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new RagQueryResult(
                question ?? "",
                mode,
                "",
                false,
                0,
                Array.Empty<RagSource>(),
                "Пустой вопрос.");
        }

        if (mode == RagMode.NoRag)
        {
            var noRag = await _noRag.AskAsync(question, cancellationToken);
            return new RagQueryResult(
                noRag.Question,
                RagMode.NoRag,
                noRag.Answer,
                false,
                0,
                Array.Empty<RagSource>(),
                noRag.Error);
        }

        var rag = await _rag.AskAsync(question, cancellationToken);
        return new RagQueryResult(
            rag.Question,
            RagMode.Rag,
            rag.Answer,
            rag.SearchSucceeded,
            rag.RetrievedCount,
            rag.Sources,
            rag.Error);
    }
}