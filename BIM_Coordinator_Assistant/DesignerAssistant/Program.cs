using System.Text;
using DesignerAssistant.Agent;
using DesignerAssistant.Configuration;
using DesignerAssistant.Diagnostics;
using DesignerAssistant.Llm;
using DesignerAssistant.Memory;
using DesignerAssistant.Models;
using DesignerAssistant.Prompts;
using DesignerAssistant.Revit;
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
    if (args.Contains("--revit-smoke", StringComparer.OrdinalIgnoreCase))
    {
        await using var smokeClient = new RevitMcpClient();
        Console.WriteLine("Targets:");
        Console.WriteLine(await smokeClient.ListTargetsAsync(cancellationSource.Token));
        if (args.Contains("--tools", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("Allowed tools:");
            Console.WriteLine(await smokeClient.DescribeToolsAsync(cancellationSource.Token));
            return;
        }
        var smokeYear = args.SkipWhile(value => !value.Equals("--revit-smoke", StringComparison.OrdinalIgnoreCase)).Skip(1).Select(value => int.TryParse(value, out var year) ? year : 0).FirstOrDefault();
        if (smokeYear is 2022 or 2024)
        {
            Console.WriteLine($"Switch to Revit {smokeYear}:");
            Console.WriteLine(await smokeClient.UseAsync(smokeYear, cancellationSource.Token));
        }
        Console.WriteLine("Current target:");
        Console.WriteLine(await smokeClient.StatusAsync(cancellationSource.Token));
        Console.WriteLine("Selection:");
        Console.WriteLine(await smokeClient.SelectionAsync(cancellationSource.Token));
        return;
    }

    var options = AppOptions.FromEnvironment();
    long cumulativeBilledTokens = 0;
    var toolDiagnosticsEnabled = false;
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    ILlmClient llmClient = new GigaChatClient(httpClient, options);
    IChatHistoryStore historyStore = new SqliteChatHistoryStore(options.DatabasePath);
    IMemoryStore memoryStore = new SqliteMemoryStore(options.DatabasePath);
    await using var revit = new RevitMcpClient(confirmWriteAsync: ConfirmRevitWriteAsync);
    IDesignAssistantAgent agent = new DesignAssistantAgent(
        llmClient,
        historyStore,
        memoryStore,
        DesignerAssistantPrompt.Text,
        options,
        revit);
    await agent.InitializeAsync(cancellationSource.Token);

    PrintWelcome(options.Model, options.DatabasePath);
    PrintRestoredHistory(agent.GetHistory());
    MemoryCapture? pendingMemory = null;

    while (!cancellationSource.IsCancellationRequested)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("Вы [/help — команды]: ");
        Console.ResetColor();
        var input = Console.ReadLine();
        input = input?.TrimStart('\uFEFF');

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
            if (pendingMemory is not null)
            {
                var selectedLayer = input.Trim() switch
                {
                    "1" => MemoryLayer.Working,
                    "2" => MemoryLayer.LongTerm,
                    "3" => (MemoryLayer?)null,
                    _ => throw new ArgumentException("Выберите 1 (рабочая), 2 (долговременная) или 3 (не сохранять).")
                };
                if (selectedLayer is not null)
                {
                    await SaveCapturedMemoryAsync(agent, selectedLayer.Value, pendingMemory.Value, cancellationSource.Token);
                }
                else
                {
                    Console.WriteLine("Запоминание отменено.\n");
                }
                pendingMemory = null;
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
                Console.WriteLine("Диалог очищен. Рабочая и долговременная память сохранены.\n");
                continue;
            }

            if (input.Equals("/history", StringComparison.OrdinalIgnoreCase))
            {
                PrintHistory(agent.GetHistory());
                continue;
            }

            if (input.Equals("/memory", StringComparison.OrdinalIgnoreCase))
            {
                PrintMemory(await agent.GetMemoryAsync(cancellationSource.Token));
                continue;
            }

            if (input.Equals("/memory-test", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Автотест выполнит 5 запросов к GigaChat в изолированной памяти. Рабочая база не изменится.\n");
                var results = await MemoryLayersAutoTest.RunAsync(llmClient, options, cancellationSource.Token);
                PrintMemoryTest(results);
                continue;
            }

            if (input.StartsWith("/debug", StringComparison.OrdinalIgnoreCase))
            {
                var argument = input["/debug".Length..].Trim().ToLowerInvariant();
                toolDiagnosticsEnabled = argument switch
                {
                    "on" => true,
                    "off" => false,
                    "" or "status" => toolDiagnosticsEnabled,
                    _ => throw new ArgumentException("Используйте /debug on, /debug off или /debug status.")
                };
                Console.WriteLine($"Диагностика инструментов: {(toolDiagnosticsEnabled ? "включена" : "выключена")}.\n");
                continue;
            }

            if (input.StartsWith("/remember ", StringComparison.OrdinalIgnoreCase))
            {
                var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 3 || !parts[2].Contains('='))
                {
                    throw new ArgumentException("Используйте /remember work|long ключ=значение.");
                }
                var pair = parts[2];
                var separator = pair.IndexOf('=');
                if (separator < 1) throw new ArgumentException("Укажите ключ=значение.");
                var layer = parts[1].Equals("work", StringComparison.OrdinalIgnoreCase) ? MemoryLayer.Working : parts[1].Equals("long", StringComparison.OrdinalIgnoreCase) ? MemoryLayer.LongTerm : throw new ArgumentException("Слой: work или long.");
                await agent.RememberAsync(layer, pair[..separator].Trim(), pair[(separator + 1)..].Trim(), cancellationSource.Token);
                Console.WriteLine("Запись сохранена.\n");
                continue;
            }

            if (input.Equals("/task complete", StringComparison.OrdinalIgnoreCase))
            {
                await agent.CompleteTaskAsync(cancellationSource.Token);
                Console.WriteLine("Задача завершена; рабочая память очищена.\n");
                continue;
            }

            if (input.StartsWith("/revit", StringComparison.OrdinalIgnoreCase))
            {
                var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var result = parts.Length switch
                {
                    2 when parts[1].Equals("select", StringComparison.OrdinalIgnoreCase) => FormatRevitSessions(await revit.ListSessionsAsync(cancellationSource.Token)),
                    >= 3 when parts[1].Equals("select", StringComparison.OrdinalIgnoreCase) => await revit.SelectSessionAsync(string.Join(' ', parts[2..]), cancellationSource.Token),
                    2 when parts[1].Equals("status", StringComparison.OrdinalIgnoreCase) => await revit.StatusAsync(cancellationSource.Token),
                    2 when parts[1].Equals("selection", StringComparison.OrdinalIgnoreCase) => await revit.SelectionAsync(cancellationSource.Token),
                    _ => throw new ArgumentException("Используйте /revit select [ID|имя], /revit status или /revit selection.")
                };
                if (toolDiagnosticsEnabled) Console.WriteLine("Tool trace: rvt-mcp");
                Console.WriteLine(result + "\n");
                continue;
            }

            var memoryCapture = MemoryPhraseParser.Parse(input);
            if (memoryCapture is not null)
            {
                if (string.IsNullOrWhiteSpace(memoryCapture.Value))
                {
                    throw new ArgumentException("После фразы запоминания укажите, что именно нужно сохранить.");
                }
                if (memoryCapture.NeedsLayerChoice)
                {
                    pendingMemory = memoryCapture;
                    Console.WriteLine("Куда сохранить эту информацию?");
                    Console.WriteLine("  1 — рабочая память текущей задачи");
                    Console.WriteLine("  2 — долговременная память");
                    Console.WriteLine("  3 — не сохранять\n");
                }
                else
                {
                    await SaveCapturedMemoryAsync(agent, memoryCapture.Layer!.Value, memoryCapture.Value, cancellationSource.Token);
                }
                continue;
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("Помощник думает...");
            Console.ResetColor();
            var response = await agent.AskAsync(input, cancellationSource.Token);
            cumulativeBilledTokens += response.ModelResponse.Usage.BilledTokens;
            PrintAnswer(response, cumulativeBilledTokens, options, toolDiagnosticsEnabled);
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

static void PrintAnswer(
    AgentResponse response,
    long cumulativeBilledTokens,
    AppOptions options,
    bool toolDiagnosticsEnabled)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("Помощник: ");
    Console.ResetColor();
    Console.WriteLine(response.ModelResponse.Content);

    foreach (var trace in (response.ToolTraces ?? []).Where(
                 trace => toolDiagnosticsEnabled || IsToolError(trace)))
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine(trace);
        Console.ResetColor();
    }

    var usage = response.ModelResponse.Usage;
    var requestTokens = usage.BilledTokens;
    var requestCost = requestTokens * options.PricePerMillionTokens / 1_000_000m;
    var totalCost = cumulativeBilledTokens * options.PricePerMillionTokens / 1_000_000m;
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine(
        $"Полная история: ≈{response.FullHistoryTokens:N0}; " +
        $"отправлено: ≈{response.SentHistoryTokens:N0}");
    Console.WriteLine(
        $"Ответ: {usage.CompletionTokens:N0}; к оплате: {requestTokens:N0}; " +
        $"причина: {response.ModelResponse.FinishReason}");
    Console.WriteLine(
        $"Стоимость запроса: ≈{requestCost:0.000000} руб.; " +
        $"за запуск: ≈{totalCost:0.000000} руб.\n");
    Console.ResetColor();
}

static bool IsToolError(string trace)
{
    if (!trace.StartsWith("Tool result:", StringComparison.Ordinal)) return false;
    return trace.Contains("\"isError\":true", StringComparison.OrdinalIgnoreCase) ||
           trace.Contains("\"ok\":false", StringComparison.OrdinalIgnoreCase) ||
           trace.Contains("\"error\"", StringComparison.OrdinalIgnoreCase) ||
           trace.Contains("Error:", StringComparison.OrdinalIgnoreCase) ||
           trace.Contains("ошиб", StringComparison.OrdinalIgnoreCase);
}

static async Task SaveCapturedMemoryAsync(
    IDesignAssistantAgent agent,
    MemoryLayer layer,
    string value,
    CancellationToken cancellationToken)
{
    var memory = await agent.GetMemoryAsync(cancellationToken);
    var count = layer == MemoryLayer.Working ? memory.Working.Count : memory.LongTerm.Count;
    var key = $"note_{count + 1:000}";
    await agent.RememberAsync(layer, key, value, cancellationToken);
    Console.WriteLine($"Сохранено в {(layer == MemoryLayer.Working ? "рабочую" : "долговременную")} память: {value}\n");
}

static void PrintWelcome(string model, string databasePath)
{
    Console.WriteLine("Помощник BIM-координатора");
    Console.WriteLine($"Модель: {model}");
    Console.WriteLine($"История: {databasePath}");
    Console.WriteLine("/help — показать все команды.\n");
}

static void PrintRestoredHistory(IReadOnlyList<ChatMessage> history)
{
    if (history.Count == 0)
    {
        Console.WriteLine("Сохранённая история отсутствует.\n");
        return;
    }

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"Восстановлен диалог: {history.Count} сообщений.");
    Console.ResetColor();
    PrintHistory(history);
}

static void PrintHistory(IReadOnlyList<ChatMessage> history)
{
    if (history.Count == 0)
    {
        Console.WriteLine("История пуста.\n");
        return;
    }

    Console.WriteLine("История диалога:");
    foreach (var message in history)
    {
        var label = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
            ? "Вы"
            : "Помощник";
        Console.ForegroundColor = message.Role.Equals(
            "user",
            StringComparison.OrdinalIgnoreCase)
                ? ConsoleColor.Cyan
                : ConsoleColor.Green;
        Console.Write($"{label}: ");
        Console.ResetColor();
        Console.WriteLine(message.Content);
    }

    Console.WriteLine();
}

static void PrintMemory(MemorySnapshot memory)
{
    Console.WriteLine("Слои памяти:");
    Console.WriteLine($"  Краткосрочная: {memory.ShortTerm.Count} сообщений текущего диалога");
    PrintEntries("Рабочая", memory.Working);
    PrintEntries("Долговременная", memory.LongTerm);
    Console.WriteLine();
}

static void PrintEntries(string title, IReadOnlyList<MemoryEntry> entries)
{
    Console.WriteLine($"  {title}: {entries.Count}");
    foreach (var entry in entries) Console.WriteLine($"    {entry.Key} = {entry.Value} [{entry.Source}/{entry.Status}, {entry.UpdatedAt.LocalDateTime:g}]");
}

static void PrintHelp()
{
    Console.WriteLine("Команды:");
    Console.WriteLine("  /help                              показать все команды");
    Console.WriteLine("  /history                           показать восстановленный диалог");
    Console.WriteLine("  /memory                            показать состояние слоёв памяти");
    Console.WriteLine("  /memory-test                       проверить слои памяти через GigaChat");
    Console.WriteLine("  /debug on|off|status               управлять Tool route/trace/result");
    Console.WriteLine("  /remember work <ключ>=<значение>   сохранить в рабочую память");
    Console.WriteLine("  /remember long <ключ>=<значение>   сохранить в долговременную память");
    Console.WriteLine("  /task complete                     завершить задачу и очистить рабочую память");
    Console.WriteLine("  /revit select                      показать сеансы Revit и их ID");
    Console.WriteLine("  /revit select <ID|имя>             выбрать конкретный сеанс");
    Console.WriteLine("  /revit status                      показать активный target");
    Console.WriteLine("  /revit selection                   прочитать выделенные элементы");
    Console.WriteLine("  /clear                             очистить только текущий диалог");
    Console.WriteLine("  /exit                              завершить работу");
    Console.WriteLine();
}

static string FormatRevitSessions(IReadOnlyList<RevitSession> sessions)
{
    if (sessions.Count == 0) return "Запущенные сеансы Revit не найдены.";
    var lines = new List<string> { "Доступные сеансы Revit:" };
    lines.AddRange(sessions.Select(session =>
        $"  ID {session.ProcessId}: {session.DisplayName} " +
        (session.IsRoutable ? "[доступен]" : "[не опубликован rvt-mcp]")));
    return string.Join(Environment.NewLine, lines);
}

static void PrintMemoryTest(IReadOnlyList<MemoryTestStep> results)
{
    Console.WriteLine("===== ПРОВЕРКА MEMORY LAYERS =====");
    foreach (var result in results)
    {
        Console.ForegroundColor = result.Passed ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"[{(result.Passed ? "PASS" : "FAIL")}] {result.Name}");
        Console.ResetColor();
        Console.WriteLine($"Слои: short={result.Memory.ShortTerm.Count}, work={result.Memory.Working.Count}, long={result.Memory.LongTerm.Count}");
        foreach (var entry in result.Memory.Working) Console.WriteLine($"  work: {entry.Key}={entry.Value}");
        foreach (var entry in result.Memory.LongTerm) Console.WriteLine($"  long: {entry.Key}={entry.Value}");
        Console.WriteLine($"Ожидание: {result.Expectation}");
        Console.WriteLine($"Ответ: {result.Answer}\n");
    }
    Console.WriteLine($"Итог: {results.Count(result => result.Passed)}/{results.Count} проверок пройдено.\n");
}

static Task<bool> ConfirmRevitWriteAsync(string proposal)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("\nЗапрошена транзакция в Revit:");
    Console.WriteLine(proposal);
    Console.Write("Выполнить эту операцию? [да/нет, по умолчанию нет]: ");
    Console.ResetColor();
    var answer = Console.ReadLine()?.Trim();
    var approved = answer?.Equals("да", StringComparison.OrdinalIgnoreCase) == true;
    Console.WriteLine(approved ? "Транзакция разрешена.\n" : "Транзакция отменена.\n");
    return Task.FromResult(approved);
}
