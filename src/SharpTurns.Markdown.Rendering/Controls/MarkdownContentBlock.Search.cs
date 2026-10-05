using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;

namespace SharpTurns.Markdown.Rendering;

public sealed partial class MarkdownContentBlock
{
    // Search highlight colors. All matches use yellow; the active match (the one
    // the user navigated to via previous/next) uses orange + bold, mirroring the
    // legacy Python QML TextContentItem highlight colors.
    private static readonly IBrush SearchMatchBackgroundBrush = SolidColorBrush.Parse("#FFEB3B");
    private static readonly IBrush SearchActiveMatchBackgroundBrush = SolidColorBrush.Parse("#FF9900");
    private static readonly IBrush SearchMatchForegroundBrush = SolidColorBrush.Parse("#0A0A0A");

    private sealed record MarkdownRunInfo(
        string Text,
        IBrush Foreground,
        FontFamily FontFamily,
        double FontSize,
        FontWeight FontWeight,
        FontStyle FontStyle,
        BaselineAlignment BaselineAlignment,
        TextDecorationCollection? TextDecorations,
        bool IsLineBreak = false,
        Color? SourceForegroundColor = null,
        Run? SourceRun = null,
        InlineCollection? SourceInlines = null);

    // Search highlight state. _searchSnapshots holds a snapshot of each rendered
    // paragraph's clean leaf runs. Only matching runs are temporarily replaced,
    // inside their original spans, so links keep their identity and behavior.
    // Collections touched by highlighting are restored from their original inlines.
    private Dictionary<MarkdownTextBlock, List<MarkdownRunInfo>>? _searchSnapshots;
    private Dictionary<InlineCollection, Inline[]>? _searchOriginalInlines;
    private string? _searchQuery;
    private int _searchActiveLocalIndex = -1;
    private bool _searchActive;
    private InlineUIContainer? _activeMatchContainer;

    /// <summary>
    /// Returns the number of case-insensitive matches of <paramref name="query"/>
    /// across all rendered paragraph/code blocks.
    /// </summary>
    public int GetSearchMatchCount(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        var total = 0;
        foreach (var visual in this.GetVisualDescendants())
        {
            if (visual is MarkdownTextBlock block)
            {
                total += SnapshotMatchCount(EnsureSnapshot(block), query);
            }
        }

        return total;
    }

    /// <summary>
    /// Highlights every match. The match whose local index (the index of this
    /// match within this whole markdown block) equals
    /// <paramref name="activeLocalIndex"/>, or -1 for none, is rendered as the
    /// active match in orange.
    /// </summary>
    public void ApplySearchHighlight(string query, int activeLocalIndex)
    {
        _searchQuery = query;
        _searchActiveLocalIndex = activeLocalIndex;
        _searchActive = !string.IsNullOrEmpty(query);
        RenderSearchHighlight();
    }

    /// <summary>
    /// Removes search highlighting, restoring the original inline objects
    /// (or leaving the rendered tree untouched when never highlighted).
    /// </summary>
    public void ClearSearchHighlight()
    {
        _searchActive = false;
        _searchQuery = null;
        _searchActiveLocalIndex = -1;
        RestoreSearchInlines();
    }

    /// <summary>
    /// Returns the visual representing the active match (for scroll-into-view),
    /// or null when this block holds no active match.
    /// </summary>
    public InlineUIContainer? GetActiveMatchContainer() => _activeMatchContainer;

    private void RenderSearchHighlight()
    {
        RestoreSearchInlines();

        var query = _searchQuery;
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        var remaining = _searchActiveLocalIndex;

        foreach (var block in this.GetVisualDescendants().OfType<MarkdownTextBlock>().ToArray())
        {
            var snapshot = EnsureSnapshot(block);
            var count = SnapshotMatchCount(snapshot, query);
            var blockActive = (remaining >= 0 && remaining < count) ? remaining : -1;

            var localIdx = 0;
            foreach (var info in snapshot)
            {
                var matches = SearchHighlight.FindMatches(info.Text, query);
                if (matches.Count == 0 || info.SourceRun is not { } sourceRun || info.SourceInlines is not { } inlines)
                {
                    continue;
                }

                _searchOriginalInlines ??= new Dictionary<InlineCollection, Inline[]>();
                if (!_searchOriginalInlines.ContainsKey(inlines))
                {
                    _searchOriginalInlines.Add(inlines, inlines.ToArray());
                }

                var inlineIndex = inlines.IndexOf(sourceRun);
                inlines.RemoveAt(inlineIndex);
                var cursor = 0;
                foreach (var match in matches)
                {
                    if (match.Start > cursor)
                    {
                        inlines.Insert(inlineIndex++, MakeMarkdownInlineWithSourceForeground(info with { Text = info.Text[cursor..match.Start] }));
                    }

                    var active = localIdx == blockActive;
                    var matchText = info.Text[match.Start..(match.Start + match.Length)];
                    var container = MakeMarkdownHighlightInline(
                        matchText, info, active, block.LineHeight);
                    inlines.Insert(inlineIndex++, container);

                    if (active)
                    {
                        _activeMatchContainer = container;
                    }

                    cursor = match.Start + match.Length;
                    localIdx++;
                }

                if (cursor < info.Text.Length)
                {
                    inlines.Insert(inlineIndex, MakeMarkdownInlineWithSourceForeground(info with { Text = info.Text[cursor..] }));
                }
            }

            remaining = blockActive >= 0 ? -1 : (remaining >= 0 ? remaining - count : -1);
        }
    }

    private void RestoreSearchInlines()
    {
        _activeMatchContainer = null;
        if (_searchOriginalInlines is null)
        {
            return;
        }

        foreach (var (inlines, originals) in _searchOriginalInlines)
        {
            inlines.Clear();
            inlines.AddRange(originals);
        }

        _searchOriginalInlines.Clear();
    }

    private List<MarkdownRunInfo> EnsureSnapshot(MarkdownTextBlock block)
    {
        _searchSnapshots ??= new Dictionary<MarkdownTextBlock, List<MarkdownRunInfo>>();
        if (!_searchSnapshots.TryGetValue(block, out var snapshot))
        {
            snapshot = CaptureSnapshot(block);
            _searchSnapshots[block] = snapshot;
        }

        return snapshot;
    }

    private List<MarkdownRunInfo> CaptureSnapshot(MarkdownTextBlock block)
    {
        var infos = new List<MarkdownRunInfo>();
        if (block.Inlines is { } inlines)
        {
            CaptureInline(inlines, infos, block, block.FontWeight, block.FontStyle, block.TextDecorations);
        }

        return infos;
    }

    private void CaptureInline(
        InlineCollection inlines,
        List<MarkdownRunInfo> infos,
        MarkdownTextBlock block,
        FontWeight inheritedFontWeight,
        FontStyle inheritedFontStyle,
        TextDecorationCollection? inheritedTextDecorations)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    var sourceForegroundColor = HasCodeBlockAncestor(block)
                        ? GetCodeRunSourceForegroundColor(run, _codeRunSourceForegroundColors)
                        : null;
                    infos.Add(new MarkdownRunInfo(
                        run.Text ?? string.Empty,
                        run.Foreground!,
                        run.FontFamily!,
                        run.FontSize,
                        StrongerFontWeight(run.FontWeight, inheritedFontWeight),
                        run.FontStyle == FontStyle.Normal ? inheritedFontStyle : run.FontStyle,
                        run.BaselineAlignment,
                        run.TextDecorations ?? inheritedTextDecorations,
                        SourceForegroundColor: sourceForegroundColor,
                        SourceRun: run,
                        SourceInlines: inlines));
                    break;

                case LineBreak:
                    infos.Add(new MarkdownRunInfo(
                        string.Empty,
                        block.Foreground!,
                        block.FontFamily!,
                        block.FontSize,
                        block.FontWeight,
                        block.FontStyle,
                        BaselineAlignment.Baseline,
                        null,
                        IsLineBreak: true));
                    break;

                case Span span when span.Inlines is { } spanInlines:
                    CaptureInline(
                        spanInlines,
                        infos,
                        block,
                        StrongerFontWeight(span.FontWeight, inheritedFontWeight),
                        span.FontStyle == FontStyle.Normal ? inheritedFontStyle : span.FontStyle,
                        span.TextDecorations ?? inheritedTextDecorations);
                    break;

                // Inline containers (images, etc.) are not searchable text.
            }
        }
    }

    private static int SnapshotMatchCount(List<MarkdownRunInfo> snapshot, string query)
    {
        var total = 0;
        foreach (var info in snapshot)
        {
            if (!info.IsLineBreak)
            {
                total += SearchHighlight.CountMatches(info.Text, query);
            }
        }

        return total;
    }

    private static Inline MakeMarkdownInline(MarkdownRunInfo info)
    {
        if (info.IsLineBreak)
        {
            return new LineBreak();
        }

        var run = new Run(info.Text)
        {
            Foreground = info.Foreground,
            FontFamily = info.FontFamily,
            FontSize = info.FontSize,
            FontWeight = info.FontWeight,
            FontStyle = info.FontStyle,
            BaselineAlignment = info.BaselineAlignment,
        };

        if (info.TextDecorations is not null)
        {
            run.TextDecorations = info.TextDecorations;
        }

        return run;
    }

    private Inline MakeMarkdownInlineWithSourceForeground(MarkdownRunInfo info)
    {
        var inline = MakeMarkdownInline(info);
        if (inline is Run run && info.SourceForegroundColor is { } sourceColor)
        {
            _codeRunSourceForegroundColors[run] = sourceColor;
        }

        return inline;
    }

    private static InlineUIContainer MakeMarkdownHighlightInline(
        string text,
        MarkdownRunInfo info,
        bool active,
        double lineHeight)
    {
        var border = new Border
        {
            Background = active ? SearchActiveMatchBackgroundBrush : SearchMatchBackgroundBrush,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(1, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = text,
                Foreground = SearchMatchForegroundBrush,
                FontFamily = info.FontFamily,
                FontSize = info.FontSize,
                FontWeight = info.FontWeight,
                FontStyle = info.FontStyle,
                TextDecorations = info.TextDecorations,
                LineHeight = lineHeight,
            },
        };

        // An embedded control sits with its bottom on the line's baseline unless it
        // reports its own, which would lift the match above the surrounding text.
        using var layout = new TextLayout(
            text,
            new Typeface(info.FontFamily, info.FontStyle, info.FontWeight),
            info.FontSize,
            null,
            lineHeight: lineHeight);
        TextBlock.SetBaselineOffset(border, layout.Baseline);

        return new InlineUIContainer
        {
            Child = border,
            BaselineAlignment = info.BaselineAlignment,
        };
    }
}
