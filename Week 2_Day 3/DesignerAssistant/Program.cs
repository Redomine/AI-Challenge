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
        DesignerAssistantPrompt.Text);
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

        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Помощник думает...");
            Console.ResetColor();

            var response = await agent.AskAsync(input, cancellationSource.Token);
            cumulativeBilledTokens += response.Usage.BilledTokens;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("Помощник: ");
            Console.ResetColor();
            Console.WriteLine(response.Content);
            Console.WriteLine();
            PrintTokenUsage(
                response.Usage,
                response.FinishReason,
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
    TokenUsage usage,
    string finishReason,
    long cumulativeBilledTokens,
    AppOptions options)
{
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
    Console.WriteLine("  /exit   завершить работу");
    Console.WriteLine("  /help   показать справку\n");
}
