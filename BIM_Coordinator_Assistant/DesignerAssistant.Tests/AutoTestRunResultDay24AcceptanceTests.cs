using DesignerAssistant.Web.Services;

namespace DesignerAssistant.Tests;

/// <summary>
/// Фокусные тесты на acceptance-критерий <see cref="AutoTestRunResult.Passed"/>
/// для Дня 24: позитивные кейсы требуют подтверждённой цитаты и
/// поддержки ответа цитатами; негативные кейсы требуют явной
/// фразы «Не знаю» + уточняющий вопрос и допускают наличие
/// низкокачественных чанков в <c>Sources</c> (но не должны содержать
/// запрещённых фактических утверждений).
/// <para>Эти тесты не трогают ни <c>RagCitationParser</c>, ни
/// <c>RagServicesTests</c> (их редактирует другой воркер), а
/// собирают <see cref="AutoTestRunResult"/> напрямую через публичный
/// конструктор.</para>
/// </summary>
public sealed class AutoTestRunResultDay24AcceptanceTests : IDisposable
{
    private readonly string _tempDir;

    public AutoTestRunResultDay24AcceptanceTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "d24-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Базовый STLB-позитивный кейс (На каком сервере STLB-OK1?).
    /// Все базовые проверки true, цитата подтверждена, ответ
    /// поддержан цитатой, без запрещённых терминов.
    /// </summary>
    private static AutoTestRunResult BuildBaselineDay24Positive()
    {
        return new AutoTestRunResult(
            Index: 1,
            Mode: "rag",
            Question: "На каком сервере находится проект STLB-OK1?",
            ExpectedSource: "confluence",
            ExpectedPdfPage: 2,
            ExpectedSectionContains: "revit-703",
            ExpectedNoEvidence: false,
            SearchSucceeded: true,
            RetrievedCount: 1,
            Sources: new[]
            {
                new RagSource(
                    ChunkId: "revit-703",
                    Source: "confluence",
                    Title: "Серверы",
                    Section: "revit-703",
                    PdfPage: 2,
                    Text: "Projects 2022: STLB-OK1",
                    Score: 0.9f),
            },
            Answer: "STLB-OK1 находится на revit-703.",
            ExpectedFactTerms: new[] { "revit-703", "STLB-OK1" },
            FoundFactTerms: new[] { "revit-703", "STLB-OK1" },
            FactCheckPassed: true,
            SourceCheckPassed: true,
            SectionCheckPassed: true,
            Error: null,
            Trace: null,
            Citations: new[]
            {
                new RagCitation(
                    "revit-703", "confluence", "revit-703", 2,
                    "Projects 2022: STLB-OK1", true),
            },
            QuoteCheckPassed: true,
            AnswerSupportedByQuotes: true,
            Abstained: false,
            AbstentionReason: RagAbstentionReason.None,
            ExpectedKind: "stlb",
            ForbiddenTerms: new[] { "revit-702", "revit-704" },
            FoundForbiddenTerms: Array.Empty<string>(),
            ForbiddenTermsCheckPassed: true,
            ClarificationQuestion: null,
            IsDay24Evaluation: true);
    }

    [Fact]
    public void Day24_Positive_WithoutQuote_Fails()
    {
        // Позитивный кейс Дня 24 без подтверждённой цитаты
        // (QuoteCheckPassed=false) должен проваливаться, даже если
        // все остальные проверки true. Это и есть фикс: раньше
        // AutoTestRunResult.Passed игнорировал QuoteCheckPassed.
        var run = BuildBaselineDay24Positive() with
        {
            Citations = Array.Empty<RagCitation>(),
            QuoteCheckPassed = false,
        };
        Assert.False(run.Passed,
            "Day 24 позитивный кейс без подтверждённой цитаты должен проваливаться.");
        // Вспомогательно: AnswerSupportedByQuotes=true не спасает,
        // если самих цитат нет.
        Assert.True(run.AnswerSupportedByQuotes);
        Assert.True(run.FactCheckPassed);
        Assert.True(run.SourceCheckPassed);
        Assert.True(run.SectionCheckPassed);
        Assert.True(run.ForbiddenTermsCheckPassed);
        Assert.False(run.Abstained);
    }

    [Fact]
    public void Day24_Positive_QuoteExists_ButAnswerNotSupported_Fails()
    {
        // Позитивный кейс Дня 24: цитата формально есть
        // (QuoteCheckPassed=true), но ответ не поддержан этой
        // цитатой (AnswerSupportedByQuotes=false). Модель
        // «уверенно» назвала ответ, который не выводится из
        // фрагмента — это провал.
        var run = BuildBaselineDay24Positive() with
        {
            Answer = "STLB-OK1 находится на revit-703 (дополнительно: 24/7 поддержка).",
            AnswerSupportedByQuotes = false,
        };
        Assert.True(run.QuoteCheckPassed,
            "В этом сценарии сама цитата присутствует — но ответ ею не поддержан.");
        Assert.False(run.AnswerSupportedByQuotes);
        Assert.False(run.Passed,
            "Day 24 позитивный кейс с неподдержанным ответом должен проваливаться.");
    }

    [Fact]
    public void Day24_Negative_AbstainsWithRetrievedChunks_Passes()
    {
        // Негативный кейс Дня 24 (ExpectedNoEvidence=true): модель
        // вернула «Не знаю», есть clarification, и при этом поиск
        // вернул низкокачественные чанки (Sources.Count > 0).
        // Этот сценарий должен проходить, в отличие от старой
        // семантики, которая требовала Sources.Count == 0.
        var run = new AutoTestRunResult(
            Index: 1,
            Mode: "rag",
            Question: "Где проект ГГЖ-ПР1?",
            ExpectedSource: "confluence",
            ExpectedPdfPage: null,
            ExpectedSectionContains: "",
            ExpectedNoEvidence: true,
            SearchSucceeded: true,
            RetrievedCount: 2,
            // Поиск нашёл чанки, и они нерелевантны запросу —
            // тем не менее модель обязана отказаться от выдумки.
            Sources: new[]
            {
                new RagSource(
                    ChunkId: "revit-702",
                    Source: "confluence",
                    Title: "Серверы",
                    Section: "revit-702",
                    PdfPage: 1,
                    Text: "Проекты других годов",
                    Score: 0.05f),
                new RagSource(
                    ChunkId: "revit-704",
                    Source: "confluence",
                    Title: "Серверы",
                    Section: "revit-704",
                    PdfPage: 3,
                    Text: "Проекты других годов",
                    Score: 0.04f),
            },
            Answer: "Не знаю. Уточните, пожалуйста, вопрос — в индексе нет упоминания проекта ГГЖ-ПР1.",
            ExpectedFactTerms: Array.Empty<string>(),
            FoundFactTerms: Array.Empty<string>(),
            FactCheckPassed: true,
            SourceCheckPassed: true,
            SectionCheckPassed: true,
            Error: null,
            Trace: null,
            Citations: Array.Empty<RagCitation>(),
            QuoteCheckPassed: false,
            AnswerSupportedByQuotes: false,
            Abstained: true,
            AbstentionReason: RagAbstentionReason.NoEvidence,
            ExpectedKind: "unknown_project",
            ForbiddenTerms: new[] { "revit-702", "revit-703", "revit-704" },
            FoundForbiddenTerms: Array.Empty<string>(),
            ForbiddenTermsCheckPassed: true,
            ClarificationQuestion: "Уточните, пожалуйста, вопрос — в индексе нет упоминания проекта ГГЖ-ПР1.",
            IsDay24Evaluation: true);

        Assert.True(run.IsDay24NegativeCase);
        Assert.True(run.Sources.Count > 0,
            "Этот сценарий принципиально отличается от старого — поиск что-то нашёл.");
        Assert.True(run.Abstained);
        Assert.True(run.Passed,
            "Day 24 негативный кейс с retrieval+abstain+clarification должен проходить.");
    }

    [Fact]
    public void Day24_Negative_MissingClarification_Fails()
    {
        // Негативный кейс Дня 24 без уточняющего вопроса — провал.
        // Это требование явной абстенции: «Не знаю» должно
        // сопровождаться просьбой уточнить.
        var run = BuildBaselineDay24Positive() with
        {
            // Превращаем позитивный кейс в негативный, но без
            // clarification question.
            ExpectedNoEvidence = true,
            ExpectedFactTerms = Array.Empty<string>(),
            FoundFactTerms = Array.Empty<string>(),
            Sources = Array.Empty<RagSource>(),
            RetrievedCount = 0,
            Answer = "Не знаю.",
            Citations = Array.Empty<RagCitation>(),
            QuoteCheckPassed = false,
            AnswerSupportedByQuotes = false,
            Abstained = true,
            AbstentionReason = RagAbstentionReason.NoEvidence,
            ClarificationQuestion = null,
        };
        Assert.True(run.IsDay24NegativeCase);
        Assert.True(run.IsAbstentionAnswer,
            "Сам ответ содержит «Не знаю», но это не спасает без clarification.");
        Assert.False(run.Passed,
            "Day 24 негативный кейс без clarification должен проваливаться.");
    }

    [Fact]
    public void Day24_Negative_HasClarification_Passes()
    {
        // Контр-проверка к предыдущему тесту: с clarification
        // негативный кейс Дня 24 проходит.
        var run = new AutoTestRunResult(
            Index: 1,
            Mode: "rag",
            Question: "Где проект ГГЖ-ПР1?",
            ExpectedSource: "confluence",
            ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedNoEvidence: true,
            SearchSucceeded: true,
            RetrievedCount: 0,
            Sources: Array.Empty<RagSource>(),
            Answer: "Не знаю. Уточните, пожалуйста.",
            ExpectedFactTerms: Array.Empty<string>(),
            FoundFactTerms: Array.Empty<string>(),
            FactCheckPassed: true,
            SourceCheckPassed: true,
            SectionCheckPassed: true,
            Error: null,
            Trace: null,
            Citations: Array.Empty<RagCitation>(),
            QuoteCheckPassed: false,
            AnswerSupportedByQuotes: false,
            Abstained: true,
            AbstentionReason: RagAbstentionReason.NoEvidence,
            ExpectedKind: "unknown_project",
            ForbiddenTerms: new[] { "revit-702", "revit-703", "revit-704" },
            FoundForbiddenTerms: Array.Empty<string>(),
            ForbiddenTermsCheckPassed: true,
            ClarificationQuestion: "Уточните, пожалуйста.",
            IsDay24Evaluation: true);
        Assert.True(run.IsDay24NegativeCase);
        Assert.True(run.Passed,
            "Day 24 негативный кейс с abstention+clarification должен проходить.");
    }

    [Fact]
    public void Day24_Negative_ForbiddenTermsPresent_Fails()
    {
        // Негативный кейс Дня 24, где модель «отказалась», но
        // всё равно написала фактическое утверждение из
        // forbidden_terms (например, упомянула revit-702).
        // Это именно то, что защищает «не делать unsupported
        // factual assertion» — даже внутри «Не знаю» ответ не
        // должен противоречить abstention.
        var run = BuildBaselineDay24Positive() with
        {
            ExpectedNoEvidence = true,
            ExpectedFactTerms = Array.Empty<string>(),
            FoundFactTerms = new[] { "revit-703" },
            FactCheckPassed = false,
            Answer = "Не знаю. Возможно, проект на revit-703.",
            Citations = Array.Empty<RagCitation>(),
            QuoteCheckPassed = false,
            AnswerSupportedByQuotes = false,
            Abstained = true,
            AbstentionReason = RagAbstentionReason.NoEvidence,
            FoundForbiddenTerms = new[] { "revit-703" },
            ForbiddenTermsCheckPassed = false,
            ClarificationQuestion = "Уточните, пожалуйста.",
        };
        Assert.True(run.IsDay24NegativeCase);
        Assert.False(run.ForbiddenTermsCheckPassed,
            "Запрещённый токен должен фиксироваться как нарушение.");
        Assert.False(run.Passed,
            "Day 24 негативный кейс с forbidden terms в ответе должен проваливаться.");
    }

    [Fact]
    public void Day24_Error_AlwaysFails_EvenForPositiveCase()
    {
        // Ошибка — это явный провал. Проверяем, что Error != null
        // не «протаскивается» через Day 24 позитивный контракт.
        var run = BuildBaselineDay24Positive() with
        {
            Error = "Что-то пошло не так в RAG-сервисе.",
        };
        Assert.False(run.Passed,
            "Любая техническая ошибка должна проваливать acceptance, независимо от других проверок.");
    }

    [Fact]
    public void Legacy_Positive_WithoutQuote_StillPasses()
    {
        // Обратная совместимость: для НЕ-Day24 режима (например,
        // устаревший autotest на comparison-наборе) цитаты не
        // обязательны. Это и есть «legacy comparison cases should
        // preserve old semantics if needed».
        var run = new AutoTestRunResult(
            Index: 1,
            Mode: "rag",
            Question: "Любой вопрос",
            ExpectedSource: "pdf",
            ExpectedPdfPage: 1,
            ExpectedSectionContains: null,
            ExpectedNoEvidence: false,
            SearchSucceeded: true,
            RetrievedCount: 1,
            Sources: new[]
            {
                new RagSource(
                    ChunkId: "c1",
                    Source: "pdf",
                    Title: "Doc",
                    Section: "Section",
                    PdfPage: 1,
                    Text: "text",
                    Score: 0.9f),
            },
            Answer: "влажность 45%",
            ExpectedFactTerms: new[] { "влажность" },
            FoundFactTerms: new[] { "влажность" },
            FactCheckPassed: true,
            SourceCheckPassed: true,
            SectionCheckPassed: true,
            Error: null,
            Trace: null,
            Citations: Array.Empty<RagCitation>(),
            QuoteCheckPassed: false,
            AnswerSupportedByQuotes: false,
            Abstained: false,
            AbstentionReason: RagAbstentionReason.None,
            ExpectedKind: null,
            ForbiddenTerms: null,
            FoundForbiddenTerms: null,
            ForbiddenTermsCheckPassed: true,
            ClarificationQuestion: null,
            IsDay24Evaluation: false);
        Assert.False(run.IsDay24PositiveCase);
        Assert.False(run.IsDay24NegativeCase);
        Assert.True(run.Passed,
            "Legacy режим сравнения не должен ужесточать требования к цитатам.");
    }

    [Fact]
    public void Legacy_Negative_StillRequires_ZeroSources()
    {
        // Обратная совместимость: legacy-негативный RAG-кейс
        // по-прежнему требует Sources.Count == 0 для abstention,
        // потому что это и есть «old semantics if needed».
        var run = new AutoTestRunResult(
            Index: 1,
            Mode: "rag",
            Question: "Неизвестный вопрос",
            ExpectedSource: "",
            ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedNoEvidence: true,
            SearchSucceeded: true,
            RetrievedCount: 2,
            Sources: new[]
            {
                new RagSource(
                    ChunkId: "c1",
                    Source: "pdf",
                    Title: "Doc",
                    Section: "S",
                    PdfPage: 1,
                    Text: "noise",
                    Score: 0.05f),
                new RagSource(
                    ChunkId: "c2",
                    Source: "pdf",
                    Title: "Doc",
                    Section: "S",
                    PdfPage: 1,
                    Text: "noise2",
                    Score: 0.04f),
            },
            Answer: "Не знаю.",
            ExpectedFactTerms: Array.Empty<string>(),
            FoundFactTerms: Array.Empty<string>(),
            FactCheckPassed: true,
            SourceCheckPassed: true,
            SectionCheckPassed: true,
            Error: null,
            Trace: null,
            Citations: Array.Empty<RagCitation>(),
            QuoteCheckPassed: false,
            AnswerSupportedByQuotes: true,
            Abstained: true,
            AbstentionReason: RagAbstentionReason.NoEvidence,
            ExpectedKind: null,
            ForbiddenTerms: null,
            FoundForbiddenTerms: null,
            ForbiddenTermsCheckPassed: true,
            ClarificationQuestion: null,
            IsDay24Evaluation: false);
        Assert.False(run.IsDay24NegativeCase);
        Assert.True(run.IsNegativeCase);
        Assert.False(run.Passed,
            "Legacy негативный RAG-кейс требует Sources.Count == 0 для accepted abstention.");
    }

    [Fact]
    public void Day24_Negative_HelperFlags_ConsistentWithClass()
    {
        // Smoke-test: новые helper-свойства согласованы с
        // оригинальной IsNegativeCase и отражают флаг
        // IsDay24Evaluation.
        var positive = BuildBaselineDay24Positive();
        Assert.True(positive.IsDay24PositiveCase);
        Assert.False(positive.IsDay24NegativeCase);
        Assert.False(positive.IsNegativeCase);

        var negative = BuildBaselineDay24Positive() with
        {
            ExpectedNoEvidence = true,
            ExpectedFactTerms = Array.Empty<string>(),
            FoundFactTerms = Array.Empty<string>(),
            Sources = Array.Empty<RagSource>(),
            RetrievedCount = 0,
            Citations = Array.Empty<RagCitation>(),
            QuoteCheckPassed = false,
            AnswerSupportedByQuotes = false,
            Abstained = true,
            AbstentionReason = RagAbstentionReason.NoEvidence,
            Answer = "Не знаю. Уточните.",
            ClarificationQuestion = "Уточните.",
        };
        Assert.True(negative.IsDay24NegativeCase);
        Assert.False(negative.IsDay24PositiveCase);
        Assert.True(negative.IsNegativeCase);
    }
}