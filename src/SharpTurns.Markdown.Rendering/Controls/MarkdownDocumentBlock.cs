using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
using SharpTurns.Markdown.Rendering.Styling;
using TextMateSharp.Grammars;

namespace SharpTurns.Markdown.Rendering;

/// <summary>
/// Renders a markdown document by delegating non-table markdown to
/// <see cref="MarkdownContentBlock"/> and rendering GitHub-style pipe tables as
/// native Avalonia Grid/Border controls. This keeps LiveMarkdown for ordinary
/// markdown while giving tables predictable borders, padding, and alignment.
/// </summary>
public sealed class MarkdownDocumentBlock : StackPanel, ISearchableDocumentView
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, string?>(nameof(Markdown));

    public static readonly StyledProperty<FontFamily> ContentFontFamilyProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, FontFamily>(nameof(ContentFontFamily), MarkdownFontFamilies.Sans);

    public static readonly StyledProperty<IBrush> ContentForegroundProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, IBrush>(nameof(ContentForeground), SolidColorBrush.Parse("#CFD7E6"));

    public static readonly StyledProperty<IBrush?> StrongForegroundProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, IBrush?>(nameof(StrongForeground));

    public static readonly StyledProperty<double> TextIntensityProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, double>(nameof(TextIntensity), OklchColorUtility.DefaultTextIntensity);

    public static readonly StyledProperty<IBrush> InlineCodeForegroundProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, IBrush>(nameof(InlineCodeForeground), SolidColorBrush.Parse("#E5C07B"));

    public static readonly StyledProperty<bool> HighlightHybridCompressionSectionLabelsProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, bool>(nameof(HighlightHybridCompressionSectionLabels));

    public static readonly StyledProperty<IBrush> HybridCompressionSectionLabelForegroundProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, IBrush>(nameof(HybridCompressionSectionLabelForeground), SolidColorBrush.Parse("#00FFFF"));

    public static readonly StyledProperty<bool> HighlightIdentifiersProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, bool>(nameof(HighlightIdentifiers));

    public static readonly StyledProperty<IBrush> IdentifierForegroundProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, IBrush>(nameof(IdentifierForeground), SolidColorBrush.Parse("#00FFFF"));

    public static readonly StyledProperty<bool> UseBrightHeadingColorsProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, bool>(nameof(UseBrightHeadingColors));

    public static readonly StyledProperty<bool> ConstrainTablesToAvailableWidthProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, bool>(nameof(ConstrainTablesToAvailableWidth));

    public static readonly StyledProperty<double> ContentFontSizeProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, double>(nameof(ContentFontSize), 15);

    public static readonly StyledProperty<double> ContentLineHeightProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, double>(nameof(ContentLineHeight), 22);

    public static readonly StyledProperty<double> CodeFontSizeProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, double>(nameof(CodeFontSize), 13);

    public static readonly StyledProperty<ThemeName> CodeBlockColorThemeProperty =
        AvaloniaProperty.Register<MarkdownDocumentBlock, ThemeName>(nameof(CodeBlockColorTheme), ThemeName.DarkPlus);

    public MarkdownDocumentBlock()
    {
        Spacing = 0;

        var copyMarkdownSourceItem = new MenuItem { Header = "Copy Markdown Source" };
        copyMarkdownSourceItem.Click += OnCopyMarkdownSourceItemClick;
        ContextMenu = new ContextMenu
        {
            ItemsSource = new[] { copyMarkdownSourceItem }
        };
    }

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public FontFamily ContentFontFamily
    {
        get => GetValue(ContentFontFamilyProperty);
        set => SetValue(ContentFontFamilyProperty, value);
    }

    public IBrush ContentForeground
    {
        get => GetValue(ContentForegroundProperty);
        set => SetValue(ContentForegroundProperty, value);
    }

    public IBrush? StrongForeground
    {
        get => GetValue(StrongForegroundProperty);
        set => SetValue(StrongForegroundProperty, value);
    }

    public double TextIntensity
    {
        get => GetValue(TextIntensityProperty);
        set => SetValue(TextIntensityProperty, value);
    }

    public IBrush InlineCodeForeground
    {
        get => GetValue(InlineCodeForegroundProperty);
        set => SetValue(InlineCodeForegroundProperty, value);
    }

    public bool HighlightHybridCompressionSectionLabels
    {
        get => GetValue(HighlightHybridCompressionSectionLabelsProperty);
        set => SetValue(HighlightHybridCompressionSectionLabelsProperty, value);
    }

    public IBrush HybridCompressionSectionLabelForeground
    {
        get => GetValue(HybridCompressionSectionLabelForegroundProperty);
        set => SetValue(HybridCompressionSectionLabelForegroundProperty, value);
    }

    public bool HighlightIdentifiers
    {
        get => GetValue(HighlightIdentifiersProperty);
        set => SetValue(HighlightIdentifiersProperty, value);
    }

    public IBrush IdentifierForeground
    {
        get => GetValue(IdentifierForegroundProperty);
        set => SetValue(IdentifierForegroundProperty, value);
    }

    public bool UseBrightHeadingColors
    {
        get => GetValue(UseBrightHeadingColorsProperty);
        set => SetValue(UseBrightHeadingColorsProperty, value);
    }

    public bool ConstrainTablesToAvailableWidth
    {
        get => GetValue(ConstrainTablesToAvailableWidthProperty);
        set => SetValue(ConstrainTablesToAvailableWidthProperty, value);
    }

    public double ContentFontSize
    {
        get => GetValue(ContentFontSizeProperty);
        set => SetValue(ContentFontSizeProperty, value);
    }

    public double ContentLineHeight
    {
        get => GetValue(ContentLineHeightProperty);
        set => SetValue(ContentLineHeightProperty, value);
    }

    public double CodeFontSize
    {
        get => GetValue(CodeFontSizeProperty);
        set => SetValue(CodeFontSizeProperty, value);
    }

    public ThemeName CodeBlockColorTheme
    {
        get => GetValue(CodeBlockColorThemeProperty);
        set => SetValue(CodeBlockColorThemeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MarkdownProperty)
        {
            // Streaming text uses the plain-text sibling while this renderer is
            // hidden. Defer its rebuild until the existing show path below so
            // every streamed delta does not construct an invisible visual tree.
            if (IsVisible)
            {
                RebuildDocument();
            }

            return;
        }

        if (change.Property == IsVisibleProperty && IsVisible)
        {
            // LiveMarkdown's inline-code inlines can lose our post-render
            // styling when the document is hidden for raw-text mode and then
            // shown again. Rebuild children on re-show so the post-toggle path
            // follows the same construction/render-patch sequence as initial
            // load instead of trying to revive stale hidden visuals.
            RebuildDocument();
            return;
        }

        if (change.Property == ContentFontFamilyProperty ||
            change.Property == ContentForegroundProperty ||
            change.Property == StrongForegroundProperty ||
            change.Property == TextIntensityProperty ||
            change.Property == InlineCodeForegroundProperty ||
            change.Property == HighlightHybridCompressionSectionLabelsProperty ||
            change.Property == HybridCompressionSectionLabelForegroundProperty ||
            change.Property == HighlightIdentifiersProperty ||
            change.Property == IdentifierForegroundProperty ||
            change.Property == UseBrightHeadingColorsProperty ||
            change.Property == ConstrainTablesToAvailableWidthProperty ||
            change.Property == ContentFontSizeProperty ||
            change.Property == ContentLineHeightProperty ||
            change.Property == CodeFontSizeProperty ||
            change.Property == CodeBlockColorThemeProperty)
        {
            // Font/palette sliders and reset can change multiple style inputs in
            // quick succession. Rebuilding is more reliable than mutating the
            // existing LiveMarkdown visual tree, but hidden renderers can defer
            // that work until the show path applies the latest values.
            if (IsVisible)
            {
                RebuildDocument();
            }
        }
    }

    /// <summary>
    /// Recreates the rendered document from the current Markdown and style values.
    /// </summary>
    public void RefreshDocument() => RebuildDocument();

    public int GetSearchMatchCount(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 0;
        }

        var total = 0;
        foreach (var searchable in CollectSearchableChildren())
        {
            total += GetSearchMatchCount(searchable, query);
        }

        return total;
    }

    public void ApplySearchHighlight(string query, int activeLocalIndex)
    {
        var remaining = activeLocalIndex;
        foreach (var searchable in CollectSearchableChildren())
        {
            var count = GetSearchMatchCount(searchable, query);
            var localActive = (remaining >= 0 && remaining < count) ? remaining : -1;
            ApplySearchHighlight(searchable, query, localActive);
            if (localActive >= 0)
            {
                remaining = -1;
            }
            else if (remaining >= 0)
            {
                remaining -= count;
            }
        }
    }

    public void ClearSearchHighlight()
    {
        foreach (var searchable in CollectSearchableChildren())
        {
            ClearSearchHighlight(searchable);
        }
    }

    public Control? GetActiveMatchContainer()
    {
        foreach (var searchable in CollectSearchableChildren())
        {
            var active = GetActiveMatchContainer(searchable);
            if (active is not null)
            {
                return active;
            }
        }

        return null;
    }

    private void ApplyDocumentStyle()
    {
        foreach (var child in Children)
        {
            ApplyStyleToDescendant(child);
            foreach (var descendant in child.GetVisualDescendants())
            {
                ApplyStyleToDescendant(descendant);
            }
        }

        RefreshRenderedMarkdownChildren();
    }

    private void RefreshRenderedMarkdownChildren()
    {
        foreach (var child in Children.OfType<MarkdownContentBlock>())
        {
            child.RefreshRenderedVisuals();
        }

        foreach (var child in Children)
        {
            foreach (var descendant in child.GetVisualDescendants().OfType<MarkdownContentBlock>())
            {
                descendant.RefreshRenderedVisuals();
            }
        }
    }

    private void ApplyStyleToDescendant(Visual descendant)
    {
        switch (descendant)
        {
            case MarkdownContentBlock markdownBlock:
                markdownBlock.ContentFontFamily = ContentFontFamily;
                markdownBlock.ContentForeground = ContentForeground;
                markdownBlock.StrongForeground = StrongForeground;
                markdownBlock.TextIntensity = TextIntensity;
                markdownBlock.InlineCodeForeground = InlineCodeForeground;
                markdownBlock.HybridCompressionSectionLabelForeground = HybridCompressionSectionLabelForeground;
                markdownBlock.HighlightIdentifiers = HighlightIdentifiers;
                markdownBlock.IdentifierForeground = IdentifierForeground;
                markdownBlock.UseBrightHeadingColors = UseBrightHeadingColors;
                markdownBlock.ContentFontSize = ContentFontSize;
                markdownBlock.ContentLineHeight = ContentLineHeight;
                markdownBlock.CodeFontSize = CodeFontSize;
                markdownBlock.CodeBlockColorTheme = CodeBlockColorTheme;
                break;
            case MarkdownTableCellTextBlock tableCell:
                tableCell.FontFamily = ContentFontFamily;
                tableCell.Foreground = ContentForeground;
                tableCell.FontSize = Math.Max(11, ContentFontSize - 1);
                tableCell.HighlightIdentifiers = HighlightIdentifiers;
                tableCell.IdentifierForeground = IdentifierForeground;
                break;
        }
    }

    private void RebuildDocument()
    {
        Children.Clear();

        var segments = MarkdownTableParser.ParseDocument(Markdown);
        if (segments.Count == 0)
        {
            return;
        }

        var remainingHybridSectionLabels = HighlightHybridCompressionSectionLabels
            ? HybridCompressionSectionLabelKinds.All
            : HybridCompressionSectionLabelKinds.None;
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case MarkdownTextSegment textSegment:
                    var sectionLabels = TakeFirstHybridCompressionSectionLabels(
                        textSegment.Markdown,
                        ref remainingHybridSectionLabels);
                    Children.Add(CreateMarkdownBlock(textSegment.Markdown, sectionLabels));
                    break;
                case MarkdownTableSegment tableSegment:
                    Children.Add(MarkdownTableRenderer.CreateTableViewer(
                        tableSegment.Table,
                        ContentFontFamily,
                        ContentForeground,
                        ContentFontSize,
                        ConstrainTablesToAvailableWidth,
                        HighlightIdentifiers,
                        IdentifierForeground));
                    break;
            }
        }
    }

    private MarkdownContentBlock CreateMarkdownBlock(
        string markdown,
        HybridCompressionSectionLabelKinds hybridSectionLabelsToHighlight)
    {
        var markdownBlock = new MarkdownContentBlock
        {
            ContentFontFamily = ContentFontFamily,
            ContentForeground = ContentForeground,
            StrongForeground = StrongForeground,
            TextIntensity = TextIntensity,
            InlineCodeForeground = InlineCodeForeground,
            HybridCompressionSectionLabelForeground = HybridCompressionSectionLabelForeground,
            HighlightIdentifiers = HighlightIdentifiers,
            IdentifierForeground = IdentifierForeground,
            UseBrightHeadingColors = UseBrightHeadingColors,
            ContentFontSize = ContentFontSize,
            ContentLineHeight = ContentLineHeight,
            CodeFontSize = CodeFontSize,
            HybridCompressionSectionLabelsToHighlight = hybridSectionLabelsToHighlight,
        };

        markdownBlock.CodeBlockColorTheme = CodeBlockColorTheme;
        markdownBlock.Classes.Add("conversationMarkdown");
        if (ConstrainTablesToAvailableWidth)
        {
            markdownBlock.Classes.Add("markdownViewer");
        }
        markdownBlock.Markdown = markdown;
        return markdownBlock;
    }

    internal static HybridCompressionSectionLabelKinds TakeFirstHybridCompressionSectionLabels(
        string markdown,
        ref HybridCompressionSectionLabelKinds remaining)
    {
        if (remaining == HybridCompressionSectionLabelKinds.None)
        {
            return HybridCompressionSectionLabelKinds.None;
        }

        var selected = HybridCompressionSectionLabelKinds.None;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        foreach (var line in lines)
        {
            var kind = MarkdownContentBlock.GetHybridCompressionSectionLabelKind(line);
            if (kind == HybridCompressionSectionLabelKinds.None || !remaining.HasFlag(kind))
            {
                continue;
            }

            selected |= kind;
            remaining &= ~kind;
        }

        return selected;
    }

    private IEnumerable<Control> CollectSearchableChildren()
    {
        foreach (var descendant in this.GetVisualDescendants())
        {
            if ((descendant is MarkdownContentBlock || descendant is MarkdownTableCellTextBlock) &&
                descendant.IsEffectivelyVisible)
            {
                yield return (Control)descendant;
            }
        }
    }

    private static int GetSearchMatchCount(Control control, string query)
        => control switch
        {
            MarkdownContentBlock markdown => markdown.GetSearchMatchCount(query),
            MarkdownTableCellTextBlock tableCell => tableCell.GetSearchMatchCount(query),
            _ => 0
        };

    private static void ApplySearchHighlight(Control control, string query, int localActive)
    {
        switch (control)
        {
            case MarkdownContentBlock markdown:
                markdown.ApplySearchHighlight(query, localActive);
                break;
            case MarkdownTableCellTextBlock tableCell:
                tableCell.ApplySearchHighlight(query, localActive);
                break;
        }
    }

    private static void ClearSearchHighlight(Control control)
    {
        switch (control)
        {
            case MarkdownContentBlock markdown:
                markdown.ClearSearchHighlight();
                break;
            case MarkdownTableCellTextBlock tableCell:
                tableCell.ClearSearchHighlight();
                break;
        }
    }

    private static Control? GetActiveMatchContainer(Control control)
        => control switch
        {
            MarkdownContentBlock markdown => markdown.GetActiveMatchContainer()?.Child,
            MarkdownTableCellTextBlock tableCell => tableCell.GetActiveMatchContainer()?.Child,
            _ => null
        };

    private async void OnCopyMarkdownSourceItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(Markdown ?? string.Empty).ConfigureAwait(true);
        }
    }
}
