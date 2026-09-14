using System.Text;
using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Storage;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

try
{
    var options = AppOptions.FromEnvironment();
    long cumulativeBilledTokens = 0;
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    ILlmClient llmClient = new GigaChatClient(httpClient, options);
    IChatHistoryStore historyStore = new SqliteChatHistoryStore(options.DatabasePath);
    IDesignAssistantAgent agent = new DesignAssistantAgent(
        llmClient,
        historyStore,
        DesignerAssistantPrompt.Text,
        options);
    await agent.InitializeAsync(cancellationSource.Token);

    PrintWelcome(options.Model, options.DatabasePath, agent.GetContextStatus());

    while (!cancellationSource.IsCancellationRequested)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("Вы: ");
        Console.ResetColor();
        var input = Console.ReadLine();

        if (input is null || input.Equals("/exit", StringComparison.OrdinalIgnoreCase))
        {
            break;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            continue;
        }

        try
        {
            if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                continue;
            }

            if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
            {
                await agent.ClearHistoryAsync(cancellationSource.Token);
                Console.WriteLine("История, facts, checkpoint и ветки очищены.\n");
                continue;
            }

            if (input.StartsWith("/strategy", StringComparison.OrdinalIgnoreCase))
            {
                var argument = input["/strategy".Length..].Trim();
                var status = string.IsNullOrEmpty(argument) ||
                             argument.Equals("status", StringComparison.OrdinalIgnoreCase)
                    ? agent.GetContextStatus()
                    : await agent.SetStrategyAsync(
                        ParseStrategy(argument),
                        cancellationSource.Token);
                PrintContextStatus(status);
                continue;
            }

            if (input.Equals("/checkpoint", StringComparison.OrdinalIgnoreCase))
            {
                PrintContextStatus(await agent.CreateCheckpointAsync(
                    cancellationSource.Token));
                Console.WriteLine("Checkpoint сохранён.\n");
                continue;
            }

            if (input.StartsWith("/branch", StringComparison.OrdinalIgnoreCase))
            {
                var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var status = parts.Length switch
                {
                    2 when parts[1].Equals("list", StringComparison.OrdinalIgnoreCase) =>
                        agent.GetContextStatus(),
                    3 when parts[1].Equals("create", StringComparison.OrdinalIgnoreCase) =>
                        await agent.CreateBranchAsync(parts[2], cancellationSource.Token),
                    3 when parts[1].Equals("switch", StringComparison.OrdinalIgnoreCase) =>
                        await agent.SwitchBranchAsync(parts[2], cancellationSource.Token),
                    _ => throw new ArgumentException(
                        "Используйте /branch list, /branch create <имя> или /branch switch <имя>.")
                };
                PrintContextStatus(status);
                continue;
            }

            if (input.Equals("/compare", StringComparison.OrdinalIgnoreCase))
            {
                await RunStrategiesComparisonAsync(
                    llmClient,
                    options,
                    cancellationSource.Token);
                continue;
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Помощник думает...");
            Console.ResetColor();
            var response = await agent.AskAsync(input, cancellationSource.Token);
            cumulativeBilledTokens +=
                response.ModelResponse.Usage.BilledTokens + response.MemoryBilledTokens;
            PrintAnswer(response, cumulativeBilledTokens, options);
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
            break;
        }
        catch (Exception exception)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Ошибка: {exception.Message}\n");
            Console.ResetColor();
        }
    }
}
catch (InvalidOperationException exception)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(exception.Message);
    Console.ResetColor();
    Environment.ExitCode = 1;
}

static ContextStrategy ParseStrategy(string value) => value.ToLowerInvariant() switch
{
    "sliding" => ContextStrategy.SlidingWindow,
    "facts" => ContextStrategy.StickyFacts,
    "branching" => ContextStrategy.Branching,
    _ => throw new ArgumentException("Стратегии: sliding, facts, branching.")
};

static void PrintAnswer(
    AgentResponse response,
    long cumulativeBilledTokens,
    AppOptions options)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("Помощник: ");
    Console.ResetColor();
    Console.WriteLine(response.ModelResponse.Content);

    var usage = response.ModelResponse.Usage;
    var requestTokens = usage.BilledTokens + response.MemoryBilledTokens;
    var requestCost = requestTokens * options.PricePerMillionTokens / 1_000_000m;
    var totalCost = cumulativeBilledTokens * options.PricePerMillionTokens / 1_000_000m;
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"\nСтратегия: {StrategyName(response.Strategy)}");
    Console.WriteLine(
        $"Полная история: ≈{response.FullHistoryTokens:N0}; " +
        $"отправлено: ≈{response.SentHistoryTokens:N0}; " +
        $"facts: {response.FactsCount}; ветка: {response.ActiveBranch}");
    Console.WriteLine(
        $"Ответ: {usage.CompletionTokens:N0}; память: {response.MemoryBilledTokens:N0}; " +
        $"к оплате: {requestTokens:N0}; причина: {response.ModelResponse.FinishReason}");
    Console.WriteLine(
        $"Стоимость запроса: ≈{requestCost:0.000000} руб.; " +
        $"за запуск: ≈{totalCost:0.000000} руб.\n");
    Console.ResetColor();
}

static async Task RunStrategiesComparisonAsync(
    ILlmClient llmClient,
    AppOptions options,
    CancellationToken cancellationToken)
{
    Console.WriteLine(
        "Автотест использует отдельные истории и выполнит не менее 62 запросов " +
        "к модели. Основная SQLite-база не изменится.\n");

    var results = new List<ComparisonRunResult>();
    foreach (var strategy in Enum.GetValues<ContextStrategy>())
    {
        results.Add(await RunScenarioAsync(
            llmClient,
            options,
            strategy,
            cancellationToken));
    }

    PrintComparison(results, options);
}

static async Task<ComparisonRunResult> RunScenarioAsync(
    ILlmClient llmClient,
    AppOptions options,
    ContextStrategy strategy,
    CancellationToken cancellationToken)
{
    Console.WriteLine($"===== {StrategyName(strategy)} =====\n");
    IDesignAssistantAgent agent = new DesignAssistantAgent(
        llmClient,
        new InMemoryChatHistoryStore(),
        DesignerAssistantPrompt.Text,
        options);
    await agent.InitializeAsync(cancellationToken);
    await agent.SetStrategyAsync(strategy, cancellationToken);

    long billedTokens = 0;
    long sentHistoryTokens = 0;
    var maxContextTokens = 0;
    AgentResponse? lastResponse = null;

    for (var index = 0; index < ComparisonScenario.Questions.Count; index++)
    {
        if (strategy == ContextStrategy.Branching && index == 8)
        {
            await agent.CreateCheckpointAsync(cancellationToken);
            await agent.CreateBranchAsync("option-a", cancellationToken);
            await agent.CreateBranchAsync("option-b", cancellationToken);
            await agent.SwitchBranchAsync("option-a", cancellationToken);
            Console.WriteLine("Checkpoint: созданы option-a и option-b; продолжаем option-a.\n");
        }

        var question = ComparisonScenario.Questions[index];
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[{index + 1}/15] Вы: {question}");
        Console.ResetColor();
        lastResponse = await agent.AskAsync(question, cancellationToken);
        billedTokens += lastResponse.ModelResponse.Usage.BilledTokens +
                        lastResponse.MemoryBilledTokens;
        sentHistoryTokens += lastResponse.SentHistoryTokens;
        maxContextTokens = Math.Max(
            maxContextTokens,
            lastResponse.ModelResponse.Usage.ContextTokens);
        Console.WriteLine($"Помощник: {lastResponse.ModelResponse.Content}\n");
    }

    if (strategy == ContextStrategy.Branching)
    {
        await DemonstrateIndependentBranchesAsync(agent, cancellationToken);
    }

    return new ComparisonRunResult(
        strategy,
        billedTokens,
        sentHistoryTokens,
        maxContextTokens,
        lastResponse!.FactsCount,
        lastResponse.ModelResponse.Content);
}

static async Task DemonstrateIndependentBranchesAsync(
    IDesignAssistantAgent agent,
    CancellationToken cancellationToken)
{
    Console.WriteLine("===== ПРОВЕРКА НЕЗАВИСИМОСТИ ВЕТОК =====");
    await agent.SwitchBranchAsync("option-b", cancellationToken);
    var branchB = await agent.AskAsync(
        "В этой ветке прими ограничение высоты воздуховода 300 мм. Какое ограничение действует?",
        cancellationToken);
    Console.WriteLine($"option-b: {branchB.ModelResponse.Content}\n");

    await agent.SwitchBranchAsync("option-a", cancellationToken);
    var branchA = await agent.AskAsync(
        "Какое ограничение высоты воздуховода действует в этой ветке?",
        cancellationToken);
    Console.WriteLine($"option-a: {branchA.ModelResponse.Content}\n");
}

static void PrintComparison(
    IReadOnlyCollection<ComparisonRunResult> results,
    AppOptions options)
{
    Console.WriteLine("===== ИТОГОВОЕ СРАВНЕНИЕ =====");
    foreach (var result in results)
    {
        var cost = result.TotalBilledTokens *
                   options.PricePerMillionTokens / 1_000_000m;
        Console.WriteLine(
            $"{StrategyName(result.Strategy)}: контекстов отправлено " +
            $"≈{result.TotalSentHistoryTokens:N0}, максимум {result.MaxContextTokens:N0}, " +
            $"к оплате {result.TotalBilledTokens:N0}, стоимость ≈{cost:0.000000} руб., " +
            $"facts {result.FinalFactsCount}.");
    }

    Console.WriteLine("\nКонтроль качества: сравните ответы на вопрос 15:");
    foreach (var result in results)
    {
        Console.WriteLine($"\n{StrategyName(result.Strategy)}:\n{result.FinalAnswer}");
    }

    Console.WriteLine();
}

static void PrintContextStatus(ContextStatus status)
{
    Console.WriteLine($"Стратегия: {StrategyName(status.Strategy)}");
    Console.WriteLine(
        $"Facts: {status.FactsCount}; активная ветка: {status.ActiveBranch}; " +
        $"сообщений: {status.ActiveMessageCount}");
    if (status.BranchNames.Count > 0)
    {
        Console.WriteLine($"Ветки: {string.Join(", ", status.BranchNames)}");
    }

    Console.WriteLine();
}

static string StrategyName(ContextStrategy strategy) => strategy switch
{
    ContextStrategy.SlidingWindow => "Sliding Window",
    ContextStrategy.StickyFacts => "Sticky Facts",
    ContextStrategy.Branching => "Branching",
    _ => strategy.ToString()
};

static void PrintWelcome(string model, string databasePath, ContextStatus status)
{
    Console.WriteLine("Помощник проектировщика, день 10");
    Console.WriteLine($"Модель: {model}");
    Console.WriteLine($"История: {databasePath}");
    Console.WriteLine($"Стратегия: {StrategyName(status.Strategy)}");
    Console.WriteLine("Введите /help для просмотра команд.\n");
}

static void PrintHelp()
{
    Console.WriteLine("Команды:");
    Console.WriteLine("  /strategy sliding|facts|branching  выбрать стратегию");
    Console.WriteLine("  /strategy status                   показать состояние");
    Console.WriteLine("  /checkpoint                        сохранить точку ветвления");
    Console.WriteLine("  /branch create <имя>               создать ветку от checkpoint");
    Console.WriteLine("  /branch switch <имя>               переключить ветку");
    Console.WriteLine("  /branch list                       показать ветки");
    Console.WriteLine("  /compare                           сравнить три стратегии");
    Console.WriteLine("  /clear                             очистить всю память");
    Console.WriteLine("  /exit                              завершить работу");
    Console.WriteLine("  /help                              показать справку\n");
}
