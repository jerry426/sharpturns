using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpTurns.Core;

namespace SharpTurns.App.Services;

public enum TurnExportFormat { Markdown, Text, Docx }

/// <summary>
/// FullContent exports a compressed turn's saved parts instead of what the replay sends. A conversation export leaves
/// hidden turns out unless IncludeHiddenTurns is set.
/// </summary>
public sealed record TurnExportOptions(
    bool IncludeToolDetails = true,
    bool IncludeMetadata = true,
    bool FullContent = false,
    bool FullToolResults = false,
    bool IncludeHiddenTurns = false);

/// <summary>A turn or a whole conversation as Markdown or plain text, as the Workbench's export formats them.</summary>
public static partial class TurnExportFormatter
{
    private const int ToolInputStringLimit = 500;
    private const int ToolResultLineLimit = 10;

    public static string FormatMarkdown(ConversationTurn turn, Conversation conversation, Project project, TurnExportOptions options)
    {
        var builder = new StringBuilder();
        AppendTurn(builder, turn, (conversation, project), options);
        return TrimTrailingWhitespace(builder);
    }

    public static string FormatMarkdown(IReadOnlyList<ConversationTurn> turns, Conversation conversation, Project project,
        TurnExportOptions options)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(string.IsNullOrWhiteSpace(conversation.Title) ? "Conversation Export" : conversation.Title.Trim());
        builder.AppendLine();
        var exported = turns.Where(t => options.IncludeHiddenTurns || t.IsHydrated).OrderBy(t => t.TurnNumber).ToList();
        if (options.IncludeMetadata) AppendConversationMetadata(builder, conversation, project, exported, turns.Count);
        foreach (var turn in exported) AppendTurn(builder, turn, null, options);
        return TrimTrailingWhitespace(builder);
    }

    public static string ToText(string markdown)
    {
        var text = CodeFenceRegex().Replace(markdown.ReplaceLineEndings("\n"), match => match.Groups[1].Value);
        text = HeadingRegex().Replace(text, string.Empty);
        text = BlockquoteRegex().Replace(text, string.Empty);
        text = BoldItalicRegex().Replace(text, "$1$2");
        text = InlineCodeRegex().Replace(text, "$1");
        text = LinkRegex().Replace(text, "$1");
        text = HorizontalRuleRegex().Replace(text, string.Empty);
        return text.Trim();
    }

    public static string Extension(TurnExportFormat format) => format switch
    {
        TurnExportFormat.Docx => ".docx",
        TurnExportFormat.Text => ".txt",
        _ => ".md",
    };

    public static string SanitizeFileName(string title)
    {
        var safe = new string(title.Select(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or ' ' ? ch : '_').ToArray()).Trim();
        return safe.Length == 0 ? "conversation" : safe[..Math.Min(safe.Length, 50)];
    }

    /// <summary>MacDown needs a blank line before a list that follows a paragraph; items of one list stay together.</summary>
    public static string FixMacDownListSpacing(string text)
    {
        var result = new List<string>();
        var previousWasBlank = true;
        var previousWasListItem = false;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            var isListItem = UnorderedListRegex().IsMatch(trimmed) || OrderedListRegex().IsMatch(trimmed);
            if (isListItem && !previousWasBlank && !previousWasListItem) result.Add(string.Empty);
            result.Add(line);
            previousWasBlank = string.IsNullOrWhiteSpace(line);
            previousWasListItem = isListItem;
        }
        return string.Join("\n", result);
    }

    // A single turn's export names its conversation in the metadata; a conversation export lists it once at the top.
    private static void AppendTurn(StringBuilder builder, ConversationTurn turn, (Conversation Conversation, Project Project)? owner,
        TurnExportOptions options)
    {
        builder.AppendLine(new string('▪', 70)).AppendLine();
        builder.Append("## 🔹 Turn ").AppendLine(turn.TurnNumber.ToString(CultureInfo.InvariantCulture)).AppendLine();
        if (options.IncludeMetadata) AppendTurnMetadata(builder, turn, owner);

        var parts = turn.Parts.Where(p => p.PartType != TurnParts.Thinking).OrderBy(p => p.Sequence).ToArray();
        var summaryView = turn.IsCompressed && !options.FullContent;
        if (summaryView)
        {
            builder.AppendLine("**[Compressed turn: user inputs verbatim, then the summary Claude sees in place of the rest]**");
            builder.AppendLine();
        }
        else if (turn.IsCompressed)
        {
            builder.AppendLine("**[Compressed turn shown in full]**").AppendLine();
        }

        var prompt = parts.FirstOrDefault(p => p is { Role: "user", PartType: TurnParts.Text });
        var images = parts.Where(p => p.PartType == TurnParts.Image).Select(TurnParts.ReadImage).ToArray();
        AppendSection(builder, "### User", prompt?.Content);
        AppendImageDescriptors(builder, images);
        foreach (var part in parts.Where(p => p != prompt))
        {
            switch (part)
            {
                case { PartType: TurnParts.Text, Role: "user" }:
                    AppendSection(builder, "### User (sent during the turn)", part.Content);
                    break;
                case { PartType: TurnParts.Question }:
                    var question = TurnParts.ReadQuestion(part);
                    AppendSection(builder, "### ❓ Question", question.Question);
                    AppendSection(builder, "### User (answer)", question.Answer ?? "No answer; the question was declined.");
                    break;
                case { PartType: TurnParts.Text, Role: "assistant" } when !summaryView:
                    AppendSection(builder, "### Assistant", part.Content);
                    break;
                case { PartType: TurnParts.Tool } when !summaryView && options.IncludeToolDetails:
                    AppendTool(builder, TurnParts.ReadTool(part), options.FullToolResults);
                    break;
            }
        }
        if (summaryView) AppendSection(builder, "### Summary", turn.Summary);
    }

    private static void AppendConversationMetadata(StringBuilder builder, Conversation conversation, Project project,
        IReadOnlyList<ConversationTurn> exported, int totalTurns)
    {
        builder.AppendLine($"> **Conversation ID:** {conversation.Id}");
        builder.AppendLine($"> **Project:** {project.Name}");
        if (!string.IsNullOrWhiteSpace(conversation.Model)) builder.AppendLine($"> **Model:** {conversation.Model}");
        builder.AppendLine($"> **Updated:** {FormatTimestamp(conversation.UpdatedAt)}");
        builder.AppendLine(string.Create(CultureInfo.CurrentCulture, $"> **Turns:** {exported.Count:N0}"));
        if (exported.Count != totalTurns) builder.AppendLine(string.Create(CultureInfo.CurrentCulture, $"> **Total Turns:** {totalTurns:N0}"));
        var tokens = exported.Sum(t => t.Usage is { } usage ? usage.InputTokens + usage.OutputTokens : 0);
        if (tokens > 0) builder.AppendLine(string.Create(CultureInfo.CurrentCulture, $"> **Tokens:** {tokens:N0}"));
        builder.AppendLine();
    }

    private static void AppendTurnMetadata(StringBuilder builder, ConversationTurn turn, (Conversation Conversation, Project Project)? owner)
    {
        builder.AppendLine($"> **Turn ID:** {turn.Id}");
        if (owner is var (conversation, project))
        {
            builder.AppendLine($"> **Conversation ID:** {conversation.Id}");
            builder.AppendLine($"> **Project:** {project.Name}");
            builder.AppendLine($"> **Conversation:** {conversation.Title}");
        }
        if (!string.IsNullOrWhiteSpace(turn.Model)) builder.AppendLine($"> **Model:** {turn.Model}");
        builder.AppendLine($"> **Timestamp:** {FormatTimestamp(turn.CreatedAt)}");
        if (turn.Usage is { } usage && usage.InputTokens + usage.OutputTokens > 0)
            builder.AppendLine(string.Create(CultureInfo.CurrentCulture,
                $"> **Tokens:** {usage.InputTokens + usage.OutputTokens:N0} ({usage.InputTokens:N0} in / {usage.OutputTokens:N0} out)"));
        if (turn.FinishedAt is { } finished && finished >= turn.CreatedAt)
            builder.AppendLine($"> **Duration:** {FormatDuration(finished - turn.CreatedAt)}");
        if (turn.Status != TurnStatus.Completed)
            builder.AppendLine($"> **Status:** {turn.Status}" + (turn.ErrorMessage is { } error ? $": {error}" : ""));
        if (!turn.IsHydrated) builder.AppendLine("> **Hidden from Claude's context**");
        builder.AppendLine();
    }

    private static void AppendTool(StringBuilder builder, ToolCallRecord tool, bool fullResult)
    {
        builder.AppendLine($"#### 🔧 Tool Call: `{(string.IsNullOrWhiteSpace(tool.Name) ? "unknown" : tool.Name.Trim())}`").AppendLine();
        if (FilterToolInput(tool.Input) is { Length: > 0 } input)
            builder.AppendLine("```json").AppendLine(input).AppendLine("```").AppendLine();
        if (tool.Result is null) return;

        builder.AppendLine(tool.IsError ? "#### ❌ Tool Error" : "#### ✅ Tool Result").AppendLine();
        var wasTruncated = false;
        var shown = fullResult ? tool.Result : TruncateLines(tool.Result, ToolResultLineLimit, out wasTruncated);
        // A longer fence keeps a full result's own fences from closing the block.
        var fence = fullResult ? "````" : "```";
        builder.AppendLine(fence).AppendLine(shown).AppendLine(fence);
        if (wasTruncated)
            builder.AppendLine().AppendLine($"*[Tool result truncated to first {ToolResultLineLimit} lines. Enable full tool results to export everything.]*");
        builder.AppendLine();
    }

    private static void AppendSection(StringBuilder builder, string header, string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        builder.AppendLine(header).AppendLine();
        builder.AppendLine(FixMacDownListSpacing(content.Trim())).AppendLine();
    }

    private static void AppendImageDescriptors(StringBuilder builder, IReadOnlyList<ImageAttachment> images)
    {
        for (var i = 0; i < images.Count; i++)
        {
            var fileName = images[i].FileName.ReplaceLineEndings(" ").Replace("`", "\\`", StringComparison.Ordinal);
            builder.AppendLine($"> **Attachment {i + 1}:** `{fileName}` · {images[i].MediaType} · {FormatBytes(DecodedLength(images[i].Data))}");
        }
        if (images.Count > 0) builder.AppendLine();
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.CurrentCulture);

    private static string FormatDuration(TimeSpan duration) => duration.TotalMinutes >= 1
        ? string.Create(CultureInfo.CurrentCulture, $"{duration.TotalMinutes:N1} min")
        : string.Create(CultureInfo.CurrentCulture, $"{duration.TotalSeconds:N1} sec");

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (1024d * 1024d):0.0} MB"),
        >= 1024 => string.Create(CultureInfo.CurrentCulture, $"{bytes / 1024d:0.0} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes} B"),
    };

    private static long DecodedLength(string base64) =>
        base64.Length / 4 * 3L - (base64.EndsWith("==", StringComparison.Ordinal) ? 2 : base64.EndsWith('=') ? 1 : 0);

    private static string TruncateLines(string content, int maxLines, out bool wasTruncated)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        wasTruncated = lines.Length > maxLines;
        return string.Join('\n', lines.Take(maxLines));
    }

    // Long strings are cut and "__" keys dropped, as in the Workbench; input that isn't JSON is shown as is.
    private static string FilterToolInput(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(FilterJsonValue(document.RootElement), new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return json.Trim(); }
    }

    private static object? FilterJsonValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .Where(property => !property.Name.StartsWith("__", StringComparison.Ordinal))
            .ToDictionary(property => property.Name, property => FilterJsonValue(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(FilterJsonValue).ToArray(),
        JsonValueKind.String => TruncateString(element.GetString() ?? ""),
        JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    private static string TruncateString(string value) => value.Length <= ToolInputStringLimit
        ? value
        : string.Create(CultureInfo.InvariantCulture, $"{value[..ToolInputStringLimit]}... [truncated {value.Length - ToolInputStringLimit} chars]");

    private static string TrimTrailingWhitespace(StringBuilder builder) => builder.ToString().TrimEnd() + Environment.NewLine;

    [GeneratedRegex(@"^[-*+]\s+")]
    private static partial Regex UnorderedListRegex();

    [GeneratedRegex(@"^\d+[.)]\s+")]
    private static partial Regex OrderedListRegex();

    [GeneratedRegex(@"```+\w*\n([\s\S]*?)\n```+")]
    private static partial Regex CodeFenceRegex();

    [GeneratedRegex(@"^#{1,6}\s+", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^>\s?", RegexOptions.Multiline)]
    private static partial Regex BlockquoteRegex();

    [GeneratedRegex(@"\*\*([^*]+)\*\*|\*([^*]+)\*")]
    private static partial Regex BoldItalicRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^[-▪]{3,}$", RegexOptions.Multiline)]
    private static partial Regex HorizontalRuleRegex();
}
