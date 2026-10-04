using System.Net;
using System.Text;
using System.Text.Json;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Web.Services;
using Microsoft.Data.Sqlite;

namespace DesignerAssistant.Tests;

/// <summary>
/// Тесты RagMiniChatService: per-conversation RAG-мини-чат с памятью
/// реплик и контекстным окном для поиска. Используются синтетические
/// индексы и stub-LLM; внешние ресурсы и Revit не задействованы.
/// </summary>
public sealed class RagMiniChatServiceTests : IDisposable
{
    private readonly string _tempDir;

    public RagMiniChatServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rag-mini-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------
    // Базовый сценарий 1: 12 реплик по нормам офиса.
    // Беседа хранит goal/clarifications/terms; каждая реплика
    // вызывает реальный RagQueryService.QueryAsync; цитаты обязательны;
    // при отсутствии фактов — явный abstention.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Scenario_OfficeNorms_12Turns_PreservesHistoryAndCitations()
    {
        var (service, llm) = await BuildOfficeNormsServiceAsync();

        const string conv = "office-norms";
        service.SetGoal(conv,
            "Собрать нормативные требования к офисному помещению для проекта STLB-OK1.");
        service.AddClarification(conv, "Офис в существующем здании, перепланировка не планируется.");
        service.AddTerms(conv, new[] { "офис", "микроклимат", "эргономика", "STLB-OK1" });

        var transcript = new List<(string Question, string KeywordForEmbedding)>
        {
            ("Какая норма влажности в офисе?", "влажность"),
            ("А температура?", "температур"),
            ("Допустимый уровень шума?", "шум"),
            ("Какая высота стола по нормам?", "стол"),
            ("Минимальная освещённость рабочего места?", "освещён"),
            ("Сколько площади на одного сотрудника?", "площад"),
            ("А для переговорной комнаты есть отдельные нормы?", "переговорн"),
            ("Требования к вентиляции?", "вентиляц"),
            ("Какая кратность воздухообмена?", "воздухообмен"),
            ("Что насчёт пожарной безопасности?", "пожар"),
            ("И последнее — требования к эвакуационным путям?", "эвакуац"),
            ("Спасибо, сформируй резюме требований.", "резюме"),
        };

        var seenUserQuestions = new List<string>();
        foreach (var (question, keyword) in transcript)
        {
            seenUserQuestions.Add(question);
            var turn = await service.AskAsync(conv, question, RagMode.Rag);

            Assert.Equal(question, turn.UserQuestion);
            Assert.NotNull(turn.Result.Trace);
            Assert.DoesNotContain("История беседы", turn.Result.Trace.SearchQuery);
            Assert.NotNull(turn.Result);
            // Сервис обязан вернуть цитаты или явный abstention.
            if (turn.Result.Abstained)
            {
                Assert.Equal(RagAbstentionMessages.Russian, turn.Result.Answer);
                Assert.NotNull(turn.Result.ClarificationQuestion);
            }
            else
            {
                // На каждую реплику с цитатами должна быть хотя бы одна
                // подтверждённая цитата и реальный источник.
                Assert.NotEmpty(turn.Result.CitationsSafe);
                Assert.NotEmpty(turn.Result.Sources);
                Assert.Contains(turn.Result.CitationsSafe, c => c.Verified);
                // Цитата должна ссылаться на чанк из индекса.
                Assert.All(turn.Result.CitationsSafe,
                    c => Assert.Contains(c.ChunkId,
                        new[] { "c-hum", "c-temp", "c-noise", "c-table", "c-light",
                                "c-area", "c-meet", "c-vent", "c-fire" }));
            }

            // История сохраняется целиком, не теряется.
            var snapshot = service.GetSession(conv);
            Assert.Equal(seenUserQuestions.Count, snapshot.TurnCount);
            Assert.Equal(seenUserQuestions, snapshot.Turns.Select(t => t.UserQuestion).ToArray());
            // goal/clarifications/terms тоже сохраняются.
            Assert.Contains("STLB-OK1", snapshot.State.Goal, StringComparison.OrdinalIgnoreCase);
            Assert.Single(snapshot.State.Clarifications);
            Assert.Equal(4, snapshot.State.Terms.Count);
        }

        Assert.Equal(12, llm.Calls.Count);
        // В контексте каждого вопроса фигурирует goal.
        foreach (var call in llm.Calls)
        {
            Assert.Contains("STLB-OK1", call.UserMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Цель беседы", call.UserMessage, StringComparison.OrdinalIgnoreCase);
        }
        // Финальный вопрос — последняя реплика в промпте.
        var lastCall = llm.Calls.Last();
        Assert.Contains("резюме требований", lastCall.UserMessage, StringComparison.OrdinalIgnoreCase);
        // Окно ограничено MaxTurns: более ранние реплики должны быть вытеснены.
        // history.Count на момент отправки 12-го вопроса = 11 (без текущего).
        // Берём последние MaxTurns реплик истории, т.е. transcript[MaxTurns-1..MaxTurns-1+MaxTurns-1].
        var maxTurns = RagMiniChatWindowOptions.Default.MaxTurns;
        var historyBeforeLast = transcript.Count - 1;
        var windowSize = Math.Min(historyBeforeLast, maxTurns);
        var firstInWindowIndex = historyBeforeLast - windowSize; // 11 - 6 = 5
        var firstOmittedIndex = firstInWindowIndex - 1; // 4
        var promptText = lastCall.UserMessage;
        Assert.Contains(transcript[firstInWindowIndex].Item1, promptText, StringComparison.OrdinalIgnoreCase);
        if (firstOmittedIndex >= 0)
        {
            Assert.DoesNotContain(transcript[firstOmittedIndex].Item1, promptText, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------
    // Базовый сценарий 2: 10 реплик по серверам Revit.
    // Проверяется работа с clarifications, подмешивание терминов,
    // abstention при отсутствии подтверждающих фактов и Reset.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Scenario_RevitServers_10Turns_AbstainsAndResets()
    {
        var (service, _) = await BuildRevitServersServiceAsync();

        const string conv = "revit-servers";
        service.SetGoal(conv, "Понять, на каком сервере лежат проекты серии OK1.");
        service.AddClarification(conv, "Меня интересуют только проекты 2022 года.");
        service.AddTerms(conv, new[] { "revit-703", "STLB-OK1", "проекты 2022" });

        // Реплики 1-5 — позитивные, должны возвращать цитаты.
        // Мини-чат-сервис не отвечает за выбор конкретного чанка — это
        // делает RagAnswerService по векторному поиску. Здесь достаточно
        // проверить, что цитаты есть, верифицированы и ссылаются на
        // реальные чанки из индекса.
        var positive = new[]
        {
            "На каком сервере STLB-OK1?",
            "А STLB-OK2?",
            "Где STLB-OK3?",
            "Покажи все проекты 2022 года.",
            "Есть ли ещё проекты OK1?",
        };
        var realChunks = new[] { "revit-702", "revit-703", "revit-704", "revit-705" };
        foreach (var q in positive)
        {
            var turn = await service.AskAsync(conv, q, RagMode.Rag);
            Assert.NotNull(turn.Result.Trace);
            Assert.Equal(q, turn.Result.Trace.SearchQuery);
            Assert.False(turn.Result.Abstained, $"Ожидался ответ с цитатами для: {q}");
            Assert.NotEmpty(turn.Result.CitationsSafe);
            Assert.Contains(turn.Result.CitationsSafe, c => c.Verified);
            Assert.All(turn.Result.CitationsSafe,
                c => Assert.Contains(c.ChunkId, realChunks));
        }

        // Реплика 6: уточнение попадает в контекст.
        var clarificationTurn = await service.AskAsync(
            conv,
            "Уточняю: меня интересуют только проекты со словом STLB.",
            RagMode.Rag);
        Assert.NotNull(clarificationTurn.Result);

        // Реплика 7: вопрос без подтверждающих фактов → abstention.
        var unknownTurn = await service.AskAsync(
            conv,
            "А что насчёт сервера revit-999?",
            RagMode.Rag);
        Assert.True(unknownTurn.Result.Abstained);
        Assert.Equal(RagAbstentionMessages.Russian, unknownTurn.Result.Answer);
        Assert.NotNull(unknownTurn.Result.ClarificationQuestion);

        // Реплики 8-10: после abstention продолжаем спрашивать.
        var postAbstainTurns = new[]
        {
            "Вернёмся к STLB-OK1 — подтверди сервер.",
            "А STLB-OK5?",
            "Спасибо, этого достаточно.",
        };
        foreach (var q in postAbstainTurns)
        {
            var turn = await service.AskAsync(conv, q, RagMode.Rag);
            Assert.NotNull(turn.Result);
        }

        var fullSnapshot = service.GetSession(conv);
        Assert.Equal(10, fullSnapshot.TurnCount);
        Assert.Contains("OK1", fullSnapshot.State.Goal, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, fullSnapshot.State.Clarifications.Count);
        Assert.Equal(3, fullSnapshot.State.Terms.Count);

        // Reset должен стереть историю и состояние, но не задеть другие беседы.
        const string otherConv = "other";
        service.SetGoal(otherConv, "other-goal");
        await service.AskAsync(otherConv, "любой вопрос", RagMode.Rag);
        Assert.Equal(1, service.GetSession(otherConv).TurnCount);

        service.Reset(conv);
        var afterReset = service.GetSession(conv);
        Assert.Equal(0, afterReset.TurnCount);
        Assert.Equal("", afterReset.State.Goal);
        Assert.Empty(afterReset.State.Clarifications);
        Assert.Empty(afterReset.State.Terms);
        // Другая беседа не пострадала.
        Assert.Equal(1, service.GetSession(otherConv).TurnCount);

        // ResetAll стирает всё.
        service.ResetAll();
        Assert.Empty(service.ListConversations());
    }

    // ------------------------------------------------------------------
    // Дополнительные проверки для отдельных аспектов.
    // ------------------------------------------------------------------

    [Fact]
    public void WindowOptions_RejectsNonPositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RagMiniChatWindowOptions(MaxTurns: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RagMiniChatWindowOptions(MaxGoalChars: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RagMiniChatWindowOptions(MaxClarificationsChars: 0).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RagMiniChatWindowOptions(MaxTermsChars: 0).Validate());
    }

    [Fact]
    public void AskAsync_RequiresConversationId()
    {
        var service = new RagMiniChatService(BuildEmptyFacade());
        Assert.ThrowsAsync<ArgumentException>(() => service.AskAsync("", "q"));
        Assert.ThrowsAsync<ArgumentException>(() => service.AskAsync("   ", "q"));
    }

    [Fact]
    public async Task AskAsync_PreservesFullHistory_EvenBeyondWindow()
    {
        var (service, _) = await BuildOfficeNormsServiceAsync(
            new RagMiniChatWindowOptions(MaxTurns: 2, MaxGoalChars: 200, MaxClarificationsChars: 500, MaxTermsChars: 200));

        const string conv = "c1";
        service.SetGoal(conv, "goal");
        for (var i = 0; i < 5; i++)
        {
            await service.AskAsync(conv, $"Вопрос {i}", RagMode.Rag);
        }
        // Все 5 реплик сохранены, несмотря на маленькое окно.
        Assert.Equal(5, service.GetSession(conv).TurnCount);
        for (var i = 0; i < 5; i++)
        {
            Assert.Contains($"Вопрос {i}",
                service.GetSession(conv).Turns.Select(t => t.UserQuestion),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task EmptyQuestion_ReturnsAbstention_AndPersistsTurn()
    {
        var (service, _) = await BuildOfficeNormsServiceAsync();
        const string conv = "c-empty";
        var turn = await service.AskAsync(conv, "", RagMode.Rag);
        Assert.True(turn.Result.Abstained);
        Assert.NotNull(turn.Result.ClarificationQuestion);
        Assert.Single(service.GetSession(conv).Turns);
    }

    [Fact]
    public async Task SearchError_PropagatesAsAbstention_NotRawError()
    {
        // Сервис с «падающим» индексом — эмбеддинг/поиск бросит исключение.
        var rag = new RagAnswerService(
            new RagOptions(
                IndexPath: Path.Combine(_tempDir, "absent.sqlite3"),
                OllamaUrl: "http://ollama.local",
                EmbedModel: "qwen3-embedding:0.6b",
                TopK: 1, MaxContextChars: 100,
                EmbedTimeout: TimeSpan.FromMilliseconds(10),
                EmbedMaxRetries: 0,
                LlmTimeout: TimeSpan.FromMilliseconds(10)),
            MakeThrowingEmbeddings(),
            new StubLlmClient("ok"));
        var facade = new RagQueryService(
            rag,
            new NoRagAnswerService(new StubLlmClient("ok"), TimeSpan.FromMilliseconds(10)),
            rag.Options);
        var service = new RagMiniChatService(facade);

        var turn = await service.AskAsync("c1", "Вопрос", RagMode.Rag);

        Assert.True(turn.Result.Abstained);
        Assert.Equal(RagAbstentionReason.SearchError, turn.Result.AbstentionReason);
        Assert.Equal(RagAbstentionMessages.Russian, turn.Result.Answer);
    }

    [Fact]
    public async Task Day25Runner_RejectsUnexpectedAbstention()
    {
        var (service, _) = await BuildRevitServersServiceAsync();
        var runner = new RagMiniChatScenarioRunner(service);
        var scenario = new RagMiniChatScenario(
            "known-answer", "case", "Найти сервер", [], [],
            [new RagMiniChatScenarioStep("А что насчёт сервера revit-999?", null)]);

        var report = await runner.RunAsync([scenario], null, CancellationToken.None);

        Assert.False(report.OverallPassed);
        Assert.True(report.Scenarios[0].Steps[0].Result.Abstained);
        Assert.Contains("Неожиданное", report.Scenarios[0].Steps[0].FailureReason);
    }

    [Fact]
    public void Search_ExactServerBeatsSimilarWrongServer()
    {
        var path = Path.Combine(_tempDir, "servers.sqlite3");
        BuildSyntheticIndex(path, "qwen3-embedding:0.6b", 2,
        [
            ("wrong", "confluence_html", "Таблица", "Проекты 2022 / revit-701",
                "Сервер: revit-701. Проект: OTHER. Год: Проекты 2022", null, new[] { 1f, 0f }),
            ("right", "confluence_html", "Таблица", "Проекты 2022 / revit-702",
                "Сервер: revit-702. Проект: PRKS-SD2. Год: Проекты 2022", null, new[] { 0.8f, 0.6f })
        ]);

        using var reader = new StructuralIndexReader(path);
        Assert.Equal("wrong", reader.Search([1f, 0f], 1)[0].ChunkId);
        Assert.Equal("right", reader.Search([1f, 0f], 1, "revit-702")[0].ChunkId);
    }

    [Fact]
    public async Task ServerProjectList_UsesExactRows_WhenModelCannotCiteThem()
    {
        var path = Path.Combine(_tempDir, "project-list.sqlite3");
        BuildSyntheticIndex(path, "qwen3-embedding:0.6b", 8,
        [
            ("wrong", "confluence_html", "Таблица", "Проекты 2022 / revit-701",
                "Сервер: revit-701. Проект: OTHER. Год: Проекты 2022", null, MakeOneHot(8, 0)),
            ("p1", "confluence_html", "Таблица", "Проекты 2022 / revit-702",
                "Сервер: revit-702. Проект: PRKS-SD2. Год: Проекты 2022", null, MakeOneHot(8, 0)),
            ("p2", "confluence_html", "Таблица", "Проекты 2022 / revit-702",
                "Сервер: revit-702. Проект: STLB-SD1. Год: Проекты 2022", null, MakeOneHot(8, 0))
        ]);
        var options = MakeBaselineOptions(path, preFilterK: 3, postFilterK: 3) with { ScoreThreshold = 0.5f };
        var embeddings = new OllamaEmbeddingsClient(MakeEmbeddingHttpForAnyQuery(), TimeSpan.FromSeconds(2), 0);
        var rag = new RagAnswerService(options, embeddings, new StubLlmClient("Не знаю"));

        var result = await rag.AskAsync("Какие проекты хранятся на сервере 702 для 2022 версии ревита?",
            options.ToProductionSettings(), CancellationToken.None);

        Assert.False(result.Abstained, $"{result.AbstentionReason}: {result.Error}; sources={string.Join(" | ", result.Sources.Select(s => s.Text))}");
        Assert.Contains("PRKS-SD2", result.Answer);
        Assert.Contains("STLB-SD1", result.Answer);
        Assert.Contains("версию Revit", result.Answer);
        Assert.DoesNotContain("OTHER", result.Answer);
        Assert.Equal(2, result.CitationsSafe.Count);
        Assert.All(result.CitationsSafe, c => Assert.True(c.Verified));

        var project = await rag.AskAsync("На каком сервере находится проект PRKS-SD2?",
            options.ToProductionSettings(), CancellationToken.None);
        Assert.False(project.Abstained);
        Assert.Contains("revit-702", project.Answer);
        Assert.Single(project.CitationsSafe);
        Assert.True(project.CitationsSafe[0].Verified);

        var withoutProjectWord = await rag.AskAsync("На каком сервере находится PRKS-SD2?",
            options.ToProductionSettings(), CancellationToken.None);
        Assert.False(withoutProjectWord.Abstained);
        Assert.Contains("revit-702", withoutProjectWord.Answer);
        Assert.Single(withoutProjectWord.CitationsSafe);
        Assert.True(withoutProjectWord.CitationsSafe[0].Verified);
    }

    [Fact]
    public async Task Clarification_ChangesFollowingProjectListFormat()
    {
        var path = Path.Combine(_tempDir, "project-format.sqlite3");
        BuildSyntheticIndex(path, "qwen3-embedding:0.6b", 8,
        [
            ("old", "confluence_html", "Таблица", "Проекты 2022 / revit-203",
                "Сервер: revit-203. Проект: PRKS-09. Год: Проекты 2022", null, MakeOneHot(8, 0)),
            ("new", "confluence_html", "Таблица", "Проекты 2024 / revit-203",
                "Сервер: revit-203. Проект: VTNK-09. Год: Проекты 2024", null, MakeOneHot(8, 0))
        ]);
        var options = MakeBaselineOptions(path, preFilterK: 2, postFilterK: 2) with { ScoreThreshold = 0.5f };
        var rag = new RagAnswerService(options,
            new OllamaEmbeddingsClient(MakeEmbeddingHttpForAnyQuery(), TimeSpan.FromSeconds(2), 0),
            new StubLlmClient("Не знаю"));
        var noRag = new NoRagAnswerService(new StubLlmClient("Не знаю"), TimeSpan.FromSeconds(1));
        var chat = new RagMiniChatService(new RagQueryService(rag, noRag, options));
        chat.SetGoal("format", "Узнать проекты сервера 203");

        var clarification = await chat.AskAsync("format",
            "Уточняю: Давай мне ответ в формате 2022: перечень проектов 2024: перечень проектов");
        var answer = await chat.AskAsync("format", "Какие проекты хранятся на сервере 203");

        Assert.StartsWith("Принял уточнение", clarification.Result.Answer);
        Assert.Single(chat.GetSession("format").State.Clarifications);
        Assert.Equal("2022: PRKS-09\n2024: VTNK-09", answer.Result.Answer);
        Assert.Equal(2, answer.Result.CitationsSafe.Count);
        Assert.All(answer.Result.CitationsSafe, c => Assert.True(c.Verified));
    }

    [Fact]
    public async Task Conversations_AreIsolatedById()
    {
        var (service, _) = await BuildOfficeNormsServiceAsync();

        service.SetGoal("A", "goal A");
        service.SetGoal("B", "goal B");
        service.AddTerms("A", new[] { "term-A" });
        service.AddTerms("B", new[] { "term-B" });

        await service.AskAsync("A", "qA", RagMode.Rag);
        await service.AskAsync("B", "qB", RagMode.Rag);

        var sa = service.GetSession("A");
        var sb = service.GetSession("B");
        Assert.Single(sa.Turns);
        Assert.Single(sb.Turns);
        Assert.Contains("term-A", sa.State.Terms);
        Assert.DoesNotContain("term-B", sa.State.Terms);
        Assert.Contains("term-B", sb.State.Terms);
        Assert.DoesNotContain("term-A", sb.State.Terms);
        Assert.Contains("goal A", sa.State.Goal);
        Assert.Contains("goal B", sb.State.Goal);
    }

    [Fact]
    public async Task EnrichedQuestion_TruncatesLongGoalAndTerms()
    {
        var (service, _) = await BuildOfficeNormsServiceAsync(
            new RagMiniChatWindowOptions(MaxTurns: 5, MaxGoalChars: 10, MaxClarificationsChars: 30, MaxTermsChars: 5));
        service.SetGoal("c1", new string('X', 100));
        service.AddTerms("c1", new[] { "очень-длинный-термин-1", "очень-длинный-термин-2" });
        // Реальный AskAsync: проверим, что контекст обрезан по символам.
        var turn = await service.AskAsync("c1", "текущий вопрос", RagMode.Rag);
        Assert.NotNull(turn);
        Assert.NotNull(turn.EnrichedQuestion);
        Assert.Contains("XXXXXXXXXX", turn.EnrichedQuestion);
        Assert.DoesNotContain(new string('X', 11), turn.EnrichedQuestion);
        // При maxTermsChars = 5 только самые короткие термины попадут в контекст.
        // Здесь проверяем, что длинные термины обрезаны / не попали целиком.
        Assert.DoesNotContain("очень-длинный-термин-1", turn.EnrichedQuestion);
        Assert.DoesNotContain("очень-длинный-термин-2", turn.EnrichedQuestion);
    }

    // ==================================================================
    // Инфраструктура тестов: синтетические индексы и stub-сервисы.
    // ==================================================================

    private async Task<(RagMiniChatService Service, RecordingLlmClient Llm)> BuildOfficeNormsServiceAsync(
        RagMiniChatWindowOptions? window = null)
    {
        var indexPath = Path.Combine(_tempDir, "office-norms.sqlite3");
        const int vectorDim = 8;
        // Каждому чанку — уникальный «центроид» в одной из осей, чтобы поиск
        // детерминированно возвращал нужный чанк для соответствующего
        // ключевого слова в вопросе.
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("c-hum",   "pdf", "СП 60",     "Микроклимат",  "влажность офис 45 процентов допустимо",         1, MakeOneHot(vectorDim, 0)),
            ("c-temp",  "pdf", "СП 60",     "Микроклимат",  "температура офис 22 градуса",                   2, MakeOneHot(vectorDim, 1)),
            ("c-noise", "pdf", "СП 51",     "Шум",          "уровень шума офис 55 дБ",                       3, MakeOneHot(vectorDim, 2)),
            ("c-table", "pdf", "ГОСТ Р",    "Эргономика",   "высота стола 75 см",                            4, MakeOneHot(vectorDim, 3)),
            ("c-light", "pdf", "СП 52",     "Освещение",    "освещённость рабочего места 300 лк",            5, MakeOneHot(vectorDim, 4)),
            ("c-area",  "pdf", "СП 44",     "Площадь",      "площадь на сотрудника 4.5 м2",                 0, MakeOneHot(vectorDim, 5)),
            ("c-meet",  "pdf", "СП 44",     "Переговорная", "переговорная комната 1.5 м2 на человека",       0, MakeOneHot(vectorDim, 6)),
            ("c-vent",  "pdf", "СП 60",     "Вентиляция",   "кратность воздухообмена офис 1.5",              0, MakeOneHot(vectorDim, 7)),
            ("c-fire",  "pdf", "123-ФЗ",    "Пожарная",     "эвакуационные пути ширина 1.2 м",               0, MakeOneHot(vectorDim, 0, 0.85f)), // matches fire/EVAC
        });

        var replies = new Queue<string>();
        var seedReplies = new[]
        {
            StructuredAnswer("c-hum",   "влажность офис 45 процентов допустимо",            1, "Микроклимат"),
            StructuredAnswer("c-temp",  "температура офис 22 градуса",                      2, "Микроклимат"),
            StructuredAnswer("c-noise", "уровень шума офис 55 дБ",                         3, "Шум"),
            StructuredAnswer("c-table", "высота стола 75 см",                              4, "Эргономика"),
            StructuredAnswer("c-light", "освещённость рабочего места 300 лк",              5, "Освещение"),
            StructuredAnswer("c-area",  "площадь на сотрудника 4.5 м2",                    0, "Площадь"),
            StructuredAnswer("c-meet",  "переговорная комната 1.5 м2 на человека",        0, "Переговорная"),
            StructuredAnswer("c-vent",  "кратность воздухообмена офис 1.5",               0, "Вентиляция"),
            StructuredAnswer("c-fire",  "эвакуационные пути ширина 1.2 м",                0, "Пожарная"),
        };
        for (var i = 0; i < 60; i++)
        {
            replies.Enqueue(seedReplies[i % seedReplies.Length]);
        }
        var llm = new RecordingLlmClient(replies);

        // HTTP-обработчик возвращает вектор, ближайший к чанку, который
        // соответствует ключевым словам в вопросе. Это позволяет
        // детерминированно проверять, что контекст + индекс дают
        // ожидаемый результат поиска.
        var officeKeywordMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["влажность"] = 0,
            ["температур"] = 1,
            ["шум"] = 2,
            ["стол"] = 3, ["высота"] = 3, ["эргономик"] = 3,
            ["освещён"] = 4, ["освещ"] = 4,
            ["площад"] = 5, ["сотрудник"] = 5,
            ["переговорн"] = 6, ["встреч"] = 6,
            ["вентиляц"] = 7, ["воздухообмен"] = 7, ["кратност"] = 7,
            ["пожар"] = 0, ["эвакуац"] = 0,
            ["резюме"] = 0, ["итог"] = 0, // финальный вопрос — пусть вернёт влажность
        };
        var embeddings = new OllamaEmbeddingsClient(
            MakeKeywordEmbeddingHttp(vectorDim, officeKeywordMap, fallbackAxis: 0),
            TimeSpan.FromSeconds(2), 0);
        var options = MakeBaselineOptions(indexPath, preFilterK: 8, postFilterK: 4);
        var rag = new RagAnswerService(options, embeddings, llm);
        var noRag = new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1));
        var facade = new RagQueryService(rag, noRag, options);
        var service = new RagMiniChatService(facade, window);
        await Task.Yield();
        return (service, llm);
    }

    private async Task<(RagMiniChatService Service, RecordingLlmClient Llm)> BuildRevitServersServiceAsync(
        RagMiniChatWindowOptions? window = null)
    {
        var indexPath = Path.Combine(_tempDir, "revit-servers.sqlite3");
        const int vectorDim = 8;
        BuildSyntheticIndex(indexPath, "qwen3-embedding:0.6b", vectorDim, new (string, string, string, string, string, int?, float[])[]
        {
            ("revit-702", "confluence", "Серверы", "revit-702", "Projects 2020: LEGACY-A; server revit-702", 1, MakeOneHot(vectorDim, 0)),
            ("revit-703", "confluence", "Серверы", "revit-703", "Projects 2022: STLB-OK1; STLB-OK4; STLB-OK7; server revit-703", 2, MakeOneHot(vectorDim, 1)),
            ("revit-704", "confluence", "Серверы", "revit-704", "Projects 2023: STLB-OK2; STLB-OK9; server revit-704", 3, MakeOneHot(vectorDim, 2)),
            ("revit-705", "confluence", "Серверы", "revit-705", "Projects 2021: OLD-OK3; server revit-705",     4, MakeOneHot(vectorDim, 3)),
        });

        var replies = new Queue<string>();
        var seedReplies = new[]
        {
            StructuredAnswerConfluence("revit-703", "Projects 2022: STLB-OK1; STLB-OK4; STLB-OK7; server revit-703", "revit-703", 2),
        };
        for (var i = 0; i < 60; i++)
        {
            replies.Enqueue(seedReplies[i % seedReplies.Length]);
        }
        // Для вопроса про revit-999 LLM должен abstentionить.
        replies.Enqueue("Не знаю. Уточните, пожалуйста, вопрос.");
        var llm = new RecordingLlmClient(replies);

        var serverKeywordMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["999"] = -1,
        };
        var embeddings = new OllamaEmbeddingsClient(
            MakeKeywordEmbeddingHttp(vectorDim, serverKeywordMap, fallbackAxis: 1),
            TimeSpan.FromSeconds(2), 0);
        var options = MakeBaselineOptions(indexPath, preFilterK: 4, postFilterK: 4)
            with { ScoreThreshold = 0.5f };
        var rag = new RagAnswerService(options, embeddings, llm);
        var noRag = new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1));
        var facade = new RagQueryService(rag, noRag, options);
        var service = new RagMiniChatService(facade, window);
        await Task.Yield();
        return (service, llm);
    }

    private static RagQueryService BuildEmptyFacade()
    {
        var noRag = new NoRagAnswerService(new StubLlmClient("n/a"), TimeSpan.FromSeconds(1));
        var rag = new RagAnswerService(
            new RagOptions(
                IndexPath: Path.Combine(Path.GetTempPath(), "absent.sqlite3"),
                OllamaUrl: "http://ollama.local",
                EmbedModel: "qwen3-embedding:0.6b",
                TopK: 1, MaxContextChars: 100,
                EmbedTimeout: TimeSpan.FromMilliseconds(10),
                EmbedMaxRetries: 0,
                LlmTimeout: TimeSpan.FromMilliseconds(10)),
            MakeThrowingEmbeddings(),
            new StubLlmClient("ok"));
        return new RagQueryService(rag, noRag, rag.Options);
    }

    private static string StructuredAnswer(string chunkId, string text, int? page, string section)
    {
        return $"""
            ANSWER: {text}
            QUOTES:
            [{chunkId}] source=pdf section="{section}" pdf_page={(page?.ToString() ?? "-")} quote="{text}"
            """;
    }

    private static string StructuredAnswerConfluence(string chunkId, string text, string section, int pdfPage)
    {
        return $"""
            ANSWER: {text}
            QUOTES:
            [{chunkId}] source=confluence section="{section}" pdf_page={pdfPage} quote="{text}"
            """;
    }

    private static HttpClient MakeEmbeddingHttpForAnyQuery()
    {
        return new HttpClient(new StubHttpHandler(req =>
        {
            var body = req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("input", out var input) &&
                    input.ValueKind == JsonValueKind.Array && input.GetArrayLength() > 0)
                {
                    text = input[0].GetString();
                }
            }
            catch
            {
                // ignore
            }
            // Возвращаем единичный вектор для любого входа — нас не интересует
            // конкретный similarity, нам важно, что в индекс что-то попало.
            var vec = new float[8];
            vec[0] = 1f;
            return HttpResponseFacts.Json(HttpStatusCode.OK, new { embeddings = new[] { vec } });
        }))
        {
            BaseAddress = new Uri("http://ollama.local"),
        };
    }

    /// <summary>
    /// HTTP-обработчик эмбеддингов: по ключевым словам в запросе возвращает
    /// вектор, соответствующий чанку, ближайшему к этим словам. Используется,
    /// чтобы детерминированно проверять, что RAG возвращает ожидаемый чанк.
    /// Если ни одно ключевое слово не нашлось — fallback. Если 999 — возвращаем
    /// вектор, далёкий от всех чанков (fallbackAxis с инвертированной компонентой).
    /// </summary>
    private static HttpClient MakeKeywordEmbeddingHttp(
        int vectorDim,
        Dictionary<string, int> keywordToAxis,
        int fallbackAxis)
    {
        var handler = new StubHttpHandler(req =>
        {
            var body = req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string? text = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("input", out var input) &&
                    input.ValueKind == JsonValueKind.Array && input.GetArrayLength() > 0)
                {
                    text = input[0].GetString();
                }
            }
            catch
            {
                // ignore
            }

            var axis = fallbackAxis;
            if (text is not null)
            {
                var marker = "Текущий вопрос пользователя:";
                var markerAt = text.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerAt >= 0) text = text[(markerAt + marker.Length)..];
                foreach (var (key, mappedAxis) in keywordToAxis)
                {
                    if (text.Contains(key, StringComparison.OrdinalIgnoreCase))
                    {
                        axis = mappedAxis;
                        break;
                    }
                }
            }

            float[] vec;
            if (axis < 0)
            {
                // Специальный сигнал: вернуть нулевой вектор — все чанки будут
                // иметь одинаково низкое сходство, post-filter их отбросит.
                vec = new float[vectorDim];
            }
            else
            {
                vec = MakeOneHot(vectorDim, axis);
            }
            return HttpResponseFacts.Json(HttpStatusCode.OK, new { embeddings = new[] { vec } });
        });
        return new HttpClient(handler) { BaseAddress = new Uri("http://ollama.local") };
    }

    private static float[] MakeOneHot(int dim, int axis, float scale = 1f)
    {
        var v = new float[dim];
        if (axis >= 0 && axis < dim)
        {
            v[axis] = scale;
        }
        return v;
    }

    private static RagOptions MakeBaselineOptions(string indexPath, int preFilterK, int postFilterK) =>
        new(
            IndexPath: indexPath,
            OllamaUrl: "http://ollama.local",
            EmbedModel: "qwen3-embedding:0.6b",
            TopK: postFilterK,
            MaxContextChars: 4000,
            EmbedTimeout: TimeSpan.FromSeconds(2),
            EmbedMaxRetries: 0,
            LlmTimeout: TimeSpan.FromSeconds(5),
            RewriteEnabled: false,
            RewriteTimeout: TimeSpan.FromSeconds(1),
            PreFilterK: preFilterK,
            ScoreThreshold: 0f,
            PostFilterK: postFilterK);

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

    private static OllamaEmbeddingsClient MakeThrowingEmbeddings()
    {
        var handler = new StubHttpHandler(_ => throw new InvalidOperationException("embeddings should not be called"));
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") };
        return new OllamaEmbeddingsClient(http, TimeSpan.FromSeconds(1), 0);
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
            var reply = _replies.Count > 0 ? _replies.Dequeue() : "Не знаю. Уточните, пожалуйста, вопрос.";
            return Task.FromResult(new LlmResponse(reply, "stop", new TokenUsage(0, 0, 0, 0, 0, 0, 0, false)));
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
