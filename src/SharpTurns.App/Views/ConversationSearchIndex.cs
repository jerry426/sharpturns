using LiveMarkdown.Avalonia;
using SharpTurns.App.ViewModels;
using SharpTurns.Markdown.Rendering;

namespace SharpTurns.App.Views;

/// <summary>The searchable text on a turn card. The view marks each control showing one with the matching class.</summary>
internal enum ConversationSearchSegmentKind
{
    Prompt,
    Text,
    WorkSummary,
    Question,
    Answer,
    ToolInput,
    ToolResult,
}

/// <summary>Text a card shows, keyed by the item (the turn, for its prompt) the showing control is bound to.</summary>
internal sealed record ConversationSearchSource(object Item, ConversationSearchSegmentKind Kind, string Text, bool IsMarkdown);

internal sealed record ConversationSearchEntry(object Item, ConversationSearchSegmentKind Kind, int FirstMatchIndex, int MatchCount);

/// <summary>
/// The Workbench's off-screen search index: counts the matches in each piece of shown text in display order, so the
/// total and the active match don't depend on what has rendered. Rendered Markdown is counted in its displayed text.
/// </summary>
internal sealed class ConversationSearchIndex
{
    private readonly Dictionary<(object Item, ConversationSearchSegmentKind Kind), ConversationSearchEntry> _entries;

    private ConversationSearchIndex(string query, IReadOnlyList<ConversationSearchEntry> entries, int totalMatches)
    {
        Query = query;
        Entries = entries;
        TotalMatches = totalMatches;
        _entries = entries.ToDictionary(entry => (entry.Item, entry.Kind));
    }

    public string Query { get; }

    public IReadOnlyList<ConversationSearchEntry> Entries { get; }

    public int TotalMatches { get; }

    /// <summary>
    /// The text the shown turns display, in order: each prompt, then its items. As in the Workbench, a collapsed tool card
    /// or Work Summary card is left out. Response text is Markdown only when it renders as Markdown.
    /// </summary>
    public static IReadOnlyList<ConversationSearchSource> CaptureSources(IEnumerable<TurnViewModel> turns, bool renderMarkdown)
    {
        var sources = new List<ConversationSearchSource>();
        foreach (var turn in turns)
        {
            sources.Add(new(turn, ConversationSearchSegmentKind.Prompt, turn.UserText, IsMarkdown: false));
            foreach (var item in turn.Items)
            {
                switch (item)
                {
                    case TextItemViewModel text:
                        if (text is { HasWorkSummary: true, IsExpanded: true })
                            sources.Add(new(text, ConversationSearchSegmentKind.WorkSummary, text.WorkSummaryText, renderMarkdown));
                        sources.Add(new(text, ConversationSearchSegmentKind.Text, text.PrimaryText, renderMarkdown && text.IsRendered));
                        break;
                    case UserMessageItemViewModel message:
                        sources.Add(new(message, ConversationSearchSegmentKind.Text, message.Text, IsMarkdown: false));
                        break;
                    case QuestionItemViewModel question:
                        sources.Add(new(question, ConversationSearchSegmentKind.Question, question.Question.Question, IsMarkdown: true));
                        sources.Add(new(question, ConversationSearchSegmentKind.Answer, question.AnswerText, IsMarkdown: false));
                        break;
                    case ToolItemViewModel { IsExpanded: true } tool:
                        sources.Add(new(tool, ConversationSearchSegmentKind.ToolInput, tool.InputDisplayText, IsMarkdown: false));
                        if (tool.HasResult) sources.Add(new(tool, ConversationSearchSegmentKind.ToolResult, tool.ResultDisplayText, IsMarkdown: false));
                        break;
                }
            }
        }
        return sources;
    }

    public static ConversationSearchIndex Build(string query, IReadOnlyList<ConversationSearchSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var pattern = new TextSearchPattern(query, TextSearchOptions.None);
        var entries = new List<ConversationSearchEntry>(sources.Count);
        var next = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = source.IsMarkdown
                ? MarkdownSearchTextProjector.CountMatches(source.Text, pattern, cancellationToken)
                : pattern.FindRanges(source.Text).Count();
            entries.Add(new ConversationSearchEntry(source.Item, source.Kind, next, count));
            next += count;
        }
        return new ConversationSearchIndex(query, entries, next);
    }

    public bool TryGetEntry(object item, ConversationSearchSegmentKind kind, out ConversationSearchEntry entry) =>
        _entries.TryGetValue((item, kind), out entry!);
}
