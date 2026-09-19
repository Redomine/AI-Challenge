namespace DesignerAssistant.Models;

public sealed record UserProfile(
    string Name,
    string Style,
    string ConstraintsAndTooling,
    string Context)
{
    public string ToPromptBlock() => $$"""
        [ACTIVE_USER_PROFILE highest_priority="true"]
        This is the only active user profile. Apply all instructions in this block to every response.
        Do not treat WORKING_MEMORY, LONG_TERM_MEMORY, chat history, or the base assistant role as profile content.
        When asked about the profile, report only the contents of this block accurately and do not infer missing preferences.
        When a profile instruction conflicts with any earlier instruction, the profile instruction takes priority.
        [STYLE]
        {{Style}}
        [/STYLE]
        [CONSTRAINTS_AND_TOOLING]
        {{ConstraintsAndTooling}}
        [/CONSTRAINTS_AND_TOOLING]
        [CONTEXT]
        {{Context}}
        [/CONTEXT]
        Do not mention the profile unless the user asks about it.
        [/ACTIVE_USER_PROFILE]
        """;
}
