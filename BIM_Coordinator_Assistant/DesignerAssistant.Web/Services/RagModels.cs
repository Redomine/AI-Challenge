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
/// Причина abstention в RAG-ответе. Различаем «нет подтверждающих
/// источников» (модель корректно отказалась) от технических ошибок
/// поиска или LLM. UI показывает соответствующее сообщение.
/// </summary>
public enum RagAbstentionReason
{
    /// <summary>Ответ дан; abstention не сработал.</summary>
    None = 0,
    /// <summary>Поиск не выполнен (embedding, индекс или HTTP).</summary>
    SearchError = 1,
    /// <summary>Поиск выполнен, но кандидаты отклонены по threshold или пусты.</summary>
    NoEvidence = 2,
    /// <summary>LLM не вернул ответ (таймаут или исключение).</summary>
    LlmError = 3,
    /// <summary>LLM вернул ответ, но без валидных цитат и подтверждающих цитат.</summary>
    UnsupportedAnswer = 4,
}

/// <summary>
/// Цитата, на которую ссылается модель. Источник и chunk_id
/// обязаны указывать на реальный чанк из контекста; цитата —
/// дословный фрагмент из <see cref="RagSource.Text"/>.
/// </summary>
public sealed record RagCitation(
    string ChunkId,
    string Source,
    string Section,
    int? PdfPage,
    string Quote,
    bool Verified)
{
    /// <summary>Короткая подпись источника для UI и отчётов.</summary>
    public string Reference
    {
        get
        {
            var page = PdfPage is int p ? $", стр. {p}" : "";
            return $"[{Source}{page}, {ChunkId}]";
        }
    }
}

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
/// Поля <see cref="Citations"/>, <see cref="Abstained"/> и
/// <see cref="AbstentionReason"/> добавлены на День 24 для обязательных
/// цитат и явного «Не знаю» при отсутствии доказательств.
/// </summary>
public sealed record RagAnswer(
    string Question,
    string Answer,
    bool SearchSucceeded,
    int RetrievedCount,
    IReadOnlyList<RagSource> Sources,
    string? Error,
    RagSearchTrace? Trace = null,
    IReadOnlyList<RagCitation>? Citations = null,
    bool Abstained = false,
    RagAbstentionReason AbstentionReason = RagAbstentionReason.None,
    string? ClarificationQuestion = null)
{
    /// <summary>Фактический список цитат без null.</summary>
    public IReadOnlyList<RagCitation> CitationsSafe => Citations ?? Array.Empty<RagCitation>();
}

/// <summary>
/// Ответ «без RAG»: только текст модели, никакого поиска.
/// </summary>
public sealed record NoRagAnswer(
    string Question,
    string Answer,
    string? Error,
    bool Abstained = false,
    RagAbstentionReason AbstentionReason = RagAbstentionReason.None,
    string? ClarificationQuestion = null);

/// <summary>
/// Стандартизованное сообщение abstention для RAG. Используется
/// моделью и валидатором, когда подтверждающих источников нет.
/// </summary>
public static class RagAbstentionMessages
{
    public const string Russian = "Не знаю";
    public const string DefaultClarification = "Уточните, пожалуйста, вопрос — текущих данных в индексе недостаточно для подтверждённого ответа.";
}

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
    bool ExpectedNoEvidence = false,
    string? ExpectedKind = null,
    IReadOnlyList<string>? ForbiddenTerms = null);

/// <summary>
/// Запуск одного вопроса в одном режиме: оценка плюс детальные
/// сведения о фактических терминах и источниках.
/// Поле <see cref="Trace"/> содержит фактический журнал поиска
/// RAG (для режима без поиска — null). Trace — последний
/// позиционный аргумент со значением по умолчанию, чтобы старые
/// тесты и вызовы с 16 аргументами продолжали компилироваться
/// без изменений.
/// Поля <see cref="Citations"/>, <see cref="QuoteCheckPassed"/>,
/// <see cref="AnswerSupportedByQuotes"/>, <see cref="Abstained"/>
/// и <see cref="AbstentionReason"/> добавлены на День 24 для
/// обязательных цитат и проверки «Не знаю».
/// <para>Поле <see cref="IsDay24Evaluation"/> — флаг «это День 24»,
/// который выставляется <see cref="AutoTestRunner"/> по
/// <c>questions.json</c> с <c>version=day24-*</c> или имени файла с
/// <c>day24</c>. Используется в <see cref="Passed"/> для
/// дополнительной проверки <c>QuoteCheckPassed</c> и
/// <c>AnswerSupportedByQuotes</c> на позитивных кейсах Дня 24 и для
/// ужесточения требований к негативным кейсам Дня 24 (явная фраза
/// «Не знаю» + уточняющий вопрос + отсутствие запрещённых
/// фактов). По умолчанию <c>false</c> — старые режимы
/// «сравнения» сохраняют прежнюю семантику.</para>
/// <para>Поле <see cref="ClarificationQuestion"/> хранит
/// фактический уточняющий вопрос от модели, если она abstentionила;
/// используется в негативных кейсах Дня 24.</para>
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
    RagSearchTrace? Trace = null,
    IReadOnlyList<RagCitation>? Citations = null,
    bool QuoteCheckPassed = false,
    bool AnswerSupportedByQuotes = false,
    bool Abstained = false,
    RagAbstentionReason AbstentionReason = RagAbstentionReason.None,
    string? ExpectedKind = null,
    IReadOnlyList<string>? ForbiddenTerms = null,
    IReadOnlyList<string>? FoundForbiddenTerms = null,
    bool ForbiddenTermsCheckPassed = true,
    string? ClarificationQuestion = null,
    bool IsDay24Evaluation = false)
{
    /// <summary>
    /// Кейс «негативный»: явный флаг <c>ExpectedNoEvidence=true</c> в
    /// JSON. Пустой <c>ExpectedFactTerms</c> сам по себе не делает кейс
    /// негативным — это разные семантики.
    /// </summary>
    public bool IsNegativeCase => ExpectedNoEvidence;

    /// <summary>День 24 — позитивный кейс: выставлен файл Day 24 и нет <c>ExpectedNoEvidence</c>.</summary>
    public bool IsDay24PositiveCase => IsDay24Evaluation && !IsNegativeCase;

    /// <summary>День 24 — негативный кейс: выставлен файл Day 24 и есть <c>ExpectedNoEvidence</c>.</summary>
    public bool IsDay24NegativeCase => IsDay24Evaluation && IsNegativeCase;

    /// <summary>Стандартная фраза abstention, которую должна выдать модель при отсутствии фактов в индексе.</summary>
    public const string NoFactsAnswerPhrase = "Подтверждённых фактов в индексе не найдено";

    /// <summary>День 24 — стандартная фраза abstention «Не знаю».</summary>
    public const string Day24AbstentionPhrase = "Не знаю";

    /// <summary>Фактический список цитат без null.</summary>
    public IReadOnlyList<RagCitation> CitationsSafe => Citations ?? Array.Empty<RagCitation>();

    /// <summary>Фактический список нарушенных запрещённых терминов без null.</summary>
    public IReadOnlyList<string> FoundForbiddenTermsSafe => FoundForbiddenTerms ?? Array.Empty<string>();

    /// <summary>Фактический список ожидаемых запрещённых терминов без null.</summary>
    public IReadOnlyList<string> ForbiddenTermsSafe => ForbiddenTerms ?? Array.Empty<string>();

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
            if (Answer.Contains(Day24AbstentionPhrase, StringComparison.OrdinalIgnoreCase)) return true;
            return Answer.Contains(NoFactsAnswerPhrase, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Полный успех для данного режима (с учётом optional checks).</summary>
    public bool Passed
    {
        get
        {
            // Любая техническая ошибка — это провал, без вариантов.
            if (Error is not null) return false;

            // День 24: позитивный кейс требует SearchSucceeded,
            // отсутствия abstention, фактов, корректных источника/секции,
            // подтверждённой цитаты, поддержки ответа цитатами и
            // отсутствия запрещённых терминов.
            if (IsDay24PositiveCase)
            {
                return SearchSucceeded
                    && !Abstained
                    && FactCheckPassed
                    && SourceCheckPassed
                    && (ExpectedSectionContains is null || SectionCheckPassed)
                    && QuoteCheckPassed
                    && AnswerSupportedByQuotes
                    && ForbiddenTermsCheckPassed;
            }

            // День 24: негативный кейс — модель должна явно написать
            // «Не знаю» и попросить уточнить, при этом не делая
            // «неподдерживаемых фактических утверждений». Поиск мог
            // вернуть низкокачественные чанки (Sources.Count может быть
            // > 0), главное — что модель всё равно abstentionила.
            if (IsDay24NegativeCase)
            {
                return SearchSucceeded
                    && Abstained
                    && IsAbstentionAnswer
                    && !string.IsNullOrWhiteSpace(ClarificationQuestion)
                    && ForbiddenTermsCheckPassed;
            }

            // Legacy / режим «сравнения»: сохраняем старую семантику.
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

            // Legacy позитивный кейс: проверки терминов, источника,
            // секции (если задана) и запрещённых терминов.
            // QuoteCheckPassed/AnswerSupportedByQuotes НЕ требуются
            // — это ужесточение только для Дня 24.
            return FactCheckPassed
                && SourceCheckPassed
                && (ExpectedSectionContains is null || SectionCheckPassed)
                && ForbiddenTermsCheckPassed;
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