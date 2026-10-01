using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>One item in a turn's response, in display order.</summary>
public abstract class TurnItemViewModel : ObservableObject;

/// <summary>Assistant text: plain while it streams, rendered as Markdown once complete.</summary>
public sealed partial class TextItemViewModel(string text, bool isStreaming) : TurnItemViewModel
{
    [ObservableProperty]
    private string _text = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRendered))]
    private bool _isStreaming = isStreaming;

    public bool IsRendered => !IsStreaming;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    [NotifyPropertyChangedFor(nameof(InputText))]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsSucceeded))]
    private ToolCallRecord _tool;

    [ObservableProperty]
    private bool _isExpanded;

    public ToolItemViewModel(ToolCallRecord tool) => _tool = tool;

    public string Id => Tool.Id;

    public string Summary => Summarize(Tool);

    public string InputText => FormatInput(Tool.Input);

    public string ResultText => Tool.Result ?? "";

    public bool HasResult => !string.IsNullOrEmpty(Tool.Result);

    public bool IsWaiting => Tool.Status.StartsWith("Waiting", StringComparison.Ordinal);

    public bool IsRunning => !IsWaiting && Tool.Status is "Running" or "Approved";

    public bool IsSucceeded => !Tool.IsError && !IsWaiting && !IsRunning;

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
