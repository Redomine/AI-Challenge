using System.Net;
using System.Net.Http;
using System.Text.Json;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Web.Services;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Tests;

/// <summary>
/// Фокусные тесты на dedicated 10-question Day 24 evaluation set:
/// проверяем парсинг, STLB-OK1 → revit-703 only, neighbour-колонки,
/// no-evidence/ambiguity, и запрет revit-702/revit-704 в ответах
/// про STLB-OK1 (даже если в ответе присутствует revit-703).
/// </summary>
public sealed class Day24QuestionsTests : IDisposable
{
    private readonly string _tempDir;

    public Day24QuestionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "day24-questions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Дедуплицированный список вопросов: 10 кейсов, по одному на каждый
    /// ожидаемый сценарий (STLB-OK1, neighbour 702/704, no-evidence,
    /// ambiguity, KB-positive). Совпадает по контракшенам с
    /// wwwroot/app-data/questions-day24.json. Серверные HTML-таблицы
    /// (STLB-OK1 и соседние колонки 702/704) используют источник
    /// <c>confluence_html</c> — это значение, с которым реальный индекс
    /// эмитит чанки из выгрузки Confluence HTML (см.
    /// <c>docindexing.corpus._confluence_html_units</c>). Негативные
    /// no-evidence кейсы (<c>expected_no_evidence=true</c>) намеренно
    /// оставляют <c>expected_source</c> пустым: abstention-ответ не
    /// производит матча по метаданным, и фейковый источник только шумит.
    /// </summary>
    private const string DedicatedJson = """
        {
          "version": "day24-v2",
          "description": "Dedicated Day 24 evaluation set",
          "questions": [
            {
              "question": "На каком сервере находится проект STLB-OK1?",
              "expected_source": "confluence_html",
              "expected_section_contains": "revit-703",
              "expected_fact_terms": ["revit-703", "STLB-OK1"],
              "forbidden_terms": ["revit-702", "revit-704"],
              "expected_kind": "stlb"
            },
            {
              "question": "Где именно в таблице Confluence указан STLB-OK1, на каком сервере?",
              "expected_source": "confluence_html",
              "expected_section_contains": "revit-703",
              "expected_fact_terms": ["revit-703"],
              "forbidden_terms": ["revit-702", "revit-704"],
              "expected_kind": "stlb"
            },
            {
              "question": "На каком сервере находится проект PRKS-SD2?",
              "expected_source": "confluence_html",
              "expected_section_contains": "revit-702",
              "expected_fact_terms": ["revit-702", "PRKS-SD2"],
              "forbidden_terms": ["STLB-OK1"],
              "expected_kind": "neighbour_column_702"
            },
            {
              "question": "На каком сервере находится проект PRKS-PL1?",
              "expected_source": "confluence_html",
              "expected_section_contains": "revit-704",
              "expected_fact_terms": ["revit-704", "PRKS-PL1"],
              "forbidden_terms": ["STLB-OK1"],
              "expected_kind": "neighbour_column_704"
            },
            {
              "question": "Какая версия Revit указана в строке Projects 2022 таблицы серверов?",
              "expected_section_contains": "",
              "expected_fact_terms": [],
              "expected_no_evidence": true,
              "forbidden_terms": ["2022", "Revit 2022", "версия 2022"],
              "expected_kind": "no_version_inference"
            },
            {
              "question": "На каком сервере в таблице Confluence указан проект с шифром ГГЖ-ПР1?",
              "expected_section_contains": "",
              "expected_fact_terms": [],
              "expected_no_evidence": true,
              "forbidden_terms": ["revit-702", "revit-703", "revit-704"],
              "expected_kind": "unknown_project"
            },
            {
              "question": "Какой проект из таблицы Projects 2022 связан с revit-703 и revit-704 одновременно?",
              "expected_section_contains": "",
              "expected_fact_terms": [],
              "expected_no_evidence": true,
              "forbidden_terms": ["STLB-OK1"],
              "expected_kind": "ambiguity_project_on_two_servers"
            },
            {
              "question": "Что делать при ошибке «Не удалось построить ленту» во время расчёта контура?",
              "expected_source": "confluence",
              "expected_section_contains": "Решение",
              "expected_fact_terms": ["контур"],
              "expected_kind": "kb_positive"
            },
            {
              "question": "Куда обращаться, если программа Autodesk Revit не работает?",
              "expected_source": "confluence",
              "expected_section_contains": "Программа Autodesk Revit не работает",
              "expected_fact_terms": ["IT", "BIM-координатор"],
              "expected_kind": "kb_positive"
            },
            {
              "question": "Нужен ли отдельный файл RSN.ini для каждой версии Revit?",
              "expected_source": "pdf",
              "expected_pdf_page": 3,
              "expected_section_contains": "RSN.ini",
              "expected_fact_terms": ["RSN.ini", "версии"],
              "expected_kind": "kb_positive_pdf"
            }
          ]
        }
        """;

    [Fact]
    public async Task LoadCases_ParsesExpectedKindAndForbiddenTerms()
    {
        var jsonPath = Path.Combine(_tempDir, "questions-day24.json");
        await File.WriteAllTextAsync(jsonPath, DedicatedJson);

        var cases = await AutoTestRunner.LoadCasesFromPathAsync(jsonPath, CancellationToken.None);

        Assert.Equal(10, cases.Count);
        // Должно быть ровно 2 STLB-вопроса и 7 разных категорий.
        var stlbCount = cases.Count(c => c.ExpectedKind == "stlb");
        Assert.Equal(2, stlbCount);

        // Forbidden terms прокидываются в AutoTestCase.
        var stlbFirst = cases.First(c => c.ExpectedKind == "stlb");
        Assert.Contains("revit-702", stlbFirst.ForbiddenTerms!);
        Assert.Contains("revit-704", stlbFirst.ForbiddenTerms!);
        Assert.DoesNotContain("revit-703", stlbFirst.ForbiddenTerms!);

        var noVersion = cases.First(c => c.ExpectedKind == "no_version_inference");
        Assert.True(noVersion.ExpectedNoEvidence);
        Assert.Contains("2022", noVersion.ForbiddenTerms!);
        Assert.Contains("Revit 2022", noVersion.ForbiddenTerms!);

        // Ambiguity-кейс: ожидаем, что модель воздержится от выдуманной
        // догадки и не назовёт STLB-OK1, даже если бы LLM знал индекс.
        var ambiguity = cases.First(c => c.ExpectedKind == "ambiguity_project_on_two_servers");
        Assert.True(ambiguity.ExpectedNoEvidence);
        Assert.Contains("STLB-OK1", ambiguity.ForbiddenTerms!);
    }

    [Fact]
    public async Task LoadCases_FromBundledAsset_LoadsTenCases()
    {
        // Проверяем, что packaged JSON в wwwroot/app-data содержит
        // ровно 10 вопросов и поле version=day24-*.
        var assetPath = LocateDay24AssetPath();
        Assert.True(File.Exists(assetPath),
            $"Dedicated Day 24 question JSON должен быть в wwwroot/app-data, отсутствует в {assetPath}.");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(assetPath));
        Assert.True(doc.RootElement.TryGetProperty("version", out var version));
        Assert.StartsWith("day24", version.GetString(), StringComparison.OrdinalIgnoreCase);

        var cases = await AutoTestRunner.LoadCasesFromPathAsync(assetPath, CancellationToken.None);
        Assert.Equal(10, cases.Count);
        // Оба STLB-вопроса требуют revit-703 и запрещают соседние серверы.
        var stlb = cases.Where(c => c.ExpectedKind == "stlb").ToList();
        Assert.NotEmpty(stlb);
        Assert.All(stlb, c =>
        {
            Assert.Contains("STLB-OK1", c.Question);
            Assert.Contains("revit-703", c.ExpectedFactTerms!);
            Assert.Contains("revit-702", c.ForbiddenTerms!);
            Assert.Contains("revit-704", c.ForbiddenTerms!);
        });
    }

    [Fact]
    public void IsDay24Set_DetectsBundledAsset()
    {
        var assetPath = LocateDay24AssetPath();
        Assert.True(File.Exists(assetPath));
        var noRag = new NoRagAnswerService(new ThrowingLlmClient(), TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(
            new RagOptions(assetPath, "http://x", "m", 2, 0, TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(1)),
            MakeThrowingEmbeddings(),
            new ThrowingLlmClient());
        var runner = new AutoTestRunner(noRag, rag, assetPath);
        Assert.True(runner.IsDay24Set());
    }

    [Fact]
    public void IsDay24Set_DetectsByFileName()
    {
        // Даже если в JSON нет version, имя файла с 'day24' делает
        // набор распознаваемым.
        var path = Path.Combine(_tempDir, "questions-day24.json");
        File.WriteAllText(path, """
            {
              "questions": [
                { "question": "Q", "expected_fact_terms": [] }
              ]
            }
            """);
        var noRag = new NoRagAnswerService(new ThrowingLlmClient(), TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(
            new RagOptions(path, "http://x", "m", 2, 0, TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(1)),
            MakeThrowingEmbeddings(),
            new ThrowingLlmClient());
        var runner = new AutoTestRunner(noRag, rag, path);
        Assert.True(runner.IsDay24Set());
    }

    [Fact]
    public void IsDay24Set_FallsBackToFalse_ForLegacyFile()
    {
        // Без поля version и без 'day24' в имени — false.
        var path = Path.Combine(_tempDir, "questions.json");
        File.WriteAllText(path, """
            {
              "questions": [
                { "question": "Q", "expected_fact_terms": [] }
              ]
            }
            """);
        var noRag = new NoRagAnswerService(new ThrowingLlmClient(), TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(
            new RagOptions(path, "http://x", "m", 2, 0, TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(1)),
            MakeThrowingEmbeddings(),
            new ThrowingLlmClient());
        var runner = new AutoTestRunner(noRag, rag, path);
        Assert.False(runner.IsDay24Set());
    }

    [Fact]
    public void Runner_FallsBackToLegacyPath_WhenPrimaryMissing()
    {
        var fallback = Path.Combine(_tempDir, "fallback.json");
        File.WriteAllText(fallback, """
            {
              "questions": [
                { "question": "Q", "expected_fact_terms": [] }
              ]
            }
            """);
        var noRag = new NoRagAnswerService(new ThrowingLlmClient(), TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(
            new RagOptions(fallback, "http://x", "m", 2, 0, TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(1)),
            MakeThrowingEmbeddings(),
            new ThrowingLlmClient());
        var primary = Path.Combine(_tempDir, "missing-primary.json");
        var runner = new AutoTestRunner(noRag, rag, primary, fallback);
        Assert.Equal(fallback, runner.ResolveExistingPath());
    }

    [Fact]
    public async Task AutoTestRunner_Day24_StlbOk1_Rejects_WhenAnswerMentionsRevit702_OrRevit704()
    {
        // Day 24 acceptance: позитивный STLB-вопрос требует revit-703
        // и ЗАПРЕЩАЕТ revit-702/revit-704 в ответе, даже если revit-703
        // присутствует. Без зависимостей от LLM/embedding.
        var noRag = new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(
            new RagOptions(Path.Combine(_tempDir, "x.sqlite3"), "http://x", "m", 2, 0,
                TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(1)),
            MakeThrowingEmbeddings(),
            new ThrowingLlmClient());
        var jsonPath = Path.Combine(_tempDir, "questions-day24.json");
        await File.WriteAllTextAsync(jsonPath, DedicatedJson);
        var runner = new AutoTestRunner(noRag, rag, jsonPath);

        var cases = await runner.LoadCasesAsync(CancellationToken.None);
        var stlb = cases.First(c => c.ExpectedKind == "stlb");

        // Сценарий A: модель назвала revit-703 и revit-702 → провал.
        var badAnswer = "STLB-OK1 находится на revit-703, но упоминается также на revit-702.";
        var badRun = new AutoTestRunResult(
            Index: 1, Mode: "rag", Question: stlb.Question,
            ExpectedSource: stlb.ExpectedSource, ExpectedPdfPage: stlb.ExpectedPdfPage,
            ExpectedSectionContains: stlb.ExpectedSectionContains,
            ExpectedNoEvidence: stlb.ExpectedNoEvidence,
            SearchSucceeded: true, RetrievedCount: 1,
            Sources: new[] { new RagSource("revit-703", "confluence", "T", "revit-703", 2, "Projects 2022: STLB-OK1", 0.9f) },
            Answer: badAnswer,
            ExpectedFactTerms: stlb.ExpectedFactTerms!,
            FoundFactTerms: new[] { "revit-703" },
            FactCheckPassed: true, SourceCheckPassed: true, SectionCheckPassed: true,
            Error: null, Trace: null, Citations: null,
            QuoteCheckPassed: true, AnswerSupportedByQuotes: true,
            Abstained: false, AbstentionReason: RagAbstentionReason.None,
            ExpectedKind: stlb.ExpectedKind,
            ForbiddenTerms: stlb.ForbiddenTerms,
            FoundForbiddenTerms: new[] { "revit-702" },
            ForbiddenTermsCheckPassed: false);
        Assert.False(badRun.Passed,
            "STLB-OK1 ответ с revit-702/revit-704 не должен проходить.");

        // Сценарий B: модель назвала revit-703 и revit-704 → провал.
        var badAnswer2 = "STLB-OK1 находится на revit-703 и revit-704.";
        var badRun2 = badRun with
        {
            Answer = badAnswer2,
            FoundForbiddenTerms = new[] { "revit-704" },
        };
        Assert.False(badRun2.Passed,
            "STLB-OK1 ответ с revit-704 не должен проходить.");

        // Сценарий C: модель назвала только revit-703 → проходит.
        var goodAnswer = "STLB-OK1 находится на revit-703.";
        var goodRun = badRun with
        {
            Answer = goodAnswer,
            FoundForbiddenTerms = Array.Empty<string>(),
            ForbiddenTermsCheckPassed = true,
        };
        Assert.True(goodRun.Passed,
            "STLB-OK1 ответ только с revit-703 должен проходить.");
    }

    [Fact]
    public async Task AutoTestRunner_Day24_NoEvidence_AbstainsWithoutForbiddenTerms()
    {
        // Negative case для «неизвестного проекта»: если модель
        // abstentionит «Не знаю», запрещённые токены автоматически
        // считаются соблюдёнными (ответ не содержит никаких фактов).
        var noRag = new NoRagAnswerService(new StubLlmClient("Не знаю."), TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(
            new RagOptions(Path.Combine(_tempDir, "x.sqlite3"), "http://x", "m", 2, 0,
                TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(1)),
            MakeThrowingEmbeddings(),
            new ThrowingLlmClient());
        var jsonPath = Path.Combine(_tempDir, "questions-day24.json");
        await File.WriteAllTextAsync(jsonPath, DedicatedJson);
        var runner = new AutoTestRunner(noRag, rag, jsonPath);

        var cases = await runner.LoadCasesAsync(CancellationToken.None);
        var unknown = cases.First(c => c.ExpectedKind == "unknown_project");

        // Прогон no-rag через прямой сервис.
        var noRagResult = await noRag.AskAsync(unknown.Question, CancellationToken.None);
        Assert.True(noRagResult.Abstained);

        // Симулируем AutoTestRunResult для проверки прохождения по
        // negative-контракту и forbidden-terms.
        var run = new AutoTestRunResult(
            Index: 1, Mode: "no-rag", Question: unknown.Question,
            ExpectedSource: unknown.ExpectedSource, ExpectedPdfPage: unknown.ExpectedPdfPage,
            ExpectedSectionContains: unknown.ExpectedSectionContains,
            ExpectedNoEvidence: unknown.ExpectedNoEvidence,
            SearchSucceeded: false, RetrievedCount: 0,
            Sources: Array.Empty<RagSource>(),
            Answer: noRagResult.Answer,
            ExpectedFactTerms: unknown.ExpectedFactTerms ?? Array.Empty<string>(),
            FoundFactTerms: Array.Empty<string>(),
            FactCheckPassed: true, SourceCheckPassed: true, SectionCheckPassed: true,
            Error: null, Trace: null, Citations: null,
            QuoteCheckPassed: false, AnswerSupportedByQuotes: noRagResult.Abstained,
            Abstained: noRagResult.Abstained,
            AbstentionReason: noRagResult.AbstentionReason,
            ExpectedKind: unknown.ExpectedKind,
            ForbiddenTerms: unknown.ForbiddenTerms,
            FoundForbiddenTerms: Array.Empty<string>(),
            ForbiddenTermsCheckPassed: true);
        Assert.True(run.Passed, "Негативный no-rag кейс с abstention должен проходить.");
    }

    [Fact]
    public async Task AutoTestRunner_Day24_FabricatedProjectName_FailsNoEvidence()
    {
        // Negative case: модель НЕ abstentionит и выдумывает STLB-OK1
        // для проекта ГГЖ-ПР1 → проваливается, т.к. STLB-OK1 входит в
        // forbidden_terms и ExpectedNoEvidence=true. Для негативных
        // кейсов expected_source намеренно пуст — abstention-ответ не
        // сопоставляется с источником, и лишнее значение только шумит.
        var testCase = new AutoTestCase(
            Question: "Где проект ГГЖ-ПР1?",
            ExpectedSource: "",
            ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedFactTerms: Array.Empty<string>(),
            ExpectedNoEvidence: true,
            ExpectedKind: "unknown_project",
            ForbiddenTerms: new[] { "revit-702", "revit-703", "revit-704" });

        // Сценарий A: модель fabricates STLB-OK1 → провал.
        var run = new AutoTestRunResult(
            Index: 1, Mode: "rag", Question: testCase.Question,
            ExpectedSource: testCase.ExpectedSource, ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedNoEvidence: true,
            SearchSucceeded: true, RetrievedCount: 1,
            Sources: new[] { new RagSource("revit-703", "confluence", "T", "revit-703", 2, "STLB-OK1", 0.9f) },
            Answer: "Проект ГГЖ-ПР1 находится на revit-703 рядом с STLB-OK1.",
            ExpectedFactTerms: Array.Empty<string>(),
            FoundFactTerms: Array.Empty<string>(),
            FactCheckPassed: true, SourceCheckPassed: true, SectionCheckPassed: true,
            Error: null, Trace: null, Citations: null,
            QuoteCheckPassed: true, AnswerSupportedByQuotes: true,
            Abstained: false, AbstentionReason: RagAbstentionReason.None,
            ExpectedKind: testCase.ExpectedKind,
            ForbiddenTerms: testCase.ForbiddenTerms,
            FoundForbiddenTerms: new[] { "revit-703" },
            ForbiddenTermsCheckPassed: false);
        Assert.False(run.Passed,
            "Выдуманный ответ для негативного кейса с forbidden токенами должен проваливаться.");
    }

    [Fact]
    public async Task AutoTestRunner_Day24_NoVersionInference_AbstainsWithoutRevit2022()
    {
        // Негативный кейс «Какая версия Revit в строке Projects 2022?»:
        // модель НЕ должна выводить «Revit 2022», «версия 2022» и т.п.
        // Если модель воздерживается — кейс проходит.
        var noRag = new NoRagAnswerService(new StubLlmClient("Не знаю."), TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(
            new RagOptions(Path.Combine(_tempDir, "x.sqlite3"), "http://x", "m", 2, 0,
                TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(1)),
            MakeThrowingEmbeddings(),
            new ThrowingLlmClient());
        var jsonPath = Path.Combine(_tempDir, "questions-day24.json");
        await File.WriteAllTextAsync(jsonPath, DedicatedJson);
        var runner = new AutoTestRunner(noRag, rag, jsonPath);

        var cases = await runner.LoadCasesAsync(CancellationToken.None);
        var noVersion = cases.First(c => c.ExpectedKind == "no_version_inference");

        // Сценарий A: модель abstentionит → проходит.
        var abstainRun = new AutoTestRunResult(
            Index: 1, Mode: "no-rag", Question: noVersion.Question,
            ExpectedSource: noVersion.ExpectedSource, ExpectedPdfPage: null,
            ExpectedSectionContains: null,
            ExpectedNoEvidence: noVersion.ExpectedNoEvidence,
            SearchSucceeded: false, RetrievedCount: 0,
            Sources: Array.Empty<RagSource>(),
            Answer: "Не знаю. Уточните, пожалуйста, вопрос.",
            ExpectedFactTerms: noVersion.ExpectedFactTerms ?? Array.Empty<string>(),
            FoundFactTerms: Array.Empty<string>(),
            FactCheckPassed: true, SourceCheckPassed: true, SectionCheckPassed: true,
            Error: null, Trace: null, Citations: null,
            QuoteCheckPassed: false, AnswerSupportedByQuotes: true,
            Abstained: true, AbstentionReason: RagAbstentionReason.NoEvidence,
            ExpectedKind: noVersion.ExpectedKind,
            ForbiddenTerms: noVersion.ForbiddenTerms,
            FoundForbiddenTerms: Array.Empty<string>(),
            ForbiddenTermsCheckPassed: true);
        Assert.True(abstainRun.Passed);

        // Сценарий B: модель утверждает «Revit 2022» → проваливается.
        var wrongRun = abstainRun with
        {
            Mode = "rag",
            SearchSucceeded = true,
            RetrievedCount = 1,
            Sources = new[] { new RagSource("revit-703", "confluence", "T", "revit-703", 2, "Projects 2022: STLB-OK1", 0.9f) },
            Answer = "Согласно таблице, в строке Projects 2022 указана версия Revit 2022.",
            Abstained = false,
            AbstentionReason = RagAbstentionReason.None,
            FoundForbiddenTerms = new[] { "Revit 2022" },
            ForbiddenTermsCheckPassed = false,
        };
        Assert.False(wrongRun.Passed,
            "Ответ с «Revit 2022» для no-version-inference должен проваливаться.");
    }

    [Fact]
    public async Task AutoTestRunner_Day24_RunAggregate_WithDedicatedSet_PopulatesForbiddenTerms()
    {
        // Прогоняем dedicated набор через реальный сервис с stub LLM,
        // проверяем что AutoTestRunResult содержит ExpectedKind/Forbidden/FoundForbidden.
        var indexPath = Path.Combine(_tempDir, "d24.sqlite3");
        const int vectorDim = 4;
        // Серверные чанки идут с source='confluence_html' — это
        // фактическое значение из реального корпуса (см. эмиттер
        // docindexing.corpus._confluence_html_units). Тест должен
        // матчить метаданные так же, как это делает prod-индекс,
        // чтобы SourceCheckPassed реально проверялся.
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("revit-702", "confluence_html", "Серверы", "revit-702", "Проекты других годов", 1, MakeVector(vectorDim, 0.1f, 1f)),
            ("revit-703", "confluence_html", "Серверы", "revit-703", "Projects 2022: STLB-OK1", 2, MakeVector(vectorDim, 1f, 0f)),
            ("revit-704", "confluence_html", "Серверы", "revit-704", "Проекты других годов", 3, MakeVector(vectorDim, 0.5f, 0.5f)),
        });

        var jsonPath = Path.Combine(_tempDir, "questions-day24.json");
        // Берём только первые 4 вопроса из dedicated набора для краткости.
        await File.WriteAllTextAsync(jsonPath, """
            {
              "version": "day24-v2",
              "questions": [
                {
                  "question": "На каком сервере находится проект STLB-OK1?",
                  "expected_source": "confluence_html",
                  "expected_section_contains": "revit-703",
                  "expected_fact_terms": ["revit-703", "STLB-OK1"],
                  "forbidden_terms": ["revit-702", "revit-704"],
                  "expected_kind": "stlb"
                },
                {
                  "question": "Какой проект из группы Projects 2022 расположен на revit-702?",
                  "expected_source": "confluence_html",
                  "expected_section_contains": "revit-702",
                  "expected_fact_terms": ["revit-702"],
                  "forbidden_terms": ["STLB-OK1"],
                  "expected_kind": "neighbour_column_702"
                },
                {
                  "question": "Какая версия Revit указана в строке Projects 2022 таблицы серверов?",
                  "expected_section_contains": "",
                  "expected_fact_terms": [],
                  "expected_no_evidence": true,
                  "forbidden_terms": ["2022"],
                  "expected_kind": "no_version_inference"
                }
              ]
            }
            """);

        var embeddings = new OllamaEmbeddingsClient(
            MakeEmbeddingHttpForQuery(MakeVector(vectorDim, 1f, 0f)),
            TimeSpan.FromSeconds(2), 0);
        // Stub LLM отвечает «Не знаю» на все вызовы → negative case проходит,
        // positive cases проваливаются с UnsupportedAnswer.
        var llm = new RecordingLlmClient(new[] { "Не знаю" });
        var noRag = new NoRagAnswerService(new StubLlmClient("Не знаю"), TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(
            new RagOptions(indexPath, "http://ollama.local", "qwen3-embedding:0.6b", 2, 4000,
                TimeSpan.FromSeconds(2), 0, TimeSpan.FromSeconds(5))
            with { PreFilterK = 4, PostFilterK = 2, ScoreThreshold = -1f },
            embeddings,
            llm);
        var runner = new AutoTestRunner(noRag, rag, jsonPath);

        var cases = await runner.LoadCasesAsync(CancellationToken.None);
        Assert.Equal(3, cases.Count);

        var report = await runner.RunAggregateAsync(
            cases,
            baseline: new RagRunSettings(false, 4, -1f, 2, TimeSpan.FromSeconds(1)),
            enhanced: null,
            includeNoRag: false,
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(3, report.Runs.Count);
        // Все RAG-результаты должны содержать ExpectedKind и ForbiddenTerms
        // — это доказательство, что dedicated-набор прокидывает поля.
        Assert.All(report.Runs, r =>
        {
            Assert.NotNull(r.ExpectedKind);
            Assert.NotEmpty(r.ForbiddenTermsSafe);
            // ForbiddenTermsCheckPassed может быть false, если в ответе есть
            // запрещённый токен (для случая «Не знаю» оно true).
            Assert.True(r.ForbiddenTermsCheckPassed || r.FoundForbiddenTermsSafe.Count > 0,
                $"Run #{r.Index} с expected_kind={r.ExpectedKind} должен либо не нарушать forbidden, либо указать нарушителей.");
        });

        // Конкретная проверка: STLB-вопрос на «Не знаю» abstentionит,
        // и forbidden_termsCheckPassed = true (abstention автоматически
        // считается соблюдением списка).
        var stlbRun = report.Runs.First(r => r.ExpectedKind == "stlb");
        Assert.True(stlbRun.Abstained);
        Assert.True(stlbRun.ForbiddenTermsCheckPassed);
        Assert.Empty(stlbRun.FoundForbiddenTermsSafe);
    }

    [Fact]
    public void AutoTestRunResult_ForbiddenFields_HaveDefaults_ForBackwardCompat()
    {
        // Старые позиционные вызовы с 16 аргументами должны собираться
        // и иметь пустые ForbiddenTerms списки.
        var r = new AutoTestRunResult(
            Index: 1, Mode: "rag", Question: "q",
            ExpectedSource: "", ExpectedPdfPage: null, ExpectedSectionContains: null,
            ExpectedNoEvidence: false,
            SearchSucceeded: true, RetrievedCount: 1,
            Sources: Array.Empty<RagSource>(),
            Answer: "a",
            ExpectedFactTerms: new[] { "x" },
            FoundFactTerms: new[] { "x" },
            FactCheckPassed: true, SourceCheckPassed: true, SectionCheckPassed: true,
            Error: null);
        Assert.Null(r.ExpectedKind);
        Assert.Empty(r.ForbiddenTermsSafe);
        Assert.Empty(r.FoundForbiddenTermsSafe);
        Assert.True(r.ForbiddenTermsCheckPassed);
    }

    [Fact]
    public void AutoTestCase_DefaultForbiddenFields_AreBackwardCompat()
    {
        // Старые позиционные вызовы AutoTestCase с 6 аргументами должны
        // иметь null ExpectedKind/ForbiddenTerms.
        var c = new AutoTestCase(
            Question: "q",
            ExpectedSource: "pdf",
            ExpectedPdfPage: 1,
            ExpectedSectionContains: "S",
            ExpectedFactTerms: new[] { "x" });
        Assert.Null(c.ExpectedKind);
        Assert.Null(c.ForbiddenTerms);
    }

    // --- helpers ---

    private static string LocateDay24AssetPath()
    {
        // ASP.NET Core Web SDK не копирует wwwroot в bin напрямую: вместо
        // этого генерируется staticwebassets.runtime.json с указанием
        // исходного каталога wwwroot. Тесты, которые читают bundled
        // JSON, должны смотреть в источник.
        // Стратегия 1: используем манифест staticwebassets.runtime.json.
        var baseDir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var manifestPath = Path.Combine(baseDir,
                "DesignerAssistant.Web.staticwebassets.runtime.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    if (doc.RootElement.TryGetProperty("ContentRoots", out var roots) &&
                        roots.ValueKind == JsonValueKind.Array && roots.GetArrayLength() > 0)
                    {
                        var root = roots[0].GetString();
                        if (!string.IsNullOrEmpty(root))
                        {
                            var candidate = Path.Combine(root, "app-data", "questions-day24.json");
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
                catch
                {
                    // fall through to next strategy
                }
            }
            var parent = Directory.GetParent(baseDir);
            if (parent is null) break;
            baseDir = parent.FullName;
        }
        // Стратегия 2: ищем DesignerAssistant.Web.csproj вверх по дереву.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var csproj = Directory.EnumerateFiles(dir, "DesignerAssistant.Web.csproj").FirstOrDefault();
            if (csproj is not null)
            {
                return Path.Combine(dir, "wwwroot", "app-data", "questions-day24.json");
            }
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        // Возвращаем относительный путь для осмысленного сообщения об ошибке.
        return Path.Combine(AppContext.BaseDirectory, "wwwroot", "app-data", "questions-day24.json");
    }

    private static HttpClient MakeEmbeddingHttpForQuery(float[] expectedVector)
    {
        return new HttpClient(new StubHttpHandler(_ => HttpResponseFacts.Json(
            HttpStatusCode.OK,
            new { embeddings = new[] { expectedVector } })))
        {
            BaseAddress = new Uri("http://ollama.local"),
        };
    }

    private static OllamaEmbeddingsClient MakeThrowingEmbeddings()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("embeddings should not be called"));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") };
        return new OllamaEmbeddingsClient(http, TimeSpan.FromSeconds(1), 0);
    }

    private static float[] MakeVector(int dim, float primary, float secondary)
    {
        var v = new float[dim];
        for (var i = 0; i < dim; i++) v[i] = i == 0 ? primary : secondary;
        return v;
    }

    private static void BuildSyntheticIndex(
        string path,
        string embedModel,
        int embedDim,
        IEnumerable<(string ChunkId, string Source, string Title, string Section, string Text, int? PdfPage, float[] Vector)> rows)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE chunks (
                chunk_id TEXT PRIMARY KEY,
                source TEXT NOT NULL,
                source_title TEXT NOT NULL,
                section TEXT NOT NULL,
                text TEXT NOT NULL,
                pdf_page INTEGER,
                embedding BLOB,
                embed_dim INTEGER NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v)";
            meta.Parameters.AddWithValue("$k", "embed_dim");
            meta.Parameters.AddWithValue("$v", embedDim.ToString());
            meta.ExecuteNonQuery();
        }
        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v)";
            meta.Parameters.AddWithValue("$k", "embed_model");
            meta.Parameters.AddWithValue("$v", embedModel);
            meta.ExecuteNonQuery();
        }
        foreach (var row in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO chunks(chunk_id, source, source_title, section, text, pdf_page, embedding, embed_dim)
                VALUES ($id, $src, $title, $section, $text, $page, $vec, $dim)
                """;
            insert.Parameters.AddWithValue("$id", row.ChunkId);
            insert.Parameters.AddWithValue("$src", row.Source);
            insert.Parameters.AddWithValue("$title", row.Title);
            insert.Parameters.AddWithValue("$section", row.Section);
            insert.Parameters.AddWithValue("$text", row.Text);
            insert.Parameters.AddWithValue("$page", (object?)row.PdfPage ?? DBNull.Value);
            insert.Parameters.AddWithValue("$vec", VectorToBytes(row.Vector));
            insert.Parameters.AddWithValue("$dim", embedDim);
            insert.ExecuteNonQuery();
        }
    }

    private static byte[] VectorToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * 4];
        for (var i = 0; i < vector.Length; i++)
        {
            var bits = BitConverter.SingleToInt32Bits(vector[i]);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), bits);
        }
        return bytes;
    }

    private sealed class StubLlmClient : ILlmClient
    {
        private readonly string _reply;
        public StubLlmClient(string reply) => _reply = reply;
        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult(new TokenCountResult(0, false));
        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => Task.FromResult(new LlmResponse(_reply, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, false)));
    }

    private sealed record LlmCall(string Instructions, string UserMessage);

    /// <summary>Возвращает заранее заданные ответы по очереди и запоминает все вызовы.</summary>
    private sealed class RecordingLlmClient : ILlmClient
    {
        private readonly Queue<string> _replies;
        public List<LlmCall> Calls { get; } = new();

        public RecordingLlmClient(IEnumerable<string> replies)
        {
            _replies = new Queue<string>(replies);
        }

        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult(new TokenCountResult(0, false));

        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
        {
            var user = messages.LastOrDefault()?.Content ?? "";
            Calls.Add(new LlmCall(instructions, user));
            var reply = _replies.Count > 0 ? _replies.Dequeue() : "";
            return Task.FromResult(new LlmResponse(reply, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, false)));
        }
    }

    private sealed class ThrowingLlmClient : ILlmClient
    {
        public Task<TokenCountResult> CountTextTokensAsync(IReadOnlyCollection<string> texts, CancellationToken cancellationToken = default)
            => Task.FromResult(new TokenCountResult(0, false));
        public Task<LlmResponse> GenerateAsync(string instructions, IReadOnlyCollection<ChatMessage> messages, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("LLM should not be called.");
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
