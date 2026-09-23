using System.Text.Json;

namespace DesignerAssistant.Models;

public sealed record ToolError(string Code, string Message, object? Details = null);

public sealed record ToolResultEnvelope(
    bool Ok,
    string Tool,
    JsonElement Arguments,
    JsonElement? Result,
    ToolError? Error,
    bool Mutation,
    bool Confirmed,
    long DurationMs,
    bool Completed)
{
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
