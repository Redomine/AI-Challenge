using System.Diagnostics;
using System.Text.Json;
using DesignerAssistant.Models;

namespace DesignerAssistant.Tools;

public sealed class AuditedToolProvider(IToolProvider inner, IToolProvider journal)
    : IToolProvider, IToolConfirmationProvider, IToolOperationCoordinator
{
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    public Task<IReadOnlyList<ToolDefinition>> GetToolsAsync(CancellationToken cancellationToken = default) =>
        inner.GetToolsAsync(cancellationToken);

    public async Task<string> InvokeAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var ok = false;
        var pending = false;
        try
        {
            var result = await inner.InvokeAsync(name, arguments, cancellationToken);
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            ok = !(root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("ok", out var status) && status.ValueKind == JsonValueKind.False ||
                 root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null));
            pending = IsPending(root);
            return result;
        }
        finally
        {
            if (!name.StartsWith("log_", StringComparison.Ordinal))
            {
                using var audit = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    taskId = _sessionId,
                    eventType = "tool_call",
                    source = name,
                    summary = pending ? "pending" : ok ? "completed" : "failed",
                    ok,
                    durationMs = watch.ElapsedMilliseconds
                }));
                try { await journal.InvokeAsync("log_append_event", audit.RootElement.Clone(), CancellationToken.None); }
                catch (Exception exception) { Console.Error.WriteLine($"Audit unavailable: {exception.Message}"); }
            }
        }
    }

    public Task<bool> ConfirmAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default) =>
        inner is IToolConfirmationProvider confirmer
            ? confirmer.ConfirmAsync(name, arguments, cancellationToken)
            : Task.FromResult(false);

    public async Task<string> WaitForCompletionAsync(string name, string initialResult, CancellationToken cancellationToken = default)
    {
        if (inner is not IToolOperationCoordinator coordinator) return initialResult;
        using var initial = JsonDocument.Parse(initialResult);
        if (!IsPending(initial.RootElement))
            return await coordinator.WaitForCompletionAsync(name, initialResult, cancellationToken);
        var watch = Stopwatch.StartNew();
        var ok = false;
        try
        {
            var result = await coordinator.WaitForCompletionAsync(name, initialResult, cancellationToken);
            using var final = JsonDocument.Parse(result);
            var root = final.RootElement;
            ok = !(root.ValueKind == JsonValueKind.Object &&
                (root.TryGetProperty("ok", out var status) && status.ValueKind == JsonValueKind.False ||
                 root.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ||
                 root.TryGetProperty("status", out var state) && state.GetString() == "failed"));
            return result;
        }
        finally
        {
            using var audit = JsonDocument.Parse(JsonSerializer.Serialize(new
            {
                taskId = _sessionId, eventType = "tool_completion", source = name,
                summary = ok ? "completed" : "failed", ok, durationMs = watch.ElapsedMilliseconds
            }));
            try { await journal.InvokeAsync("log_append_event", audit.RootElement, CancellationToken.None); }
            catch (Exception exception) { Console.Error.WriteLine($"Audit unavailable: {exception.Message}"); }
        }
    }

    private static bool IsPending(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object &&
        result.TryGetProperty("status", out var status) &&
        status.ValueKind == JsonValueKind.String &&
        status.GetString() is "queued" or "posted" or "running" or "pending";
}
