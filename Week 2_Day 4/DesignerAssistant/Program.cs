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

    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    ILlmClient llmClient = new GigaChatClient(httpClient, options);
    IChatHistoryStore historyStore = new SqliteChatHistoryStore(options.DatabasePath);
    IDesignAssistantAgent agent = new DesignAssistantAgent(
        llmClient,
        historyStore,
        DesignerAssistantPrompt.Text,
        options);
    await agent.InitializeAsync(cancellationSource.Token);

    PrintWelcome(options.Model, options.DatabasePath);

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

        if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp();
            continue;
        }

        if (input.Equals("/clear", StringComparison.OrdinalIgnoreCase))
        {
            await agent.ClearHistoryAsync(cancellationSource.Token);
            Console.WriteLine("Контекст текущего диалога очищен.\n");
            continue;
        }

        if (input.Equals("/compare", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                await RunCompressionComparisonAsync(
                    llmClient,
                    options,
                    cancellationSource.Token);
            }
            catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Сравнение остановлено: {exception.Message}\n");
                Console.ResetColor();
            }

            continue;
        }

        if (input.StartsWith("/compression", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var argument = input["/compression".Length..].Trim();
                var status = argument.ToLowerInvariant() switch
                {
                    "on" => await agent.SetCompressionEnabledAsync(
                        true,
                        cancellationSource.Token),
                    "off" => await agent.SetCompressionEnabledAsync(
                        false,
                        cancellationSource.Token),
                    "" or "status" => agent.GetCompressionStatus(),
                    _ => throw new ArgumentException(
                        "Используйте /compression on, /compression off или /compression status.")
                };
                cumulativeBilledTokens += status.CompressionBilledTokens;
                PrintCompressionStatus(status);
            }
            catch (Exception exception)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Ошибка переключения сжатия: {exception.Message}\n");
                Console.ResetColor();
            }

            continue;
        }

        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Помощник думает...");
            Console.ResetColor();

            var response = await agent.AskAsync(input, cancellationSource.Token);
            cumulativeBilledTokens +=
                response.ModelResponse.Usage.BilledTokens +
                response.CompressionBilledTokens;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("Помощник: ");
            Console.ResetColor();
            Console.WriteLine(response.ModelResponse.Content);
            Console.WriteLine();
            PrintTokenUsage(
                response,
                cumulativeBilledTokens,
                options);
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

static void PrintTokenUsage(
    AgentResponse response,
    long cumulativeBilledTokens,
    AppOptions options)
{
    var usage = response.ModelResponse.Usage;
    var finishReason = response.ModelResponse.FinishReason;
    var occupiedTokens = usage.ContextTokens + usage.CompletionTokens;
    var occupiedPercent = occupiedTokens * 100d / options.ContextLimitTokens;
    var requestCost = usage.BilledTokens * options.PricePerMillionTokens / 1_000_000m;
    var cumulativeCost = cumulativeBilledTokens * options.PricePerMillionTokens / 1_000_000m;
    var estimateMark = usage.TextCountsAreEstimated ? "≈" : string.Empty;

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("Токены:");
    Console.WriteLine($"  текущий запрос: {estimateMark}{usage.CurrentRequestTokens:N0}");
    Console.WriteLine($"  история после ответа: {estimateMark}{usage.HistoryTokens:N0}");
    Console.WriteLine($"  ответ модели: {usage.CompletionTokens:N0}");
    Console.WriteLine($"  причина завершения: {finishReason}");
    Console.WriteLine(
        $"  контекст запроса: {estimateMark}{usage.ContextTokens:N0} из " +
        $"{options.ContextLimitTokens:N0} ({occupiedPercent:0.0}%)");
    Console.WriteLine(
        $"  usage API: вход {usage.PromptTokens:N0}, " +
        $"кэш {usage.CachedPromptTokens:N0}, к оплате {usage.BilledTokens:N0}");
    Console.WriteLine($"  оценка стоимости запроса: {requestCost:0.000000} руб.");
    Console.WriteLine($"  за текущий запуск: {cumulativeCost:0.000000} руб.");
    Console.WriteLine(
        $"  сжатие: {(response.CompressionEnabled ? "включено" : "выключено")}");
    Console.WriteLine(
        $"  полная история: " +
        $"{(response.FullHistoryTokensAreEstimated ? "≈" : string.Empty)}" +
        $"{response.FullHistoryTokens:N0} токенов");
    Console.WriteLine($"  отправлено из истории: ≈{response.SentHistoryTokens:N0} токенов");
    Console.WriteLine(
        $"  сообщения: свёрнуто {response.SummarizedMessageCount:N0}, " +
        $"дословно {response.RecentMessageCount:N0}");

    var savedTokens = Math.Max(0, response.FullHistoryTokens - response.SentHistoryTokens);
    Console.WriteLine($"  экономия контекста: ≈{savedTokens:N0} токенов");
    if (response.CompressionBilledTokens > 0)
    {
        Console.WriteLine(
            $"  создание summary: {response.CompressionBilledTokens:N0} токенов к оплате");
    }

    if (usage.TextCountsAreEstimated)
    {
        Console.WriteLine(
            "  ≈ текст оценён локально: endpoint /tokens/count недоступен для модели.");
    }

    if (finishReason.Equals("length", StringComparison.OrdinalIgnoreCase))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(
            "  Ответ обрезан: модель исчерпала max_tokens, но контекст не переполнен.");
    }

    if (usage.ContextTokens + options.MaxOutputTokens > options.ContextLimitTokens)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(
            "  Внимание: контекст вместе с резервом ответа превышает лимит модели.");
    }

    Console.ResetColor();
    Console.WriteLine();
}

static async Task RunCompressionComparisonAsync(
    ILlmClient llmClient,
    AppOptions options,
    CancellationToken cancellationToken)
{
    Console.WriteLine(
        "Запускается 30 основных запросов к модели плюс запросы на summary. " +
        "Основная история не изменится.\n");

    var withoutCompression = await RunScenarioAsync(
        llmClient,
        options,
        false,
        cancellationToken);
    var withCompression = await RunScenarioAsync(
        llmClient,
        options,
        true,
        cancellationToken);

    PrintComparison(withoutCompression, withCompression, options);
}

static async Task<ComparisonRunResult> RunScenarioAsync(
    ILlmClient llmClient,
    AppOptions options,
    bool compressionEnabled,
    CancellationToken cancellationToken)
{
    var mode = compressionEnabled ? "СО СЖАТИЕМ" : "БЕЗ СЖАТИЯ";
    Console.WriteLine($"===== {mode} =====\n");

    IDesignAssistantAgent agent = new DesignAssistantAgent(
        llmClient,
        new InMemoryChatHistoryStore(),
        DesignerAssistantPrompt.Text,
        options);
    await agent.InitializeAsync(cancellationToken);
    await agent.SetCompressionEnabledAsync(compressionEnabled, cancellationToken);

    long totalBilledTokens = 0;
    long totalSentHistoryTokens = 0;
    var maxContextTokens = 0;
    AgentResponse? lastResponse = null;

    for (var index = 0; index < ComparisonScenario.Questions.Count; index++)
    {
        var question = ComparisonScenario.Questions[index];
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[{index + 1}/15] Вы: {question}");
        Console.ResetColor();

        lastResponse = await agent.AskAsync(question, cancellationToken);
        totalBilledTokens +=
            lastResponse.ModelResponse.Usage.BilledTokens +
            lastResponse.CompressionBilledTokens;
        totalSentHistoryTokens += lastResponse.SentHistoryTokens;
        maxContextTokens = Math.Max(
            maxContextTokens,
            lastResponse.ModelResponse.Usage.ContextTokens);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Помощник: {lastResponse.ModelResponse.Content}\n");
        Console.ResetColor();
        Console.WriteLine(
            $"Контекст: ≈{lastResponse.ModelResponse.Usage.ContextTokens:N0}; " +
            $"полная история: ≈{lastResponse.FullHistoryTokens:N0}; " +
            $"отправлено: ≈{lastResponse.SentHistoryTokens:N0}; " +
            $"к оплате накопительно: {totalBilledTokens:N0}\n");
    }

    return new ComparisonRunResult(
        compressionEnabled,
        totalBilledTokens,
        totalSentHistoryTokens,
        lastResponse!.FullHistoryTokens,
        lastResponse.SentHistoryTokens,
        maxContextTokens,
        lastResponse.ModelResponse.Content);
}

static void PrintComparison(
    ComparisonRunResult withoutCompression,
    ComparisonRunResult withCompression,
    AppOptions options)
{
    var savedBilledTokens = withoutCompression.TotalBilledTokens -
                            withCompression.TotalBilledTokens;
    var savedContextTokens = withoutCompression.TotalSentHistoryTokens -
                             withCompression.TotalSentHistoryTokens;
    var costWithout = withoutCompression.TotalBilledTokens *
                      options.PricePerMillionTokens / 1_000_000m;
    var costWith = withCompression.TotalBilledTokens *
                   options.PricePerMillionTokens / 1_000_000m;

    Console.WriteLine("===== ИТОГОВОЕ СРАВНЕНИЕ =====");
    Console.WriteLine(
        $"Без сжатия: отправлено истории ≈{withoutCompression.TotalSentHistoryTokens:N0}, " +
        $"к оплате {withoutCompression.TotalBilledTokens:N0}, " +
        $"стоимость ≈{costWithout:0.000000} руб.");
    Console.WriteLine(
        $"Со сжатием: отправлено истории ≈{withCompression.TotalSentHistoryTokens:N0}, " +
        $"к оплате {withCompression.TotalBilledTokens:N0}, " +
        $"стоимость ≈{costWith:0.000000} руб.");
    Console.WriteLine($"Экономия переданного контекста: ≈{savedContextTokens:N0} токенов.");
    Console.WriteLine($"Разница тарифицируемого расхода: {savedBilledTokens:N0} токенов.\n");
    Console.WriteLine("Контроль качества: сравните два ответа на вопрос 15.");
    Console.WriteLine($"\nБез сжатия:\n{withoutCompression.FinalAnswer}");
    Console.WriteLine($"\nСо сжатием:\n{withCompression.FinalAnswer}\n");
}

static void PrintCompressionStatus(CompressionStatus status)
{
    Console.WriteLine(
        $"Сжатие истории: {(status.IsEnabled ? "включено" : "выключено")}.");
    Console.WriteLine(
        $"Свёрнуто сообщений: {status.SummarizedMessageCount}; " +
        $"хранятся дословно: {status.RecentMessageCount}.");
    if (status.CompressionBilledTokens > 0)
    {
        Console.WriteLine(
            $"На обновление summary потрачено: {status.CompressionBilledTokens} токенов.\n");
    }
    else
    {
        Console.WriteLine();
    }
}

static void PrintWelcome(string model, string databasePath)
{
    Console.WriteLine("Помощник проектировщика");
    Console.WriteLine($"Модель: {model}");
    Console.WriteLine($"История: {databasePath}");
    Console.WriteLine("Введите /help для просмотра команд.\n");
}

static void PrintHelp()
{
    Console.WriteLine("Команды:");
    Console.WriteLine("  /clear  очистить историю текущего запуска");
    Console.WriteLine("  /compression on      включить сжатие истории");
    Console.WriteLine("  /compression off     выключить сжатие истории");
    Console.WriteLine("  /compression status  показать состояние сжатия");
    Console.WriteLine("  /compare  автоматически сравнить 15 вопросов в двух режимах");
    Console.WriteLine("  /exit   завершить работу");
    Console.WriteLine("  /help   показать справку\n");
}
