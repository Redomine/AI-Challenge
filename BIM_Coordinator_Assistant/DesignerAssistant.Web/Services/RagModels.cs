namespace DesignerAssistant.Web.Services;

/// <summary>
/// Одна находка из индекса. Метаданные — фактические поля
/// из строки SQLite, без догадок и без выдуманных страниц.
/// </summary>
public sealed record RagSource(
    string ChunkId,
    string Source,
    string Title,
    string Section,
    int? PdfPage,
    string Text,
    float Score);

/// <summary>
/// Ответ RAG-режима: текст модели плюс фактический список источников.
/// Источники пустые — значит поиск был пустым или упал, и подтверждённого
/// факта в ответе нет.
/// </summary>
public sealed record RagAnswer(
    string Question,
    string Answer,
    bool SearchSucceeded,
    int RetrievedCount,
    IReadOnlyList<RagSource> Sources,
    string? Error);

/// <summary>
/// Ответ «без RAG»: только текст модели, никакого поиска.
/// </summary>
public sealed record NoRagAnswer(
    string Question,
    string Answer,
    string? Error);

/// <summary>
/// Один контрольный вопрос для автотеста. Источники ожидаются
/// по фактическим метаданным (source/pdf_page/section).
/// </summary>
public sealed record AutoTestCase(
    string Question,
    string ExpectedSource,
    int? ExpectedPdfPage,
    string? ExpectedSectionContains,
    IReadOnlyList<string>? ExpectedFactTerms);

/// <summary>
/// Запуск одного вопроса в одном режиме: оценка плюс детальные
/// сведения о фактических терминах и источниках.
/// </summary>
public sealed record AutoTestRunResult(
    int Index,
    string Mode,
    string Question,
    string ExpectedSource,
    int? ExpectedPdfPage,
    string? ExpectedSectionContains,
    bool SearchSucceeded,
    int RetrievedCount,
    IReadOnlyList<RagSource> Sources,
    string Answer,
    IReadOnlyList<string> ExpectedFactTerms,
    IReadOnlyList<string> FoundFactTerms,
    bool FactCheckPassed,
    bool SourceCheckPassed,
    bool SectionCheckPassed,
    string? Error);

/// <summary>
/// Итог автотеста — список результатов плюс агрегированные счётчики
/// по обоим режимам. Никаких фейковых метрик: оценка строится по
/// детерминированной проверке ожидаемых терминов/источников.
/// </summary>
public sealed record AutoTestReport(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<AutoTestRunResult> Runs,
    string? Error)
{
    public int Total => Runs.Count;
    public int Passed => Runs.Count(r =>
        r.Error is null && r.FactCheckPassed && r.SourceCheckPassed &&
        (r.ExpectedSectionContains is null || r.SectionCheckPassed));

    public int WithErrors => Runs.Count(r => r.Error is not null);
    public IReadOnlyList<AutoTestRunResult> NoRagRuns =>
        Runs.Where(r => r.Mode == "no-rag").ToArray();
    public IReadOnlyList<AutoTestRunResult> RagRuns =>
        Runs.Where(r => r.Mode == "rag").ToArray();
}