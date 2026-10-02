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
/// Одна запись об отклонённом после фильтра чанке с фактической причиной.
/// Помогает UI и автотесту видеть, почему кандидат не попал в контекст.
/// </summary>
public sealed record RagRejectedChunk(
    string ChunkId,
    string Source,
    string Title,
    string Section,
    int? PdfPage,
    float Score,
    string Reason);

/// <summary>
/// Пошаговый журнал поиска RAG: оригинальный вопрос, фактически
/// использованный поисковый запрос, размеры выборок до и после фильтра,
/// оценки и список отклонённых чанков. Содержит только фактические
/// значения, никаких подделок.
/// </summary>
public sealed record RagSearchTrace(
    string OriginalQuestion,
    string SearchQuery,
    int PreFilterCount,
    int PostFilterCount,
    float Threshold,
    int PostFilterLimit,
    IReadOnlyList<float> PreFilterScores,
    IReadOnlyList<RagRejectedChunk> Rejected);

/// <summary>
/// Ответ RAG-режима: текст модели плюс фактический список источников.
/// Источники пустые — значит поиск был пустым или упал, и подтверждённого
/// факта в ответе нет. Поле <see cref="Trace"/> содержит фактический
/// журнал поиска (может быть null, если поиск не выполнялся).
/// </summary>
public sealed record RagAnswer(
    string Question,
    string Answer,
    bool SearchSucceeded,
    int RetrievedCount,
    IReadOnlyList<RagSource> Sources,
    string? Error,
    RagSearchTrace? Trace = null);

/// <summary>
/// Ответ «без RAG»: только текст модели, никакого поиска.
/// </summary>
public sealed record NoRagAnswer(
    string Question,
    string Answer,
    string? Error);

/// <summary>
/// Параметры одного RAG-запроса. Не мутируют singleton RagOptions —
/// каждый запрос собирает свой экземпляр. Используется UI и
/// автотестом, чтобы сравнивать baseline и enhanced без
/// побочных эффектов на shared state.
/// </summary>
public sealed record RagRunSettings(
    bool RewriteEnabled,
    int PreFilterK,
    float ScoreThreshold,
    int PostFilterK,
    TimeSpan RewriteTimeout);

/// <summary>
/// Один контрольный вопрос для автотеста. Источники ожидаются
/// по фактическим метаданным (source/pdf_page/section).
/// <para><c>ExpectedFactTerms</c> — список терминов, которые должны
/// присутствовать в ответе модели (для позитивного кейса).</para>
/// <para><c>ExpectedNoEvidence</c> — явный флаг «негативного» кейса:
/// ожидается, что в индексе нет подтверждающих фактов и модель
/// должна воздержаться от ответа. Пустой <c>ExpectedFactTerms</c>
/// на обычном вопросе НЕ считается негативом — это две разные
/// семантики. По умолчанию false для обратной совместимости со
/// старыми questions.json, где флаг отсутствует.</para>
/// </summary>
public sealed record AutoTestCase(
    string Question,
    string ExpectedSource,
    int? ExpectedPdfPage,
    string? ExpectedSectionContains,
    IReadOnlyList<string>? ExpectedFactTerms,
    bool ExpectedNoEvidence = false);

/// <summary>
/// Запуск одного вопроса в одном режиме: оценка плюс детальные
/// сведения о фактических терминах и источниках.
/// Поле <see cref="Trace"/> содержит фактический журнал поиска
/// RAG (для режима без поиска — null). Trace — последний
/// позиционный аргумент со значением по умолчанию, чтобы старые
/// тесты и вызовы с 16 аргументами продолжали компилироваться
/// без изменений.
/// </summary>
public sealed record AutoTestRunResult(
    int Index,
    string Mode,
    string Question,
    string ExpectedSource,
    int? ExpectedPdfPage,
    string? ExpectedSectionContains,
    bool ExpectedNoEvidence,
    bool SearchSucceeded,
    int RetrievedCount,
    IReadOnlyList<RagSource> Sources,
    string Answer,
    IReadOnlyList<string> ExpectedFactTerms,
    IReadOnlyList<string> FoundFactTerms,
    bool FactCheckPassed,
    bool SourceCheckPassed,
    bool SectionCheckPassed,
    string? Error,
    RagSearchTrace? Trace = null)
{
    /// <summary>
    /// Кейс «негативный»: явный флаг <c>ExpectedNoEvidence=true</c> в
    /// JSON. Пустой <c>ExpectedFactTerms</c> сам по себе не делает кейс
    /// негативным — это разные семантики.
    /// </summary>
    public bool IsNegativeCase => ExpectedNoEvidence;

    /// <summary>Стандартная фраза abstention, которую должна выдать модель при отсутствии фактов в индексе.</summary>
    public const string NoFactsAnswerPhrase = "Подтверждённых фактов в индексе не найдено";

    /// <summary>
    /// Считает, является ли ответ модели воздержанием от фактов.
    /// Используется как «нижняя граница» для негативных кейсов:
    /// либо в ответе нет ни одного expected_fact_terms, либо ответ
    /// содержит стандартную фразу abstention.
    /// </summary>
    public bool IsAbstentionAnswer
    {
        get
        {
            if (FoundFactTerms.Count > 0) return false;
            if (string.IsNullOrWhiteSpace(Answer)) return true;
            return Answer.Contains(NoFactsAnswerPhrase, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Полный успех для данного режима (с учётом optional checks).</summary>
    public bool Passed
    {
        get
        {
            if (Error is not null) return false;

            // Явный негативный кейс: критерии другие.
            if (IsNegativeCase)
            {
                if (Mode == "no-rag")
                {
                    // Для no-rag — достаточно abstention-ответа.
                    return IsAbstentionAnswer;
                }
                // RAG: поиск успешен, источников нет, ответ — abstention.
                return SearchSucceeded && Sources.Count == 0 && IsAbstentionAnswer;
            }

            return FactCheckPassed
                && SourceCheckPassed
                && (ExpectedSectionContains is null || SectionCheckPassed);
        }
    }
}

/// <summary>
/// Итог автотеста — список результатов плюс агрегированные счётчики
/// по всем режимам. Никаких фейковых метрик: оценка строится по
/// детерминированной проверке ожидаемых терминов/источников.
/// </summary>
public sealed record AutoTestReport(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<AutoTestRunResult> Runs,
    string? Error)
{
    public int Total => Runs.Count;
    public int Passed => Runs.Count(r => r.Passed);
    public int WithErrors => Runs.Count(r => r.Error is not null);

    public IReadOnlyList<AutoTestRunResult> NoRagRuns =>
        Runs.Where(r => r.Mode == "no-rag").ToArray();

    public IReadOnlyList<AutoTestRunResult> BaselineRuns =>
        Runs.Where(r => r.Mode == "baseline").ToArray();

    public IReadOnlyList<AutoTestRunResult> EnhancedRuns =>
        Runs.Where(r => r.Mode == "enhanced").ToArray();

    public IReadOnlyList<AutoTestRunResult> RagRuns =>
        BaselineRuns.Concat(EnhancedRuns).ToArray();

    /// <summary>Негативные кейсы: ожидаемых терминов нет, провал только при ошибке.</summary>
    public IReadOnlyList<AutoTestRunResult> NegativeCases =>
        Runs.Where(r => r.IsNegativeCase).ToArray();
}

/// <summary>
/// Агрегированные метрики по одному режиму: всего, прошло, ошибки,
/// среднее число найденных источников, число негативных кейсов и
/// сколько из них прошло «по нулевым ожиданиям».
/// </summary>
public sealed record AutoTestModeAggregate(
    string Mode,
    int Total,
    int Passed,
    int WithErrors,
    int NegativeTotal,
    int NegativePassed,
    double AverageRetrieved);

/// <summary>
/// Полный отчёт с агрегатами по режимам — то, что пишется в
/// rag-evaluation-*.json и показывается в UI.
/// </summary>
public sealed record AutoTestAggregateReport(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    AutoTestRunSettings Baseline,
    AutoTestRunSettings? Enhanced,
    IReadOnlyList<AutoTestModeAggregate> Aggregates,
    IReadOnlyList<AutoTestRunResult> NegativeCases,
    IReadOnlyList<AutoTestRunResult> Runs,
    string? Error);

/// <summary>
/// Конкретные настройки, с которыми выполнялся прогон режима:
/// нужно для воспроизводимости отчёта.
/// </summary>
public sealed record AutoTestRunSettings(
    bool RewriteEnabled,
    int PreFilterK,
    float ScoreThreshold,
    int PostFilterK);