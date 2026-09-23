using System.Text.Json;

namespace DesignerAssistant.Revit;

public sealed class RevitOperationCoordinator(
    Func<string, IReadOnlyDictionary<string, object?>?, CancellationToken, Task<string>> invokeAsync,
    TimeSpan? pollInterval = null)
{
    private readonly TimeSpan _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);

    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    public async Task<string> WaitForCompletionAsync(
        string toolName,
        string initialResult,
        CancellationToken cancellationToken = default)
    {
        if (!TryReadPendingOperation(initialResult, out var runId)) return initialResult;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            while (true)
            {
                await Task.Delay(_pollInterval, timeout.Token);
                var state = await invokeAsync(
                    "revit_custom_get_bridge_operation",
                    new Dictionary<string, object?> { ["runId"] = runId },
                    timeout.Token);
                if (!TryReadStatus(state, out var status)) return Failure(toolName, runId, "Bridge вернул результат без статуса.", state);

                switch (status)
                {
                    case "queued":
                    case "posted":
                    case "running":
                    case "pending":
                        continue;
                    case "completed":
                        return UnwrapResult(state);
                    case "failed":
                    case "cancelled":
                        return Failure(toolName, runId, ReadMessage(state, status), state);
                    default:
                        return Failure(toolName, runId, $"Bridge вернул неизвестный статус '{status}'.", state);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(toolName, runId, $"Операция Revit не завершилась за {Timeout.TotalMinutes:g} мин.");
        }
    }

    private static bool TryReadPendingOperation(string json, out string runId)
    {
        runId = "";
        if (!TryParse(json, out var root) || !TryReadStatus(root, out var status) ||
            status is not ("queued" or "posted" or "running" or "pending") ||
            !root.TryGetProperty("runId", out var id) || id.ValueKind != JsonValueKind.String) return false;
        runId = id.GetString() ?? "";
        return runId.Length > 0;
    }

    private static bool TryReadStatus(string json, out string status)
    {
        status = "";
        return TryParse(json, out var root) && TryReadStatus(root, out status);
    }

    private static bool TryReadStatus(JsonElement root, out string status)
    {
        status = "";
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var value) || value.ValueKind != JsonValueKind.String) return false;
        status = value.GetString()?.ToLowerInvariant() ?? "";
        return status.Length > 0;
    }

    private static string UnwrapResult(string state)
    {
        using var document = JsonDocument.Parse(state);
        var root = document.RootElement;
        return root.TryGetProperty("result", out var result) && result.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? result.GetRawText()
            : state;
    }

    private static string ReadMessage(string state, string fallback)
    {
        using var document = JsonDocument.Parse(state);
        var root = document.RootElement;
        foreach (var property in new[] { "message", "error" })
            if (root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? fallback;
        return fallback;
    }

    private static string Failure(string toolName, string runId, string message, string? details = null) =>
        JsonSerializer.Serialize(new { ok = false, status = "failed", toolName, runId, message, details });

    private static bool TryParse(string json, out JsonElement root)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }
}
