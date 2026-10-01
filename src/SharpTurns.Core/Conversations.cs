namespace SharpTurns.Core;

public sealed record Project(long Id, string Name, string WorkingDirectory);

/// <summary>Model and Effort are CLI values; null uses the CLI's own default.</summary>
public sealed record Conversation(long Id, long ProjectId, string Title, string? Model, string? Effort, DateTimeOffset UpdatedAt);

public enum TurnStatus { Running, Completed, Stopped, Failed }

public sealed record ConversationTurn(
    long Id,
    long ConversationId,
    int TurnNumber,
    TurnStatus Status,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<TurnPart> Parts);

/// <summary>Role is "user" or "assistant"; PartType is "text" or "thinking".</summary>
public sealed record TurnPart(int Sequence, string Role, string PartType, string Content);

/// <summary>The app's association only; the CLI owns the native session transcript.</summary>
public sealed record ClaudeCodeSessionState(
    string? SessionId,
    string WorkingDirectory,
    string ContextFingerprint,
    bool InFlight,
    string? PolicyVersion = null);
