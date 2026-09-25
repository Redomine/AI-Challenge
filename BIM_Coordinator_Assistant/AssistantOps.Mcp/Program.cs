using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

if (args is ["--smoke-email"])
{
    var result = await new OpsState().SendNotificationAsync(
        "BIM Coordinator: проверка уведомлений",
        $"Тестовая отправка из ops-mcp. Время: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}.",
        CancellationToken.None);
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}

if (args.Length != 1 || args[0] is not ("log" or "ops"))
    throw new ArgumentException("Expected server mode: log or ops.");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
if (args[0] == "log")
{
    builder.Services.AddSingleton<AuditJournal>();
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<LogTools>();
}
else
{
    builder.Services.AddSingleton<OpsState>();
    builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<OpsTools>();
}
await builder.Build().RunAsync();
