using System.Diagnostics;
using System.Text.Json;
using DesignerAssistant.Models;

namespace DesignerAssistant.Llm;

public sealed class ToolExecutionPolicy(
    IToolProvider provider,
    IReadOnlyCollection<ToolDefinition> currentTools)
{
    private readonly IReadOnlyDictionary<string, ToolDefinition> _tools =
        currentTools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    private readonly HashSet<string> _failedCalls = new(StringComparer.Ordinal);

    public async Task<ToolResultEnvelope> ExecuteAsync(
        string name,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var args = arguments.Clone();
        if (!_tools.TryGetValue(name, out var tool))
            return Failure("unknown_tool", $"Инструмент '{name}' отсутствует в текущем tools/list.");
        if (!TryValidate(arguments, tool.Parameters, out var validationError))
            return Failure("invalid_arguments", validationError);

        var signature = name + "\n" + arguments.GetRawText();
        if (_failedCalls.Contains(signature))
            return Failure("duplicate_failed_call", "Повтор неуспешного вызова с теми же аргументами запрещён.");

        var mutation = ToolCapabilityCatalog.IsWriteTool(tool);
        var confirmationRequired = ToolCapabilityCatalog.RequiresConfirmation(tool);
        var confirmed = false;
        if (confirmationRequired)
        {
            confirmed = provider is IToolConfirmationProvider confirmer &&
                await confirmer.ConfirmAsync(name, arguments, cancellationToken);
            if (!confirmed)
                return Failure("cancelled", "Пользователь отменил вызов; инструмент не запускался.");
        }

        try
        {
            var raw = await provider.InvokeAsync(name, arguments, cancellationToken);
            using var resultDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "null" : raw);
            var result = resultDocument.RootElement.Clone();
            if (IsFailure(result, out var message))
            {
                _failedCalls.Add(signature);
                return Failure("tool_error", message, result);
            }
            return new ToolResultEnvelope(true, name, args, result, null, mutation, confirmed,
                started.ElapsedMilliseconds, IsCompleted(result));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _failedCalls.Add(signature);
            return Failure("tool_exception", exception.Message);
        }

        ToolResultEnvelope Failure(string code, string message, object? details = null) =>
            new(false, name, args, null, new ToolError(code, message, details), Mutation: false,
                Confirmed: false, started.ElapsedMilliseconds, Completed: false);
    }

    private static bool TryValidate(JsonElement arguments, JsonElement schema, out string error)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            error = "Аргументы функции должны быть JSON-объектом.";
            return false;
        }
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString();
                if (string.IsNullOrWhiteSpace(name) || !arguments.TryGetProperty(name, out var value) ||
                    value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                    value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()))
                {
                    error = $"Отсутствует обязательный непустой аргумент '{name}'.";
                    return false;
                }
            }
        }
        if (schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var argument in arguments.EnumerateObject())
            {
                if (!properties.TryGetProperty(argument.Name, out var propertySchema)) continue;
                if (propertySchema.TryGetProperty("type", out var type) && !MatchesType(argument.Value, type))
                {
                    error = $"Аргумент '{argument.Name}' не соответствует типу {type.GetRawText()}.";
                    return false;
                }
                if (propertySchema.TryGetProperty("enum", out var allowed) && allowed.ValueKind == JsonValueKind.Array &&
                    !allowed.EnumerateArray().Any(item => JsonElement.DeepEquals(item, argument.Value)))
                {
                    error = $"Аргумент '{argument.Name}' не входит в допустимый enum.";
                    return false;
                }
            }
        }
        error = "";
        return true;
    }

    private static bool MatchesType(JsonElement value, JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.Array)
            return type.EnumerateArray().Any(item => MatchesType(value, item));
        return type.GetString() switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => true
        };
    }

    private static bool IsFailure(JsonElement result, out string message)
    {
        if (result.ValueKind == JsonValueKind.Object &&
            (result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False ||
             result.TryGetProperty("error", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ||
             result.TryGetProperty("cancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True))
        {
            message = result.TryGetProperty("message", out var text) ? text.GetString() ?? result.GetRawText() : result.GetRawText();
            return true;
        }
        message = "";
        return false;
    }

    private static bool IsCompleted(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("status", out var status)) return true;
        return status.GetString()?.ToLowerInvariant() is not ("queued" or "posted" or "running" or "pending");
    }
}
