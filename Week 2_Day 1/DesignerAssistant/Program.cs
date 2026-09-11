using System.Text;
using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Llm;
using DesignerAssistant.Prompts;

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

    using var httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    ILlmClient llmClient = new GigaChatClient(httpClient, options);
    IDesignAssistantAgent agent = new DesignAssistantAgent(
        llmClient,
        DesignerAssistantPrompt.Text);

    PrintWelcome(options.Model);

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
            agent.ClearHistory();
            Console.WriteLine("Контекст текущего диалога очищен.\n");
            continue;
        }

        try
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Помощник думает...");
            Console.ResetColor();

            var answer = await agent.AskAsync(input, cancellationSource.Token);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("Помощник: ");
            Console.ResetColor();
            Console.WriteLine(answer);
            Console.WriteLine();
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

static void PrintWelcome(string model)
{
    Console.WriteLine("Помощник проектировщика");
    Console.WriteLine($"Модель: {model}");
    Console.WriteLine("Введите /help для просмотра команд.\n");
}

static void PrintHelp()
{
    Console.WriteLine("Команды:");
    Console.WriteLine("  /clear  очистить историю текущего запуска");
    Console.WriteLine("  /exit   завершить работу");
    Console.WriteLine("  /help   показать справку\n");
}
