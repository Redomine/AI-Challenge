using DesignerAssistant.Llm;
using DesignerAssistant.Models;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Режимы RAG-фасада.
/// <list type="bullet">
/// <item><see cref="NoRag"/> — прямой запрос LLM без поиска.</item>
/// <item><see cref="Rag"/> — baseline: оригинальный вопрос,
/// переписывание выключено, top-K = 4, без score-порога.</item>
/// <item><see cref="Enhanced"/> — расширенный: переписывание
/// включено, pre-K, score-порог и post-K задаются настройками.</item>
/// </list>
/// </summary>
public enum RagMode
{
    Rag,
    NoRag,
    Enhanced,
}

public enum RagLlmProvider { Ollama, GigaChat }

/// <summary>
/// Объединённый ответ независимо от режима. Режим и текст ошибки
/// возвращаются всегда, чтобы UI мог показать «что произошло».
/// Поле <see cref="Trace"/> содержит фактический журнал поиска
/// RAG (для режима без поиска — null).
/// Поле <see cref="Settings"/> содержит фактические настройки,
/// с которыми выполнялся запрос — для воспроизводимости в UI/отчёте.
/// Поля <see cref="Citations"/>, <see cref="Abstained"/> и
/// <see cref="AbstentionReason"/> добавлены на День 24 для обязательных
/// цитат и явного «Не знаю».
/// </summary>
public sealed record RagQueryResult(
    string Question,
    RagMode Mode,
    string Answer,
    bool SearchSucceeded,
    int RetrievedCount,
    IReadOnlyList<RagSource> Sources,
    string? Error,
    RagSearchTrace? Trace = null,
    RagRunSettings? Settings = null,
    IReadOnlyList<RagCitation>? Citations = null,
    bool Abstained = false,
    RagAbstentionReason AbstentionReason = RagAbstentionReason.None,
    string? ClarificationQuestion = null)
{
    /// <summary>Режим в виде строки для логов и UI.</summary>
    public string ModeLabel => Mode switch
    {
        RagMode.Rag => "baseline",
        RagMode.Enhanced => "enhanced",
        RagMode.NoRag => "no-rag",
        _ => Mode.ToString(),
    };

    /// <summary>Фактический список цитат без null.</summary>
    public IReadOnlyList<RagCitation> CitationsSafe => Citations ?? Array.Empty<RagCitation>();
}

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
    private readonly RagOptions _options;
    private readonly RagAnswerService? _cloudRag;
    private readonly NoRagAnswerService? _cloudNoRag;

    public RagQueryService(
        RagAnswerService rag,
        NoRagAnswerService noRag,
        RagOptions options)
    {
        _rag = rag;
        _noRag = noRag;
        _options = options;
    }

    public RagQueryService(RagAnswerService rag, NoRagAnswerService noRag, RagOptions options,
        RagAnswerService? cloudRag, NoRagAnswerService? cloudNoRag) : this(rag, noRag, options)
    {
        _cloudRag = cloudRag;
        _cloudNoRag = cloudNoRag;
    }

    public RagAnswerService Rag => _rag;
    public NoRagAnswerService NoRag => _noRag;
    public RagOptions Options => _options;
    public bool CloudAvailable => _cloudRag is not null && _cloudNoRag is not null;

    public Task<RagQueryResult> QueryAsync(RagLlmProvider provider, string question, RagMode mode,
        RagRunSettings? settings = null, CancellationToken cancellationToken = default) =>
        QueryCoreAsync(question, mode, settings, null, provider, cancellationToken);

    /// <summary>
    /// Прогон по дефолтным настройкам. Для UI-переключателя и
    /// простых сценариев: режим определяет, какие настройки брать.
    /// </summary>
    public Task<RagQueryResult> QueryAsync(
        string question,
        RagMode mode,
        CancellationToken cancellationToken = default) =>
        QueryAsync(question, mode, null, cancellationToken);

    /// <summary>
    /// Прогон с пользовательскими настройками. Если <paramref name="overrideSettings"/>
    /// не null — используются они (с проверкой диапазонов).
    /// Иначе — дефолтные для данного режима. Параметры UI
    /// валидируются на входе, мутации singleton RagOptions не происходит.
    /// </summary>
    public async Task<RagQueryResult> QueryAsync(
        string question,
        RagMode mode,
        RagRunSettings? overrideSettings,
        CancellationToken cancellationToken = default) =>
        await QueryCoreAsync(question, mode, overrideSettings, null, RagLlmProvider.Ollama, cancellationToken);

    public async Task<RagQueryResult> QueryAsync(
        string question,
        RagMode mode,
        RagRunSettings? overrideSettings,
        string? answerContext,
        CancellationToken cancellationToken = default)
        => await QueryCoreAsync(question, mode, overrideSettings, answerContext, RagLlmProvider.Ollama, cancellationToken);

    private async Task<RagQueryResult> QueryCoreAsync(
        string question, RagMode mode, RagRunSettings? overrideSettings, string? answerContext,
        RagLlmProvider provider, CancellationToken cancellationToken)
    {
        if (provider == RagLlmProvider.GigaChat && !CloudAvailable)
            throw new InvalidOperationException("GigaChat недоступен: задайте GIGACHAT_AUTH_KEY и перезапустите приложение.");
        var ragService = provider == RagLlmProvider.GigaChat ? _cloudRag! : _rag;
        var noRagService = provider == RagLlmProvider.GigaChat ? _cloudNoRag! : _noRag;
        if (string.IsNullOrWhiteSpace(question))
        {
            return new RagQueryResult(
                question ?? "",
                mode,
                "",
                false,
                0,
                Array.Empty<RagSource>(),
                "Пустой вопрос.",
                null,
                null);
        }

        if (mode == RagMode.NoRag)
        {
            var noRag = await noRagService.AskAsync(question, cancellationToken);
            return new RagQueryResult(
                noRag.Question,
                RagMode.NoRag,
                noRag.Answer,
                false,
                0,
                Array.Empty<RagSource>(),
                noRag.Error,
                null,
                null,
                null,
                noRag.Abstained,
                noRag.AbstentionReason,
                noRag.ClarificationQuestion);
        }

        // Rag и Enhanced: используем либо overrideSettings (с валидацией),
        // либо дефолт для режима.
        RagRunSettings effective;
        try
        {
            effective = overrideSettings ?? DefaultForMode(mode);
        }
        catch (Exception ex)
        {
            // Невалидные пользовательские параметры — возвращаем ошибку
            // как часть результата, чтобы UI мог её показать без падения.
            return new RagQueryResult(
                question,
                mode,
                "",
                false,
                0,
                Array.Empty<RagSource>(),
                $"Невалидные параметры: {ex.Message}",
                null,
                overrideSettings);
        }

        var rag = await ragService.AskAsync(question, effective, answerContext, cancellationToken);
        return new RagQueryResult(
            rag.Question,
            mode,
            rag.Answer,
            rag.SearchSucceeded,
            rag.RetrievedCount,
            rag.Sources,
            rag.Error,
            rag.Trace,
            effective,
            rag.CitationsSafe,
            rag.Abstained,
            rag.AbstentionReason,
            rag.ClarificationQuestion);
    }

    /// <summary>
    /// Дефолтные настройки для режима (без пользовательского override).
    /// День 24: для <see cref="RagMode.Rag"/> и NoRag в проде
    /// используются production-настройки (с порогом), чтобы чат
    /// не отвечал на слабом контексте. Comparison-режим берёт явный
    /// legacy baseline без threshold через <see cref="LegacyBaselineSettings"/>.
    /// </summary>
    public RagRunSettings DefaultForMode(RagMode mode) => mode switch
    {
        RagMode.Enhanced => _options.ToEnhancedSettings(),
        _ => _options.ToProductionSettings(),
    };

    /// <summary>
    /// Legacy-baseline без фильтра по скору. Используется только
    /// comparison-режимом (День 23) для сравнения с enhanced.
    /// </summary>
    public RagRunSettings LegacyBaselineSettings() => _options.ToBaselineSettings();
}
