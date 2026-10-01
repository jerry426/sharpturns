using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SharpTurns.Markdown.Rendering.Styling;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Extensions.GenericAttributes;
using System.Text.RegularExpressions;

namespace SharpTurns.Markdown.Rendering;

[Flags]
internal enum HybridCompressionSectionLabelKinds
{
    None = 0,
    WorkSummary = 1,
    FinalAssistantResponse = 2,
    PartialAssistantResponse = 4,
    FailureNotice = 8,
    All = WorkSummary | FinalAssistantResponse | PartialAssistantResponse | FailureNotice,
}

public sealed class MarkdownContentBlock : MarkdownRenderer
{
    // Give the host first refusal before the renderer's default browser navigation.
    public static readonly RoutedEvent<LinkClickedEventArgs> PreviewLinkClickEvent =
        RoutedEvent.Register<MarkdownContentBlock, LinkClickedEventArgs>(
            "PreviewLinkClick", RoutingStrategies.Bubble);

    private const string PreservedBlankLineMarkerSource = "&#8203;&#8288;&#8203;";
    private const string PreservedBlankLineMarkerText = "\u200B\u2060\u200B";
    private static readonly FontFamily InlineCodeFontFamily = MarkdownFontFamilies.Mono;

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

    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, string?>(nameof(Markdown));

    public static readonly StyledProperty<FontFamily> ContentFontFamilyProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, FontFamily>(nameof(ContentFontFamily), MarkdownFontFamilies.Sans);

    public static readonly StyledProperty<IBrush> ContentForegroundProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, IBrush>(nameof(ContentForeground), SolidColorBrush.Parse("#CFD7E6"));

    public static readonly StyledProperty<IBrush?> StrongForegroundProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, IBrush?>(nameof(StrongForeground));

    public static readonly StyledProperty<double> TextIntensityProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, double>(nameof(TextIntensity), OklchColorUtility.DefaultTextIntensity);

    public static readonly StyledProperty<IBrush> InlineCodeForegroundProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, IBrush>(nameof(InlineCodeForeground), SolidColorBrush.Parse("#E5C07B"));

    public static readonly StyledProperty<IBrush> HybridCompressionSectionLabelForegroundProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, IBrush>(nameof(HybridCompressionSectionLabelForeground), SolidColorBrush.Parse("#00FFFF"));

    public static readonly StyledProperty<double> ContentFontSizeProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, double>(nameof(ContentFontSize), 15);

    public static readonly StyledProperty<double> ContentLineHeightProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, double>(nameof(ContentLineHeight), 22);

    public static readonly StyledProperty<double> CodeFontSizeProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, double>(nameof(CodeFontSize), 13);

    public static readonly StyledProperty<bool> HighlightIdentifiersProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, bool>(nameof(HighlightIdentifiers), false);

    public static readonly StyledProperty<IBrush> IdentifierForegroundProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, IBrush>(nameof(IdentifierForeground), SolidColorBrush.Parse("#00FFFF"));

    public static readonly StyledProperty<bool> UseBrightHeadingColorsProperty =
        AvaloniaProperty.Register<MarkdownContentBlock, bool>(nameof(UseBrightHeadingColors), false);

    internal static readonly Regex IdentifierPattern = new(
        @"\[[A-Z][A-Z0-9_./: \p{Pd}]*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Color BrightHeadingForegroundColor = Color.Parse("#FFE66D");

    private readonly ObservableStringBuilder _markdownBuilder = new();
    private readonly Dictionary<MarkdownTextBlock, int> _preservedBlankLineCounts = [];
    private readonly Dictionary<Run, Color> _codeRunSourceForegroundColors = [];
    private readonly Func<Uri, Task<bool>> _launchLinkUriAsync;
    private string _renderedMarkdown = string.Empty;
    private bool _renderVisualPatchPending;

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

    static MarkdownContentBlock()
    {
        // Must subscribe before LiveMarkdown builds its shared pipeline, which
        // happens when the first renderer instance parses.
        ConfigurePipeline += RemoveGenericAttributes;
    }

    // Markdig's generic attributes syntax ({#id .class}) silently drops plain
    // braces such as {} or {name}, which are common in assistant replies.
    internal static void RemoveGenericAttributes(MarkdownPipelineBuilder builder) =>
        builder.Extensions.TryRemove<GenericAttributesExtension>();

    public MarkdownContentBlock() : this(null)
    {
    }

    internal MarkdownContentBlock(Func<Uri, Task<bool>>? launchLinkUriAsync)
    {
        _launchLinkUriAsync = launchLinkUriAsync ?? (uri =>
            TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri) ?? Task.FromResult(false));
        MarkdownBuilder = _markdownBuilder;
        LayoutUpdated += OnLayoutUpdated;
        LinkClick += OnLinkClick;

        var copyMarkdownSourceItem = new MenuItem { Header = "Copy Markdown Source" };
        copyMarkdownSourceItem.Click += OnCopyMarkdownSourceItemClick;
        ContextMenu = new ContextMenu
        {
            ItemsSource = new[] { copyMarkdownSourceItem }
        };
    }

    private async void OnLinkClick(object? sender, LinkClickedEventArgs e)
    {
        if (e.Handled || LinkCommand is not null)
        {
            return;
        }

        var preview = new LinkClickedEventArgs(PreviewLinkClickEvent, this, e.HRef);
        RaiseEvent(preview);
        if (preview.Handled)
        {
            e.Handled = true;
            return;
        }

        // LiveMarkdown raises this event on activation; only its separate context-menu
        // Open command launches a URI automatically. Keep generated links web-only.
        if (e.Handled || LinkCommand is not null || e.HRef is not { IsAbsoluteUri: true } uri ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        e.Handled = true;
        try
        {
            if (await _launchLinkUriAsync(uri))
            {
                return;
            }
        }
        catch
        {
            // A failed OS launch must not escape this async event handler.
        }

        // Do not log the destination: generated URLs may contain sensitive values.
        Logger.TryGet(LogEventLevel.Warning, nameof(MarkdownContentBlock))?
            .Log(this, "Could not open Markdown link in the default browser.");
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

    public IBrush HybridCompressionSectionLabelForeground
    {
        get => GetValue(HybridCompressionSectionLabelForegroundProperty);
        set => SetValue(HybridCompressionSectionLabelForegroundProperty, value);
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

    internal HybridCompressionSectionLabelKinds HybridCompressionSectionLabelsToHighlight { get; init; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MarkdownProperty)
        {
            ReplaceMarkdown(change.GetNewValue<string?>());
        }
        else if (change.Property == ContentFontFamilyProperty ||
            change.Property == ContentForegroundProperty ||
            change.Property == StrongForegroundProperty ||
            change.Property == TextIntensityProperty ||
            change.Property == InlineCodeForegroundProperty ||
            change.Property == HybridCompressionSectionLabelForegroundProperty ||
            change.Property == ContentFontSizeProperty ||
            change.Property == ContentLineHeightProperty ||
            change.Property == CodeFontSizeProperty ||
            change.Property == HighlightIdentifiersProperty ||
            change.Property == IdentifierForegroundProperty ||
            change.Property == UseBrightHeadingColorsProperty)
        {
            // Typography changes invalidate the clean-run snapshots so the next
            // highlight pass recaptures with the new fonts/colors. If search is
            // currently active, restore the clean runs first; otherwise the next
            // snapshot would be captured from the highlighted inline tree, where
            // matched words live inside InlineUIContainers and would be skipped.
            RestoreSearchInlines();
            _searchSnapshots = null;
            ScheduleRenderedVisualPatch(_renderedMarkdown, force: true);
        }
    }

    private void ReplaceMarkdown(string? markdown)
    {
        markdown ??= string.Empty;

        // Restore before an incremental update can reuse the existing inline tree.
        RestoreSearchInlines();
        _searchSnapshots = null;
        _preservedBlankLineCounts.Clear();

        // Task list markers are normalized before rendering because LiveMarkdown
        // consumes [x]/[ ]
        // but does not reliably show the checkbox visual in the conversation
        // template. Consecutive blank lines are represented by an isolated spacer
        // paragraph whose rendered inlines are removed below. Those transformations
        // can desynchronize LiveMarkdown's incremental document update, so use full
        // rebuilds for markdown containing either construct. LiveMarkdown 2.4
        // represents inline code as native text runs, which can be styled in place.
        var requiresFullRender = MayContainTaskList(markdown) ||
            MayContainConsecutiveBlankLines(markdown);
        if (!requiresFullRender && _renderedMarkdown.Length > 0 && markdown.StartsWith(_renderedMarkdown, StringComparison.Ordinal))
        {
            _markdownBuilder.Append(markdown[_renderedMarkdown.Length..]);
            _renderedMarkdown = markdown;
            ScheduleRenderedVisualPatch(markdown, force: true);
            return;
        }

        _markdownBuilder.Clear();
        _markdownBuilder.Append(PrepareMarkdownForRendering(markdown));
        _renderedMarkdown = markdown;
        ScheduleRenderedVisualPatch(markdown, force: true);
    }

    public void RefreshRenderedVisuals() => ScheduleRenderedVisualPatch(_renderedMarkdown, force: true);

    public async Task<bool> CopySelectionWithFormattingAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return false;
        }

        var blocks = this.GetVisualDescendants()
            .OfType<MarkdownTextBlock>()
            .Where(block => !block.GetVisualAncestors().OfType<MarkdownTextBlock>().Any())
            .ToArray();
        var plainText = SelectedText;
        var payload = RenderedMarkdownClipboard.Create(_renderedMarkdown, plainText, blocks);
        if (payload is null)
        {
            return false;
        }

        try
        {
            await clipboard.SetDataAsync(RenderedMarkdownClipboard.CreateDataTransfer(payload)).ConfigureAwait(true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ScheduleRenderedVisualPatch(string markdown, bool force = false)
    {
        if (!force && !MayContainList(markdown) && !MayContainInlineCode(markdown))
        {
            _renderVisualPatchPending = false;
            return;
        }

        _renderVisualPatchPending = true;
        Dispatcher.UIThread.Post(PatchRenderedVisualsIfPending, DispatcherPriority.Background);
    }

    private static bool MayContainInlineCode(string markdown) => markdown.Contains('`', StringComparison.Ordinal);

    private static bool MayContainTaskList(string markdown) => markdown.Contains("[ ]", StringComparison.Ordinal)
        || markdown.Contains("[x]", StringComparison.OrdinalIgnoreCase);

    private static bool MayContainConsecutiveBlankLines(string markdown)
    {
        var consecutiveBlankLines = 0;
        foreach (var line in NormalizeLineEndings(markdown).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                consecutiveBlankLines++;
                if (consecutiveBlankLines >= 2)
                {
                    return true;
                }
            }
            else
            {
                consecutiveBlankLines = 0;
            }
        }

        return false;
    }

    private static string PrepareMarkdownForRendering(string markdown)
    {
        var mayContainTaskList = MayContainTaskList(markdown);
        var mayContainConsecutiveBlankLines = MayContainConsecutiveBlankLines(markdown);
        if (!mayContainTaskList && !mayContainConsecutiveBlankLines)
        {
            return markdown;
        }

        var normalized = NormalizeLineEndings(markdown);
        var lines = normalized.Split('\n');
        var changed = false;
        if (mayContainTaskList)
        {
            for (var index = 0; index < lines.Length; index++)
            {
                if (TryRenderTaskListLine(lines[index], out var renderedLine))
                {
                    lines[index] = renderedLine;
                    changed = true;
                }
            }
        }

        var rendered = mayContainConsecutiveBlankLines
            ? PreserveConsecutiveBlankLines(lines, ref changed)
            : string.Join('\n', lines);
        return changed ? rendered : markdown;
    }

    private static string NormalizeLineEndings(string markdown) =>
        markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string PreserveConsecutiveBlankLines(IReadOnlyList<string> lines, ref bool changed)
    {
        var renderedLines = new List<string>(lines.Count);
        var inFence = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;

        for (var index = 0; index < lines.Count;)
        {
            var line = lines[index];
            if (!string.IsNullOrWhiteSpace(line))
            {
                renderedLines.Add(line);
                UpdateFenceState(line, ref inFence, ref fenceCharacter, ref fenceLength);
                index++;
                continue;
            }

            var blankRunEnd = index + 1;
            while (blankRunEnd < lines.Count && string.IsNullOrWhiteSpace(lines[blankRunEnd]))
            {
                blankRunEnd++;
            }

            var blankLineCount = blankRunEnd - index;
            var isBoundedByContent = index > 0 && blankRunEnd < lines.Count;
            if (inFence || blankLineCount < 2 || !isBoundedByContent ||
                IsIndentedCodeGap(lines[index - 1], lines[blankRunEnd]))
            {
                for (var blankIndex = index; blankIndex < blankRunEnd; blankIndex++)
                {
                    renderedLines.Add(lines[blankIndex]);
                }

                index = blankRunEnd;
                continue;
            }

            // CommonMark collapses every run of blank lines into one block
            // separator. Keep that first separator unchanged, then encode the
            // surplus lines in an isolated zero-width paragraph. The rendered
            // paragraph is replaced with a native-height spacer after parsing,
            // so marker characters never remain selectable or copyable.
            renderedLines.Add(lines[index]);
            var surplusBlankLineCount = blankLineCount - 1;
            for (var surplusIndex = 0; surplusIndex < surplusBlankLineCount; surplusIndex++)
            {
                var hardBreakSuffix = surplusIndex < surplusBlankLineCount - 1 ? "  " : string.Empty;
                renderedLines.Add(PreservedBlankLineMarkerSource + hardBreakSuffix);
            }

            renderedLines.Add(string.Empty);
            changed = true;
            index = blankRunEnd;
        }

        return string.Join('\n', renderedLines);
    }

    private static void UpdateFenceState(
        string line,
        ref bool inFence,
        ref char fenceCharacter,
        ref int fenceLength)
    {
        if (!TryGetFenceRun(line, out var character, out var length, out var hasOnlyTrailingWhitespace))
        {
            return;
        }

        if (!inFence)
        {
            inFence = true;
            fenceCharacter = character;
            fenceLength = length;
        }
        else if (character == fenceCharacter && length >= fenceLength && hasOnlyTrailingWhitespace)
        {
            inFence = false;
            fenceCharacter = '\0';
            fenceLength = 0;
        }
    }

    private static bool TryGetFenceRun(
        string line,
        out char fenceCharacter,
        out int fenceLength,
        out bool hasOnlyTrailingWhitespace)
    {
        fenceCharacter = '\0';
        fenceLength = 0;
        hasOnlyTrailingWhitespace = false;

        var markerStart = 0;
        while (markerStart < line.Length && line[markerStart] == ' ')
        {
            markerStart++;
        }

        if (markerStart > 3 || markerStart >= line.Length || line[markerStart] is not ('`' or '~'))
        {
            return false;
        }

        fenceCharacter = line[markerStart];
        var markerEnd = markerStart;
        while (markerEnd < line.Length && line[markerEnd] == fenceCharacter)
        {
            markerEnd++;
        }

        fenceLength = markerEnd - markerStart;
        hasOnlyTrailingWhitespace = line[markerEnd..].All(char.IsWhiteSpace);
        return fenceLength >= 3;
    }

    private static bool IsIndentedCodeGap(string precedingLine, string followingLine) =>
        HasIndentedCodePrefix(precedingLine) && HasIndentedCodePrefix(followingLine);

    private static bool HasIndentedCodePrefix(string line) =>
        line.StartsWith('\t') || line.TakeWhile(character => character == ' ').Count() >= 4;

    private static bool TryRenderTaskListLine(string line, out string renderedLine)
    {
        renderedLine = line;
        var markerStart = 0;
        while (markerStart < line.Length && line[markerStart] == ' ')
        {
            markerStart++;
        }

        if (line.Length < markerStart + 6 ||
            (line[markerStart] != '-' && line[markerStart] != '*' && line[markerStart] != '+') ||
            line[markerStart + 1] != ' ' ||
            line[markerStart + 2] != '[' ||
            line[markerStart + 4] != ']' ||
            line[markerStart + 5] != ' ')
        {
            return false;
        }

        var check = line[markerStart + 3];
        if (check != ' ' && check != 'x' && check != 'X')
        {
            return false;
        }

        var checkboxGlyph = check == ' ' ? "☐" : "☑";
        renderedLine = string.Concat(line[..markerStart], line[markerStart], " ", checkboxGlyph, " ", line[(markerStart + 6)..]);
        return true;
    }

    private static bool MayContainList(string markdown)
    {
        var lines = markdown.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                return true;
            }

            var dotIndex = trimmed.IndexOf('.', StringComparison.Ordinal);
            if (dotIndex > 0 && dotIndex <= 3 && trimmed.Length > dotIndex + 1 && trimmed[dotIndex + 1] == ' ')
            {
                var allDigits = true;
                for (var i = 0; i < dotIndex; i++)
                {
                    if (!char.IsDigit(trimmed[i]))
                    {
                        allDigits = false;
                        break;
                    }
                }

                if (allDigits)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => PatchRenderedVisualsIfPending();

    private async void OnCopyMarkdownSourceItemClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(_renderedMarkdown).ConfigureAwait(true);
        }
    }

    private void PatchRenderedVisualsIfPending()
    {
        if (!_renderVisualPatchPending)
        {
            return;
        }

        RestoreSearchInlines();
        _searchSnapshots = null;
        if (PatchRenderedMarkdownVisuals())
        {
            _renderVisualPatchPending = false;
        }

        // After the rendered-visual patch (which rebuilds/corrects inlines),
        // re-apply any active search highlight so highlights stay in sync with
        // re-renders during streaming and font/style changes.
        if (_searchActive)
        {
            RenderSearchHighlight();
        }
    }

    private bool PatchRenderedMarkdownVisuals()
    {
        var patchedAny = false;
        var highlightedHybridSectionLabels = HybridCompressionSectionLabelKinds.None;
        foreach (var visual in this.GetVisualDescendants())
        {
            if (visual is TextBlock textBlock)
            {
                if (textBlock.Classes.Contains("ListBlockBullet"))
                {
                    if (HasTaskListAncestor(textBlock))
                    {
                        textBlock.Text = string.Empty;
                        textBlock.MinWidth = 0;
                    }
                    else
                    {
                        textBlock.Text = GetBulletGlyph(textBlock);
                        textBlock.MinWidth = 10;
                    }

                    textBlock.Foreground = ContentForeground;
                    textBlock.FontFamily = ContentFontFamily;
                    textBlock.FontSize = Math.Max(10, ContentFontSize - 1);
                    textBlock.LineHeight = ContentLineHeight - 1;
                    textBlock.Margin = new Thickness(0, 0, 2, 0);
                    textBlock.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                    textBlock.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                    patchedAny = true;
                }
                else if (textBlock.Classes.Contains("ListBlockNumber"))
                {
                    textBlock.Foreground = ContentForeground;
                    textBlock.FontFamily = ContentFontFamily;
                    textBlock.FontSize = ContentFontSize;
                    textBlock.LineHeight = ContentLineHeight - 1;
                    textBlock.Margin = new Thickness(0, 0, 2, 0);
                    textBlock.MinWidth = 18;
                    textBlock.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                    textBlock.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
                    patchedAny = true;
                }
            }

            if (visual is CheckBox checkBox && HasTaskListAncestor(checkBox))
            {
                checkBox.IsHitTestVisible = false;
                checkBox.Focusable = false;
                checkBox.Margin = new Thickness(0, 0, 6, 0);
                checkBox.VerticalAlignment = VerticalAlignment.Top;
                patchedAny = true;
            }

            if (visual is Border quoteBlock && quoteBlock.Classes.Contains("QuoteBlock"))
            {
                quoteBlock.BorderBrush = SolidColorBrush.Parse("#4B6CB7");
                quoteBlock.BorderThickness = new Thickness(3, 0, 0, 0);
                quoteBlock.Padding = new Thickness(12, 0, 0, 0);
                quoteBlock.Margin = new Thickness(0, 4, 0, 12);
                patchedAny = true;
            }

            if (visual is MarkdownTextBlock markdownTextBlock)
            {
                if (ApplyPreservedBlankLineSpacing(markdownTextBlock))
                {
                    patchedAny = true;
                    continue;
                }

                if (HasCodeBlockAncestor(markdownTextBlock))
                {
                    // Fenced code blocks render their own MarkdownTextBlock (the
                    // template's PART_CodeTextBlock) with a monospace font and
                    // syntax-highlighted runs. Scale the size, line height, and
                    // syntax colors to follow the viewer's appearance controls.
                    ApplyCodeBlockTypography(markdownTextBlock);
                }
                else
                {
                    ApplyTextBlockTypography(markdownTextBlock, ref highlightedHybridSectionLabels);
                    StyleInlineCodeInlines(markdownTextBlock);
                    if (HighlightIdentifiers)
                    {
                        ApplyIdentifierHighlighting(markdownTextBlock.Inlines, IdentifierForeground);
                    }
                }

                patchedAny = true;
            }

            if (visual is MarkdownTextBlock { Classes: var classes } paragraph &&
                classes.Contains("ParagraphBlock") &&
                HasListAncestor(paragraph))
            {
                paragraph.Margin = new Thickness(0);
                paragraph.LineHeight = ContentLineHeight - 1;
                patchedAny = true;
            }
        }

        return patchedAny;
    }

    private bool ApplyPreservedBlankLineSpacing(MarkdownTextBlock textBlock)
    {
        if (!textBlock.Classes.Contains("ParagraphBlock"))
        {
            return false;
        }

        if (!_preservedBlankLineCounts.TryGetValue(textBlock, out var blankLineCount))
        {
            blankLineCount = GetPreservedBlankLineCount(textBlock.ActualText);
            if (blankLineCount == 0)
            {
                if (textBlock.Classes.Remove("PreservedBlankLines"))
                {
                    textBlock.ClearValue(Layoutable.HeightProperty);
                    textBlock.ClearValue(Layoutable.MarginProperty);
                }

                return false;
            }

            _preservedBlankLineCounts[textBlock] = blankLineCount;
            textBlock.Classes.Add("PreservedBlankLines");
        }

        textBlock.Inlines?.Clear();
        textBlock.Margin = new Thickness(0);
        textBlock.Height = blankLineCount * ContentLineHeight;
        return true;
    }

    private static int GetPreservedBlankLineCount(string text)
    {
        var lines = NormalizeLineEndings(text).Split('\n');
        return lines.Length > 0 && lines.All(line => line == PreservedBlankLineMarkerText)
            ? lines.Length
            : 0;
    }

    private void ApplyTextBlockTypography(
        MarkdownTextBlock textBlock,
        ref HybridCompressionSectionLabelKinds highlightedHybridSectionLabels)
    {
        var fontScale = GetHeadingFontScale(textBlock);
        var fontSize = ContentFontSize * fontScale;
        var hybridSectionLabelKind = TakeHybridCompressionSectionLabelToHighlight(
            textBlock,
            ref highlightedHybridSectionLabels);
        var foreground = hybridSectionLabelKind != HybridCompressionSectionLabelKinds.None
            ? HybridCompressionSectionLabelForeground
            : ContentForeground;
        ApplyHybridCompressionSectionLabelDelineator(
            textBlock,
            hybridSectionLabelKind,
            HybridCompressionSectionLabelForeground);
        if (UseBrightHeadingColors)
        {
            foreground = GetBrightHeadingForeground(textBlock, foreground, TextIntensity);
        }
        textBlock.Foreground = foreground;
        textBlock.FontFamily = ContentFontFamily;
        textBlock.FontSize = fontSize;
        textBlock.LineHeight = ContentLineHeight * fontScale;
        ApplyInlineTypography(textBlock.Inlines, foreground, fontSize, textBlock.FontWeight, textBlock.FontStyle, textBlock.TextDecorations);
        if (StrongForeground is not null)
        {
            ApplyStrongForeground(textBlock.Inlines, StrongForeground);
        }
    }

    internal static void ApplyStrongForeground(
        InlineCollection? inlines,
        IBrush strongForeground,
        bool inheritedStrong = false)
    {
        if (inlines is null)
        {
            return;
        }

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Link:
                    // Link styling takes precedence even inside bold text.
                    break;
                case Run run when inheritedStrong:
                    run.Foreground = strongForeground;
                    break;
                case Span span:
                    var isStrong = inheritedStrong || span.Classes.Contains("Bold");
                    if (isStrong)
                    {
                        span.Foreground = strongForeground;
                    }

                    ApplyStrongForeground(span.Inlines, strongForeground, isStrong);
                    break;
            }
        }
    }

    private static IBrush GetBrightHeadingForeground(
        MarkdownTextBlock textBlock,
        IBrush fallback,
        double textIntensity) =>
        HasAncestorClass(textBlock, "Heading1Block") ||
        HasAncestorClass(textBlock, "Heading2Block") ||
        HasAncestorClass(textBlock, "Heading3Block") ||
        HasAncestorClass(textBlock, "Heading4Block") ||
        HasAncestorClass(textBlock, "Heading5Block") ||
        HasAncestorClass(textBlock, "Heading6Block")
            ? CreateTextIntensityBrush(BrightHeadingForegroundColor, textIntensity)
            :
        fallback;

    internal static void ApplyIdentifierHighlighting(InlineCollection? inlines, IBrush identifierForeground)
    {
        if (inlines is null)
        {
            return;
        }

        for (var index = 0; index < inlines.Count;)
        {
            switch (inlines[index])
            {
                case Run:
                    var runSequence = new List<Run>();
                    while (index + runSequence.Count < inlines.Count &&
                           inlines[index + runSequence.Count] is Run run)
                    {
                        runSequence.Add(run);
                    }

                    index += ReplaceIdentifierRunSequence(
                        inlines,
                        index,
                        runSequence,
                        identifierForeground);
                    break;
                case Span span:
                    ApplyIdentifierHighlighting(span.Inlines, identifierForeground);
                    index++;
                    break;
                default:
                    index++;
                    break;
            }
        }
    }

    private static int ReplaceIdentifierRunSequence(
        InlineCollection inlines,
        int index,
        IReadOnlyList<Run> sourceRuns,
        IBrush identifierForeground)
    {
        var text = string.Concat(sourceRuns.Select(run => run.Text));
        var matches = IdentifierPattern.Matches(text);
        if (matches.Count == 0)
        {
            return sourceRuns.Count;
        }

        var replacement = new List<Run>();
        var sourceOffset = 0;
        foreach (var source in sourceRuns)
        {
            var sourceText = source.Text ?? string.Empty;
            var sourceEnd = sourceOffset + sourceText.Length;
            var localCursor = 0;
            foreach (Match match in matches)
            {
                var matchEnd = match.Index + match.Length;
                if (matchEnd <= sourceOffset || match.Index >= sourceEnd)
                {
                    continue;
                }

                var localMatchStart = Math.Max(0, match.Index - sourceOffset);
                var localMatchEnd = Math.Min(sourceText.Length, matchEnd - sourceOffset);
                if (localMatchStart > localCursor)
                {
                    replacement.Add(CloneRun(source, sourceText[localCursor..localMatchStart], source.Foreground));
                }

                replacement.Add(CloneRun(source, sourceText[localMatchStart..localMatchEnd], identifierForeground));
                localCursor = localMatchEnd;
            }

            if (localCursor < sourceText.Length)
            {
                replacement.Add(CloneRun(source, sourceText[localCursor..], source.Foreground));
            }

            sourceOffset = sourceEnd;
        }

        for (var sourceIndex = 0; sourceIndex < sourceRuns.Count; sourceIndex++)
        {
            inlines.RemoveAt(index);
        }

        for (var replacementIndex = replacement.Count - 1; replacementIndex >= 0; replacementIndex--)
        {
            inlines.Insert(index, replacement[replacementIndex]);
        }

        return replacement.Count;
    }

    private static Run CloneRun(Run source, string text, IBrush? foreground) => new(text)
    {
        Foreground = foreground ?? source.Foreground,
        FontFamily = source.FontFamily,
        FontSize = source.FontSize,
        FontWeight = source.FontWeight,
        FontStyle = source.FontStyle,
        TextDecorations = source.TextDecorations,
        BaselineAlignment = source.BaselineAlignment,
    };

    private HybridCompressionSectionLabelKinds TakeHybridCompressionSectionLabelToHighlight(
        MarkdownTextBlock textBlock,
        ref HybridCompressionSectionLabelKinds highlightedHybridSectionLabels)
    {
        if (!HasAncestorClass(textBlock, "Heading2Block"))
        {
            return HybridCompressionSectionLabelKinds.None;
        }

        var kind = GetHybridCompressionSectionLabelKind(textBlock.ActualText);
        if (kind == HybridCompressionSectionLabelKinds.None ||
            !HybridCompressionSectionLabelsToHighlight.HasFlag(kind) ||
            highlightedHybridSectionLabels.HasFlag(kind))
        {
            return HybridCompressionSectionLabelKinds.None;
        }

        highlightedHybridSectionLabels |= kind;
        return kind;
    }

    internal static void ApplyHybridCompressionSectionLabelDelineator(
        MarkdownTextBlock textBlock,
        HybridCompressionSectionLabelKinds kind,
        IBrush sectionLabelForeground)
    {
        if (kind != HybridCompressionSectionLabelKinds.FinalAssistantResponse)
        {
            return;
        }

        foreach (var ancestor in textBlock.GetVisualAncestors())
        {
            if (ancestor is Border { Classes: var classes } heading &&
                classes.Contains("Heading2Block"))
            {
                heading.BorderBrush = sectionLabelForeground;
                heading.BorderThickness = new Thickness(0, 6, 0, 0);
                return;
            }
        }
    }

    internal static bool IsHybridCompressionSectionLabel(string text) =>
        GetHybridCompressionSectionLabelKind(text) != HybridCompressionSectionLabelKinds.None;

    internal static HybridCompressionSectionLabelKinds GetHybridCompressionSectionLabelKind(string text)
    {
        if (IsWorkSummarySectionLabel(text))
        {
            return HybridCompressionSectionLabelKinds.WorkSummary;
        }

        return text switch
        {
            "Final Assistant Response — Verbatim" or "## Final Assistant Response — Verbatim" =>
                HybridCompressionSectionLabelKinds.FinalAssistantResponse,
            "Partial Assistant Response — Verbatim" or "## Partial Assistant Response — Verbatim" =>
                HybridCompressionSectionLabelKinds.PartialAssistantResponse,
            "Failure Notice — Verbatim" or "## Failure Notice — Verbatim" =>
                HybridCompressionSectionLabelKinds.FailureNotice,
            _ => HybridCompressionSectionLabelKinds.None,
        };
    }

    private static bool IsWorkSummarySectionLabel(string text)
    {
        const string label = "Work Summary";
        var normalized = text.StartsWith("## ", StringComparison.Ordinal) ? text[3..] : text;
        return string.Equals(normalized, label, StringComparison.Ordinal)
            || (normalized.StartsWith($"{label} (", StringComparison.Ordinal)
                && normalized.EndsWith(')'));
    }

    private void ApplyInlineTypography(
        InlineCollection? inlines,
        IBrush foreground,
        double fontSize,
        FontWeight inheritedFontWeight,
        FontStyle inheritedFontStyle,
        TextDecorationCollection? inheritedTextDecorations,
        bool insideLink = false)
    {
        if (inlines is null)
        {
            return;
        }

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    if (insideLink)
                    {
                        run.ClearValue(TextElement.ForegroundProperty);
                    }
                    else
                    {
                        run.Foreground = foreground;
                    }
                    run.FontFamily = ContentFontFamily;
                    run.FontSize = fontSize;
                    run.FontWeight = StrongerFontWeight(run.FontWeight, inheritedFontWeight);
                    run.FontStyle = run.FontStyle == FontStyle.Normal ? inheritedFontStyle : run.FontStyle;
                    run.TextDecorations ??= inheritedTextDecorations;
                    break;
                case Span span:
                    ApplySemanticInlineClasses(span);
                    var spanInsideLink = insideLink || span is Link;
                    if (span is not Link)
                    {
                        if (spanInsideLink)
                        {
                            span.ClearValue(TextElement.ForegroundProperty);
                        }
                        else
                        {
                            span.Foreground = foreground;
                        }
                    }
                    span.FontFamily = ContentFontFamily;
                    span.FontSize = fontSize;
                    var spanFontWeight = StrongerFontWeight(span.FontWeight, inheritedFontWeight);
                    var spanFontStyle = span.FontStyle == FontStyle.Normal ? inheritedFontStyle : span.FontStyle;
                    var spanTextDecorations = span.TextDecorations ?? inheritedTextDecorations;
                    span.FontWeight = spanFontWeight;
                    span.FontStyle = spanFontStyle;
                    span.TextDecorations = spanTextDecorations;
                    ApplyInlineTypography(span.Inlines, foreground, fontSize, spanFontWeight, spanFontStyle, spanTextDecorations, spanInsideLink);
                    break;
            }
        }
    }

    private static void ApplySemanticInlineClasses(Span span)
    {
        if (span.Classes.Contains("Bold"))
        {
            span.FontWeight = FontWeight.Bold;
        }

        if (span.Classes.Contains("Italic"))
        {
            span.FontStyle = FontStyle.Italic;
        }

        if (span.Classes.Contains("Strikethrough"))
        {
            span.TextDecorations = TextDecorations.Strikethrough;
        }

        if (span.Classes.Contains("Underline"))
        {
            span.TextDecorations = TextDecorations.Underline;
        }
    }

    private void ApplyCodeBlockTypography(MarkdownTextBlock textBlock)
    {
        // Keep LiveMarkdown's monospace font family and per-run syntax-highlight
        // colors, while applying the same text-intensity adjustment used by
        // ordinary text. The source color for each run is cached so changing the
        // slider repeatedly never dims or brightens an already-adjusted color.
        textBlock.FontSize = CodeFontSize;
        textBlock.LineHeight = CodeFontSize + 7;
        ApplyCodeBlockTextIntensity(textBlock.Inlines, TextIntensity, _codeRunSourceForegroundColors);
    }

    internal static void ApplyCodeBlockTextIntensity(
        InlineCollection? inlines,
        double textIntensity,
        IDictionary<Run, Color> sourceForegroundColors)
    {
        if (inlines is null)
        {
            return;
        }

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    if (GetCodeRunSourceForegroundColor(run, sourceForegroundColors) is { } sourceColor)
                    {
                        run.Foreground = CreateTextIntensityBrush(sourceColor, textIntensity);
                    }

                    break;
                case Span span:
                    ApplyCodeBlockTextIntensity(span.Inlines, textIntensity, sourceForegroundColors);
                    break;
            }
        }
    }

    private static Color? GetCodeRunSourceForegroundColor(
        Run run,
        IDictionary<Run, Color> sourceForegroundColors)
    {
        if (sourceForegroundColors.TryGetValue(run, out var sourceColor))
        {
            return sourceColor;
        }

        if (run.Foreground is not SolidColorBrush brush)
        {
            return null;
        }

        sourceForegroundColors[run] = brush.Color;
        return brush.Color;
    }

    private static IBrush CreateTextIntensityBrush(Color color, double intensity) =>
        new SolidColorBrush(OklchColorUtility.AdjustTextIntensity(color, intensity));

    private Inline MakeMarkdownInlineWithSourceForeground(MarkdownRunInfo info)
    {
        var inline = MakeMarkdownInline(info);
        if (inline is Run run && info.SourceForegroundColor is { } sourceColor)
        {
            _codeRunSourceForegroundColors[run] = sourceColor;
        }

        return inline;
    }

    private void StyleInlineCodeInlines(MarkdownTextBlock textBlock)
    {
        if (textBlock.Inlines is { } inlines)
        {
            StyleInlineCodeInlines(inlines, InlineCodeForeground, ContentFontSize);
        }
    }

    private static void StyleInlineCodeInlines(InlineCollection inlines, IBrush inlineCodeForeground, double contentFontSize)
    {
        // LiveMarkdown 2.4 renders inline code as a text Run instead of an embedded
        // control. Style that run in place so it keeps the app's established
        // no-chip appearance without disrupting incremental document updates.
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case CodeInline codeInline:
                    codeInline.Background = null;
                    codeInline.Padding = default;
                    codeInline.Margin = default;
                    codeInline.Foreground = inlineCodeForeground;
                    codeInline.FontFamily = InlineCodeFontFamily;
                    codeInline.FontSize = contentFontSize;
                    codeInline.FontWeight = FontWeight.Normal;
                    codeInline.FontStyle = FontStyle.Normal;
                    codeInline.BaselineAlignment = BaselineAlignment.Baseline;
                    break;

                case Span span when span.Inlines is { } spanInlines:
                    StyleInlineCodeInlines(spanInlines, inlineCodeForeground, contentFontSize);
                    break;
            }
        }
    }

    private static Run MakeInlineCodeRun(string text, IBrush foreground, double fontSize) => new(text)
    {
        Foreground = foreground,
        FontFamily = InlineCodeFontFamily,
        FontSize = fontSize,
        FontWeight = FontWeight.Normal,
        FontStyle = FontStyle.Normal,
        BaselineAlignment = BaselineAlignment.Baseline,
    };

    private static string GetBulletGlyph(TextBlock textBlock)
    {
        if (textBlock.Classes.Contains("Level1"))
        {
            return "◦";
        }

        if (textBlock.Classes.Contains("Level2"))
        {
            return "▪";
        }

        if (textBlock.Classes.Contains("Level3"))
        {
            return "▫";
        }

        return "•";
    }

    private static double GetHeadingFontScale(Control control)
    {
        if (HasAncestorClass(control, "Heading1Block"))
        {
            return 1.4;
        }

        if (HasAncestorClass(control, "Heading2Block"))
        {
            return 1.2;
        }

        if (HasAncestorClass(control, "Heading3Block"))
        {
            return 1.07;
        }

        return 1;
    }

    private static FontWeight StrongerFontWeight(FontWeight current, FontWeight inherited) => current == FontWeight.Normal
        ? inherited
        : current;

    private static bool HasListAncestor(Control control) => HasAncestorClass(control, "ListBlock", typeName: "MarkdownListGrid");

    private static bool HasTaskListAncestor(Control control) => HasAncestorClass(control, "TaskList");

    private static bool HasCodeBlockAncestor(Control control)
    {
        foreach (var ancestor in control.GetVisualAncestors())
        {
            if (ancestor is CodeBlock)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasAncestorClass(Control control, string className, string? typeName = null)
    {
        foreach (var ancestor in control.GetVisualAncestors())
        {
            if (ancestor is StyledElement styledElement &&
                styledElement.Classes.Contains(className) &&
                (typeName is null || ancestor.GetType().Name == typeName))
            {
                return true;
            }
        }

        return false;
    }

    // ---- In-context search highlighting ----

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
                        matchText, info, active);
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

    private static InlineUIContainer MakeMarkdownHighlightInline(
        string text,
        MarkdownRunInfo info,
        bool active)
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
                FontWeight = active ? FontWeight.Bold : info.FontWeight,
                FontStyle = info.FontStyle,
                TextDecorations = info.TextDecorations,
            },
        };

        return new InlineUIContainer
        {
            Child = border,
            BaselineAlignment = info.BaselineAlignment,
        };
    }
}
