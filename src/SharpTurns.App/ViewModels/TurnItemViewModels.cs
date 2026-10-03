using System.Globalization;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>One item in a turn's response, in display order.</summary>
public abstract class TurnItemViewModel : ObservableObject;

/// <summary>
/// Assistant text: plain while it streams, rendered as Markdown once complete. A compressed turn's summary is one
/// text item: its Work Summary section is a collapsible card above the rest. The Final
/// Response heading is hidden; a Partial Response heading stays and is highlighted.
/// </summary>
public sealed partial class TextItemViewModel(string text, bool isStreaming, bool isSummary = false) : TurnItemViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWorkSummary), nameof(WorkSummaryHeaderLabel), nameof(WorkSummaryText), nameof(PrimaryText))]
    private string _text = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRendered))]
    private bool _isStreaming = isStreaming;

    /// <summary>Whether the Work Summary card is open; collapsed by default.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public bool IsRendered => !IsStreaming;

    public bool IsSummary { get; } = isSummary;

    public bool HasWorkSummary => TrySplitWorkSummary(out _, out _, out _);

    /// <summary>The Work Summary heading without its "## ", including the tool call count and reduction.</summary>
    public string WorkSummaryHeaderLabel => TrySplitWorkSummary(out var header, out _, out _) ? header : "";

    public string WorkSummaryText => TrySplitWorkSummary(out _, out var body, out _) ? body : "";

    /// <summary>The text shown outside the Work Summary card: all of it, or a summary's response section.</summary>
    public string PrimaryText => TrySplitWorkSummary(out _, out _, out var remainder) ? remainder : Text;

    // A summary leads with its Work Summary section, which runs to the response heading.
    private bool TrySplitWorkSummary(out string header, out string body, out string remainder)
    {
        const string heading = TurnCompression.WorkSummaryHeading;
        header = body = "";
        remainder = Text;
        if (!IsSummary || !Text.StartsWith(heading, StringComparison.Ordinal)) return false;
        var headerEnd = Text.IndexOf('\n');
        var headerLine = (headerEnd < 0 ? Text : Text[..headerEnd]).TrimEnd('\r');
        if (headerLine.Length > heading.Length && !headerLine.StartsWith(heading + " (", StringComparison.Ordinal)) return false;
        var bodyStart = headerEnd < 0 ? Text.Length : headerEnd + 1;
        var boundary = new[] { TurnCompression.FinalResponseHeading, TurnCompression.PartialResponseHeading }
            .Select(response => Text.IndexOf("\n" + response, bodyStart, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(Text.Length)
            .Min();
        header = headerLine[3..];
        body = Text[bodyStart..boundary].Trim();
        remainder = Text[boundary..].TrimStart();
        // The Final heading stays in the stored and replayed summary but is hidden here; the Partial heading stays visible.
        if (remainder.StartsWith(TurnCompression.FinalResponseHeading, StringComparison.Ordinal))
            remainder = remainder[TurnCompression.FinalResponseHeading.Length..].TrimStart();
        return true;
    }
}

/// <summary>A message the user sent while the turn ran.</summary>
public sealed class UserMessageItemViewModel(string text) : TurnItemViewModel
{
    public string Text { get; } = text;
}

public sealed partial class QuestionItemViewModel(QuestionRecord question, bool isWaiting) : TurnItemViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnswerText))]
    [NotifyPropertyChangedFor(nameof(IsDeclined))]
    private QuestionRecord _question = question;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnswerText))]
    [NotifyPropertyChangedFor(nameof(IsDeclined))]
    private bool _isWaiting = isWaiting;

    public string AnswerText => IsWaiting ? "Waiting for your answer…" : Question.Answer ?? "No answer; the question was declined.";

    public bool IsDeclined => !IsWaiting && Question.Answer is null;
}

/// <summary>A native tool call: a one-line header that expands to its input and result.</summary>
public sealed partial class ToolItemViewModel : TurnItemViewModel
{
    private const int MaxSummaryChars = 160;

    // The expanded card shows this many lines of the input and the result until Show Full.
    private const int DisplayLineLimit = 20;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(InputText))]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(InputDisplayText))]
    [NotifyPropertyChangedFor(nameof(ResultDisplayText))]
    [NotifyPropertyChangedFor(nameof(HasTruncatedInput))]
    [NotifyPropertyChangedFor(nameof(HasTruncatedResult))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsSucceeded))]
    [NotifyPropertyChangedFor(nameof(HasCacheUsage))]
    [NotifyPropertyChangedFor(nameof(CacheUsageBadge))]
    [NotifyPropertyChangedFor(nameof(CacheUsageToolTip))]
    private ToolCallRecord _tool;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputDisplayText))]
    [NotifyPropertyChangedFor(nameof(InputToggleLabel))]
    private bool _showFullInput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultDisplayText))]
    [NotifyPropertyChangedFor(nameof(ResultToggleLabel))]
    private bool _showFullResult;

    public ToolItemViewModel(ToolCallRecord tool, int number = 0)
    {
        _tool = tool;
        Number = number;
    }

    public string Id => Tool.Id;

    /// <summary>The call's 1-based position among the turn's tool cards; 0 when unnumbered.</summary>
    public int Number { get; }

    public bool HasNumber => Number > 0;

    public string NumberLabel => HasNumber ? $"{Number.ToString(CultureInfo.CurrentCulture)}." : "";

    public string Summary => Summarize(Tool);

    public string InputText => FormatInput(Tool.Input);

    public string ResultText => Tool.Result ?? "";

    public bool HasResult => !string.IsNullOrEmpty(Tool.Result);

    public string InputDisplayText => ShowFullInput ? InputText : FirstLines(InputText);

    public string ResultDisplayText => ShowFullResult ? ResultText : FirstLines(ResultText);

    public bool HasTruncatedInput => HasMoreThanLineLimit(InputText);

    public bool HasTruncatedResult => HasMoreThanLineLimit(ResultText);

    public string InputToggleLabel => ShowFullInput ? "Show Less" : "Show Full...";

    public string ResultToggleLabel => ShowFullResult ? "Show Less" : "Show Full...";

    public bool IsWaiting => Tool.Status.StartsWith("Waiting", StringComparison.Ordinal);

    public bool IsRunning => !IsWaiting && Tool.Status is "Running" or "Approved";

    public bool IsSucceeded => !Tool.IsError && !IsWaiting && !IsRunning;

    // The cache hit rate of the model request that made the call.
    public bool HasCacheUsage => Tool is { RequestInputTokens: > 0, RequestCachedTokens: not null };

    public string CacheUsageBadge => Tool is { RequestInputTokens: > 0 and var input, RequestCachedTokens: { } cached }
        ? string.Create(CultureInfo.CurrentCulture, $"Cache {100d * cached / input:0.0}%")
        : "";

    public string CacheUsageToolTip => Tool is { RequestInputTokens: > 0 and var input, RequestCachedTokens: { } cached }
        ? string.Create(CultureInfo.CurrentCulture,
            $"The model request that made this call: cached {cached:N0} / {input:N0} input tokens. Calls made by the same request share it.")
        : "";

    private static string Summarize(ToolCallRecord tool)
    {
        if (TryParseObject(tool.Input) is not { } input) return "";
        string? Field(string name) => input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
        var summary = tool.Name switch
        {
            "Read" or "Write" or "Edit" => Field("file_path"),
            "NotebookEdit" => Field("notebook_path"),
            "Bash" or "PowerShell" => Field("description") ?? Field("command"),
            "Glob" or "Grep" => Field("pattern"),
            "WebFetch" => Field("url"),
            "WebSearch" => Field("query"),
            _ => null,
        } ?? "";
        summary = string.Join(' ', summary.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return summary.Length <= MaxSummaryChars ? summary : summary[..MaxSummaryChars] + "…";
    }

    // One line per field; multi-line strings such as edits keep their line breaks instead of JSON escapes.
    private static string FormatInput(string? input)
    {
        if (TryParseObject(input) is not { } json) return input ?? "";
        var text = new StringBuilder();
        foreach (var property in json.EnumerateObject())
        {
            if (text.Length > 0) text.Append('\n');
            text.Append(property.Name).Append(':');
            if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                text.Append(value.Contains('\n') ? "\n" + value : " " + value);
            else text.Append(' ').Append(property.Value.GetRawText());
        }
        return text.ToString();
    }

    private static string FirstLines(string text) => HasMoreThanLineLimit(text)
        ? string.Join('\n', text.ReplaceLineEndings("\n").Split('\n').Take(DisplayLineLimit))
        : text;

    private static bool HasMoreThanLineLimit(string text)
    {
        var lines = 1;
        foreach (var character in text)
            if (character == '\n' && ++lines > DisplayLineLimit) return true;
        return false;
    }

    private static JsonElement? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }
}
