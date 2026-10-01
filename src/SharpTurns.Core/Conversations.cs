using System.Text.Json;

namespace SharpTurns.Core;

/// <summary>Color is the project's border accent as #RRGGBB (see ProjectColor).</summary>
public sealed record Project(long Id, string Name, string WorkingDirectory, string Color = ProjectColor.Default);

/// <summary>Opaque RGB project accents, as in the Workbench.</summary>
public static class ProjectColor
{
    public const string Default = "#7CFF2B";

    public static bool IsValid(string? value) => value is { Length: 7 }
        && value[0] == '#'
        && value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    public static string Normalize(string? value) => IsValid(value?.Trim())
        ? value!.Trim().ToUpperInvariant()
        : Default;
}

/// <summary>
/// Model, Effort, and OutputStyle are CLI values; null uses the CLI's own default. AutoSummarize compresses each
/// turn that completes.
/// </summary>
public sealed record Conversation(long Id, long ProjectId, string Title, string? Model, string? Effort, DateTimeOffset UpdatedAt,
    string? OutputStyle = null, bool AutoSummarize = false);

public enum TurnStatus { Running, Completed, Stopped, Failed }

/// <summary>
/// A hidden (not hydrated) turn stays out of the replayed context. A compressed turn replays its user inputs and
/// Summary (a work summary plus the verbatim response; see TurnCompression) instead of its assistant text.
/// SummaryModel is the model alias that wrote the summary; null when no model was needed.
/// </summary>
public sealed record ConversationTurn(
    long Id,
    long ConversationId,
    int TurnNumber,
    TurnStatus Status,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<TurnPart> Parts,
    TurnUsage? Usage = null,
    string? Model = null,
    bool IsHydrated = true,
    string? Summary = null,
    string? SummaryModel = null)
{
    public bool IsCompressed => Summary is not null;
}

/// <summary>
/// Role is "user" or "assistant". The first user text part is the prompt; later ones are messages sent while the
/// turn ran. Text and thinking parts hold plain text; tool, question, and image parts hold JSON (see TurnParts).
/// </summary>
public sealed record TurnPart(int Sequence, string Role, string PartType, string Content);

/// <summary>
/// Input includes cache reads and writes, summed over the turn's model requests. Context is the last request's input.
/// Requests and the first request's tokens are null for turns saved before they were recorded.
/// </summary>
public sealed record TurnUsage(long InputTokens, long CachedInputTokens, long OutputTokens, long? ContextTokens,
    int? Requests = null, long? FirstRequestInputTokens = null, long? FirstRequestCachedTokens = null);

/// <summary>A native tool call the CLI ran, saved for display only; never replayed.</summary>
public sealed record ToolCallRecord(string Id, string Name, string? Input, string? Result, string Status, bool IsError);

/// <summary>An AskUserQuestion question as shown (Markdown) and the user's answer; null when declined.</summary>
public sealed record QuestionRecord(string Question, string? Answer);

/// <summary>An attached image; Data is base64.</summary>
public sealed record ImageAttachment(string FileName, string MediaType, string Data);

public static class TurnParts
{
    public const string Text = "text";
    public const string Thinking = "thinking";
    public const string Tool = "tool";
    public const string Question = "question";
    public const string Image = "image";

    public static TurnPart FromTool(int sequence, ToolCallRecord tool) =>
        new(sequence, "assistant", Tool, JsonSerializer.Serialize(tool));

    public static TurnPart FromQuestion(int sequence, QuestionRecord question) =>
        new(sequence, "assistant", Question, JsonSerializer.Serialize(question));

    public static TurnPart FromImage(int sequence, ImageAttachment image) =>
        new(sequence, "user", Image, JsonSerializer.Serialize(image));

    public static ToolCallRecord ReadTool(TurnPart part) => Read<ToolCallRecord>(part, Tool);

    public static QuestionRecord ReadQuestion(TurnPart part) => Read<QuestionRecord>(part, Question);

    public static ImageAttachment ReadImage(TurnPart part) => Read<ImageAttachment>(part, Image);

    private static T Read<T>(TurnPart part, string type) => part.PartType == type
        ? JsonSerializer.Deserialize<T>(part.Content) ?? throw new InvalidDataException($"Turn part {part.Sequence} is empty.")
        : throw new ArgumentException($"Turn part {part.Sequence} is not a {type} part.", nameof(part));
}

/// <summary>The app's association only; the CLI owns the native session transcript.</summary>
public sealed record ClaudeCodeSessionState(
    string? SessionId,
    string WorkingDirectory,
    string ContextFingerprint,
    bool InFlight,
    string? PolicyVersion = null);
