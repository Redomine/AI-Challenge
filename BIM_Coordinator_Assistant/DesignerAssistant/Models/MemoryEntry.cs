namespace DesignerAssistant.Models;

public enum MemoryLayer { Working, LongTerm }

public enum MemorySource { User, RevitMcp, Agent }

public enum EvidenceStatus { Reported, Observed, Hypothesis, Confirmed }

public sealed record MemoryEntry(
    long Id,
    MemoryLayer Layer,
    string Key,
    string Value,
    MemorySource Source,
    EvidenceStatus Status,
    string TaskId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MemorySnapshot(
    IReadOnlyList<ChatMessage> ShortTerm,
    IReadOnlyList<MemoryEntry> Working,
    IReadOnlyList<MemoryEntry> LongTerm,
    string TaskId);
