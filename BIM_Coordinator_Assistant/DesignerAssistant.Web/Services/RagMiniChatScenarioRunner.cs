using System.Diagnostics;

namespace DesignerAssistant.Web.Services;

/// <summary>
/// Одна запланированная реплика в Day 25 сценарии: текст
/// пользователя и опциональное имя чанка-цитаты, которое мы ожидаем
/// увидеть в <see cref="RagMiniChatTurn.Result"/> (verified).
/// </summary>
public sealed record RagMiniChatScenarioStep(
    string Question,
    string? ExpectCitationChunk,
    bool ExpectAbstain = false);

/// <summary>
/// Описание Day 25 сценария: имя, цель/уточнения/термины,
/// которые нужно установить в <see cref="RagMiniChatService"/>,
/// и список шагов. Каждый шаг — это один вызов AskAsync.
/// </summary>
public sealed record RagMiniChatScenario(
    string Name,
    string ConversationId,
    string Goal,
    IReadOnlyList<string> Clarifications,
    IReadOnlyList<string> Terms,
    IReadOnlyList<RagMiniChatScenarioStep> Steps);

/// <summary>
/// Результат прогона одного шага в Day 25 сценарии. <c>ElapsedMs</c>
/// выставляется после построения через свойство в инициализаторе.
/// </summary>
public sealed record RagMiniChatScenarioStepResult
{
    public int Index { get; init; }
    public string Question { get; init; } = "";
    public string ExpectCitationChunk { get; init; } = "";
    public bool ExpectAbstain { get; init; }
    public RagMiniChatTurn Turn { get; init; } = null!;
    public bool HasError { get; init; }
    public bool Passed { get; init; }
    public string? FailureReason { get; init; }
    public long ElapsedMs { get; set; }

    public RagQueryResult Result => Turn.Result;
    public IReadOnlyList<RagCitation> CitationsSafe => Result.CitationsSafe;
    public IReadOnlyList<RagSource> SourcesSafe => Result.Sources;
    public bool CitationsPresent => CitationsSafe.Count > 0;
    public bool HasSources => SourcesSafe.Count > 0;

    public string Outcome
    {
        get
        {
            if (HasError) return "ошибка";
            if (Question.StartsWith("Уточняю:", StringComparison.OrdinalIgnoreCase) &&
                Result.Answer.StartsWith("Принял уточнение", StringComparison.Ordinal)) return "уточнение сохранено";
            if (Result.Abstained) return ExpectAbstain ? "abstain (ожидаемо)" : "abstain (неожиданно)";
            if (ExpectAbstain) return "ожидался abstain";
            return CitationsPresent && HasSources ? "ответ с цитатами" : "ответ без цитат";
        }
    }

    public bool CitationMatch =>
        string.IsNullOrEmpty(ExpectCitationChunk)
        || CitationsSafe.Any(c => c.ChunkId == ExpectCitationChunk && c.Verified);
}

/// <summary>
/// Итог прогона одного Day 25 сценария: список результатов шагов
/// плюс фактические значения goal/clarifications/terms из
/// <see cref="RagMiniChatService.GetSession"/>.
/// </summary>
public sealed record RagMiniChatScenarioRunReport(
    string ScenarioName,
    string ConversationId,
    int TotalSteps,
    int PassedSteps,
    int ErroredSteps,
    int AbstainedSteps,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string? Error,
    IReadOnlyList<RagMiniChatScenarioStepResult> Steps,
    string CapturedGoal,
    IReadOnlyList<string> CapturedClarifications,
    IReadOnlyList<string> CapturedTerms)
{
    public bool Passed => Error is null && PassedSteps == TotalSteps && TotalSteps > 0;
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// Итог прогона Day 25 автотеста: отчёты по каждому сценарию плюс
/// сводное «всё прошло» для UI.
/// </summary>
public sealed record RagMiniChatDay25Report(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyList<RagMiniChatScenarioRunReport> Scenarios,
    string? Error)
{
    public int ScenarioTotal => Scenarios.Count;
    public int ScenarioPassed => Scenarios.Count(r => r.Passed);
    public int StepTotal => Scenarios.Sum(s => s.TotalSteps);
    public int StepPassed => Scenarios.Sum(s => s.PassedSteps);
    public int StepErrors => Scenarios.Sum(s => s.ErroredSteps);
    public int StepAbstained => Scenarios.Sum(s => s.AbstainedSteps);
    public bool OverallPassed => Error is null && Scenarios.Count > 0 && Scenarios.All(r => r.Passed);
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// Прогоняет два длинных сценария на текущем индексе через
/// <see cref="RagMiniChatService"/>: серверы проектов и базу знаний Revit.
/// Фактические вопросы требуют ответа с проверенной цитатой;
/// вопросы без данных и чистые уточнения ожидают явное «Не знаю».
/// После каждого сценария проверяется сохранность истории и памяти задачи.
/// </summary>
public sealed class RagMiniChatScenarioRunner
{
    private readonly RagMiniChatService _chat;

    public RagMiniChatScenarioRunner(RagMiniChatService chat)
    {
        ArgumentNullException.ThrowIfNull(chat);
        _chat = chat;
    }

    /// <summary>Возвращает канонический набор сценариев Дня 25.</summary>
    public IReadOnlyList<RagMiniChatScenario> BuildDefaultScenarios()
    {
        static RagMiniChatScenarioStep Step(string question, bool abstain = false) => new(question, null, abstain);
        return
        [
            new RagMiniChatScenario("Серверы проектов (12 реплик)", "day25-servers",
                "Уточнить серверы проектов по таблице Confluence", ["Сохранять точный шифр проекта"],
                ["STLB-OK1", "сервер", "Confluence"],
                [
                    Step("На каком сервере находится проект STLB-OK1?"),
                    Step("Где именно в таблице Confluence указан STLB-OK1, на каком сервере?"),
                    Step("На каком сервере находится проект PRKS-SD2?"),
                    Step("На каком сервере находится проект PRKS-PL1?"),
                    Step("Уточняю: нужен точный сервер без догадок.", true),
                    Step("Повтори сервер проекта STLB-OK1 с источником."),
                    Step("Какая версия Revit указана в строке Projects 2022 таблицы серверов?", true),
                    Step("Где находится PRKS-SD2?"),
                    Step("А PRKS-PL1?"),
                    Step("На каком сервере находится STLB-OK1?"),
                    Step("Что известно о сервере revit-703?"),
                    Step("В каком разделе таблицы Confluence указан сервер проекта STLB-OK1?")
                ]),
            new RagMiniChatScenario("База знаний Revit (10 реплик)", "day25-kb",
                "Разобраться с ошибками и настройкой Revit по базе знаний", ["Нужны только подтверждённые инструкции"],
                ["Revit", "RSN.ini", "база знаний"],
                [
                    Step("Что делать при ошибке «Не удалось построить ленту» во время расчёта контура?"),
                    Step("Куда обращаться, если программа Autodesk Revit не работает?"),
                    Step("Нужен ли отдельный файл RSN.ini для каждой версии Revit?"),
                    Step("Уточняю: отвечай только по найденным документам.", true),
                    Step("Что сказано про файл RSN.ini?"),
                    Step("Что делать при ошибке «Не удалось построить ленту» во время расчёта контура?"),
                    Step("Кто помогает, когда Revit не запускается?"),
                    Step("Есть ли подтверждённая версия Revit для проекта STLB-OK1?", true),
                    Step("Вернёмся к RSN.ini: ответь с источником."),
                    Step("Повтори, куда обращаться при сбое Autodesk Revit.")
                ])
        ];
    }

    /// <summary>
    /// Прогнать канонические сценарии. Сбрасывает состояние
    /// мини-чата перед стартом, чтобы прогоны не «склеивались»
    /// между сессиями (несмотря на scoped-режим DI).
    /// </summary>
    public Task<RagMiniChatDay25Report> RunDefaultAsync(
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        return RunAsync(BuildDefaultScenarios(), progress, cancellationToken);
    }

    /// <summary>
    /// Прогнать произвольный набор сценариев. Для каждого сценария:
    /// 1) Reset(convId) — стартуем с чистого листа;
    /// 2) SetGoal/AddClarification/AddTerms — задаём память;
    /// 3) по очереди вызываем AskAsync для каждого шага;
    /// 4) проверяем результат (citations/abstain/error);
    /// 5) снимаем итоговое состояние через GetSession.
    /// </summary>
    public async Task<RagMiniChatDay25Report> RunAsync(
        IReadOnlyList<RagMiniChatScenario> scenarios,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scenarios);
        var startedAt = DateTimeOffset.Now;
        var reports = new List<RagMiniChatScenarioRunReport>(scenarios.Count);
        string? fatal = null;
        try
        {
            for (var s = 0; s < scenarios.Count; s++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scenario = scenarios[s];
                progress?.Report($"Сценарий {s + 1}/{scenarios.Count}: {scenario.Name}");
                reports.Add(await RunSingleAsync(scenario, progress, cancellationToken));
            }
        }
        catch (OperationCanceledException)
        {
            fatal = "Отменено пользователем.";
        }
        catch (Exception ex)
        {
            fatal = ex.Message;
        }
        return new RagMiniChatDay25Report(
            StartedAt: startedAt,
            CompletedAt: DateTimeOffset.Now,
            Scenarios: reports,
            Error: fatal);
    }

    private async Task<RagMiniChatScenarioRunReport> RunSingleAsync(
        RagMiniChatScenario scenario,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var scenarioStarted = DateTimeOffset.Now;
        // Сбрасываем состояние беседы, чтобы прогоны не «склеивались».
        _chat.Reset(scenario.ConversationId);

        _chat.SetGoal(scenario.ConversationId, scenario.Goal);
        foreach (var c in scenario.Clarifications)
        {
            _chat.AddClarification(scenario.ConversationId, c);
        }
        _chat.AddTerms(scenario.ConversationId, scenario.Terms);

        var stepResults = new List<RagMiniChatScenarioStepResult>(scenario.Steps.Count);
        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = scenario.Steps[i];
            progress?.Report($"  Шаг {i + 1}/{scenario.Steps.Count}: {Truncate(step.Question, 60)}");
            var stepResult = await EvaluateStepAsync(scenario.ConversationId, i + 1, step, cancellationToken);
            stepResults.Add(stepResult);
        }

        var snapshot = _chat.GetSession(scenario.ConversationId);
        var passed = stepResults.Count(r => r.Passed);
        var errors = stepResults.Count(r => r.HasError);
        var abstained = stepResults.Count(r => r.Turn.Result.Abstained && !r.HasError);
        string? scenarioError = null;
        if (stepResults.Count != scenario.Steps.Count)
        {
            scenarioError = $"Прогнано {stepResults.Count} шагов из {scenario.Steps.Count}.";
        }
        if (snapshot.TurnCount != scenario.Steps.Count || snapshot.State.Goal != scenario.Goal ||
            scenario.Clarifications.Any(c => !snapshot.State.Clarifications.Contains(c)) ||
            scenario.Terms.Any(t => !snapshot.State.Terms.Contains(t)))
            scenarioError = "История или память задачи не сохранилась.";
        return new RagMiniChatScenarioRunReport(
            ScenarioName: scenario.Name,
            ConversationId: scenario.ConversationId,
            TotalSteps: stepResults.Count,
            PassedSteps: passed,
            ErroredSteps: errors,
            AbstainedSteps: abstained,
            StartedAt: scenarioStarted,
            CompletedAt: DateTimeOffset.Now,
            Error: scenarioError,
            Steps: stepResults,
            CapturedGoal: snapshot.State.Goal,
            CapturedClarifications: snapshot.State.Clarifications.ToArray(),
            CapturedTerms: snapshot.State.Terms.ToArray());
    }

    private async Task<RagMiniChatScenarioStepResult> EvaluateStepAsync(
        string conversationId, int index, RagMiniChatScenarioStep step, CancellationToken cancellationToken)
    {
        RagMiniChatTurn turn;
        string? error = null;
        var sw = Stopwatch.StartNew();
        try
        {
            turn = await _chat.AskAsync(conversationId, step.Question, RagMode.Rag, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            turn = new RagMiniChatTurn(
                UserQuestion: step.Question,
                EnrichedQuestion: "",
                Result: new RagQueryResult(
                    Question: step.Question,
                    Mode: RagMode.Rag,
                    Answer: "",
                    SearchSucceeded: false,
                    RetrievedCount: 0,
                    Sources: Array.Empty<RagSource>(),
                    Error: ex.Message),
                AskedAt: DateTimeOffset.UtcNow);
        }
        sw.Stop();

        var failure = EvaluateFailure(step, turn, error);
        var passed = failure is null;
        return new RagMiniChatScenarioStepResult
        {
            Index = index,
            Question = step.Question,
            ExpectCitationChunk = step.ExpectCitationChunk ?? "",
            ExpectAbstain = step.ExpectAbstain,
            Turn = turn,
            HasError = error is not null || turn.Result.Error is not null,
            Passed = passed,
            FailureReason = failure,
            ElapsedMs = sw.ElapsedMilliseconds,
        };
    }

    private static string? EvaluateFailure(
        RagMiniChatScenarioStep step, RagMiniChatTurn turn, string? callError)
    {
        if (callError is not null || turn.Result.Error is not null)
        {
            return $"ошибка: {callError ?? turn.Result.Error}";
        }
        // Ожидание abstain: модель должна отказаться и попросить уточнить.
        if (step.ExpectAbstain)
        {
            if (step.Question.StartsWith("Уточняю:", StringComparison.OrdinalIgnoreCase) &&
                turn.Result.Answer.StartsWith("Принял уточнение", StringComparison.Ordinal))
                return null;
            if (!turn.Result.Abstained)
            {
                return "ожидался abstain, получен ответ";
            }
            if (string.IsNullOrWhiteSpace(turn.Result.ClarificationQuestion))
            {
                return "abstain получен, но без clarification_question";
            }
            return null;
        }
        // Обычный шаг допускает доказанный ответ или явное отсутствие данных.
        if (turn.Result.Abstained)
        {
            return "Неожиданное «Не знаю» для вопроса с ожидаемым ответом";
        }
        if (!string.IsNullOrEmpty(step.ExpectCitationChunk))
        {
            if (turn.Result.Sources.Count == 0)
            {
                return "нет источников в ответе";
            }
            if (turn.Result.CitationsSafe.Count == 0)
            {
                return "нет цитат в ответе";
            }
            if (!turn.Result.CitationsSafe.Any(c => c.ChunkId == step.ExpectCitationChunk && c.Verified))
            {
                return $"цитата для чанка '{step.ExpectCitationChunk}' не подтверждена";
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(turn.Result.Answer) || turn.Result.Sources.Count == 0 ||
                !turn.Result.CitationsSafe.Any(c => c.Verified))
            {
                return "Нет подтверждённого ответа с источником";
            }
        }
        return null;
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "…";
    }
}
