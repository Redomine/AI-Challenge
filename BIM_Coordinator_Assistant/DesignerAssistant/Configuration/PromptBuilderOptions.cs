namespace DesignerAssistant.Configuration;

public sealed record PromptBuilderOptions(
    bool Enabled,
    string Model,
    int MaxOutputTokens,
    int ProbeTimeoutSeconds)
{
    public const string DefaultModel = "GigaChat-3-Ultra";

    public static PromptBuilderOptions FromEnvironment()
    {
        var enabledValue = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_PROMPT_BUILDER_ENABLED");
        var enabled = !string.Equals(enabledValue, "0", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(enabledValue, "false", StringComparison.OrdinalIgnoreCase);

        var model = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_PROMPT_BUILDER_MODEL");
        if (string.IsNullOrWhiteSpace(model)) model = DefaultModel;

        var maxOutputValue = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_PROMPT_BUILDER_MAX_OUTPUT_TOKENS");
        var maxOutputTokens = int.TryParse(maxOutputValue, out var parsedTokens) && parsedTokens > 0 ? parsedTokens : 600;
        if (maxOutputTokens <= 0) maxOutputTokens = 600;

        var probeTimeoutValue = Environment.GetEnvironmentVariable("DESIGN_ASSISTANT_PROMPT_BUILDER_PROBE_TIMEOUT");
        var probeTimeout = int.TryParse(probeTimeoutValue, out var parsedTimeout) && parsedTimeout > 0 ? parsedTimeout : 15;

        return new PromptBuilderOptions(enabled, model, maxOutputTokens, probeTimeout);
    }

    public static PromptBuilderOptions Disabled() => new(false, DefaultModel, 600, 15);
}