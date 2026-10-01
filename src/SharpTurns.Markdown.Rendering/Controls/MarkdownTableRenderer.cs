using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SharpTurns.Markdown.Rendering.Styling;

namespace SharpTurns.Markdown.Rendering;

internal static class MarkdownTableRenderer
{
    private static readonly IBrush TableBorderBrush = SolidColorBrush.Parse("#3E4658");
    private static readonly IBrush HeaderBackgroundBrush = SolidColorBrush.Parse("#1A2434");
    private static readonly IBrush BodyBackgroundBrush = SolidColorBrush.Parse("#101722");

    public static Control CreateTableViewer(
        MarkdownTableBlock table,
        FontFamily fontFamily,
        IBrush foreground,
        double fontSize,
        bool constrainToAvailableWidth = false,
        bool highlightIdentifiers = false,
        IBrush? identifierForeground = null)
    {
        var grid = new Grid
        {
            HorizontalAlignment = constrainToAvailableWidth ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = constrainToAvailableWidth ? new Thickness(0, 8, 0, 10) : default,
        };

        for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(
                constrainToAvailableWidth ? new GridLength(1, GridUnitType.Star) : GridLength.Auto));
        }

        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
        {
            var column = table.Columns[columnIndex];
            AddCell(grid, column.Header, rowIndex: 0, columnIndex, column.Alignment, isHeader: true, fontFamily, foreground, fontSize, constrainToAvailableWidth, highlightIdentifiers, identifierForeground);
        }

        for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
        {
            var row = table.Rows[rowIndex];
            for (var columnIndex = 0; columnIndex < table.Columns.Count; columnIndex++)
            {
                var content = columnIndex < row.Count ? row[columnIndex] : string.Empty;
                AddCell(grid, content, rowIndex + 1, columnIndex, table.Columns[columnIndex].Alignment, isHeader: false, fontFamily, foreground, fontSize, constrainToAvailableWidth, highlightIdentifiers, identifierForeground);
            }
        }

        if (constrainToAvailableWidth)
        {
            return grid;
        }

        return new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AllowAutoHide = false,
            Margin = new Thickness(0, 8, 0, 10),
        };
    }

    private static void AddCell(
        Grid grid,
        string content,
        int rowIndex,
        int columnIndex,
        MarkdownTableAlignment alignment,
        bool isHeader,
        FontFamily fontFamily,
        IBrush foreground,
        double fontSize,
        bool constrainToAvailableWidth,
        bool highlightIdentifiers,
        IBrush? identifierForeground)
    {
        var textBlock = new MarkdownTableCellTextBlock
        {
            SourceText = content,
            Foreground = foreground,
            FontFamily = fontFamily,
            FontSize = Math.Max(11, fontSize - 1),
            FontWeight = isHeader ? FontWeight.SemiBold : FontWeight.Normal,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = ToTextAlignment(alignment),
            MinWidth = constrainToAvailableWidth ? 0 : 96,
            MaxWidth = constrainToAvailableWidth ? double.PositiveInfinity : 260,
            HighlightIdentifiers = highlightIdentifiers,
            IdentifierForeground = identifierForeground ?? SolidColorBrush.Parse("#00FFFF"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var border = new Border
        {
            Background = isHeader ? HeaderBackgroundBrush : BodyBackgroundBrush,
            BorderBrush = TableBorderBrush,
            BorderThickness = new Thickness(columnIndex == 0 ? 1 : 0, rowIndex == 0 ? 1 : 0, 1, 1),
            Padding = new Thickness(9, 6),
            Child = textBlock,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        Grid.SetRow(border, rowIndex);
        Grid.SetColumn(border, columnIndex);
        grid.Children.Add(border);
    }

    private static TextAlignment ToTextAlignment(MarkdownTableAlignment alignment) => alignment switch
    {
        MarkdownTableAlignment.Center => TextAlignment.Center,
        MarkdownTableAlignment.Right => TextAlignment.Right,
        _ => TextAlignment.Left,
    };
}

internal sealed class MarkdownTableCellTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string> SourceTextProperty =
        AvaloniaProperty.Register<MarkdownTableCellTextBlock, string>(nameof(SourceText), string.Empty);

    public static readonly StyledProperty<bool> HighlightIdentifiersProperty =
        AvaloniaProperty.Register<MarkdownTableCellTextBlock, bool>(nameof(HighlightIdentifiers));

    public static readonly StyledProperty<IBrush> IdentifierForegroundProperty =
        AvaloniaProperty.Register<MarkdownTableCellTextBlock, IBrush>(nameof(IdentifierForeground), SolidColorBrush.Parse("#00FFFF"));

    private string? _searchQuery;
    private int _searchActiveLocalIndex = -1;
    private bool _searchActive;
    private InlineUIContainer? _activeMatchContainer;

    public MarkdownTableCellTextBlock()
    {
        RenderSourceInlines();
    }

    public string SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
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

    public int GetSearchMatchCount(string query) =>
        SearchHighlight.CountMatches(GetDisplayText(SourceText), query);

    public void ApplySearchHighlight(string query, int activeLocalIndex)
    {
        _searchQuery = query;
        _searchActiveLocalIndex = activeLocalIndex;
        _searchActive = !string.IsNullOrEmpty(query);
        RenderSearchHighlight();
    }

    public void ClearSearchHighlight()
    {
        _searchActive = false;
        _searchQuery = null;
        _searchActiveLocalIndex = -1;
        _activeMatchContainer = null;
        RenderSourceInlines();
    }

    public InlineUIContainer? GetActiveMatchContainer() => _activeMatchContainer;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceTextProperty ||
            change.Property == HighlightIdentifiersProperty ||
            change.Property == IdentifierForegroundProperty)
        {
            if (_searchActive)
            {
                RenderSearchHighlight();
            }
            else
            {
                RenderSourceInlines();
            }
        }
        else if (change.Property == ForegroundProperty ||
                 change.Property == FontFamilyProperty ||
                 change.Property == FontSizeProperty ||
                 change.Property == FontWeightProperty ||
                 change.Property == FontStyleProperty)
        {
            if (_searchActive)
            {
                RenderSearchHighlight();
            }
            else
            {
                RenderSourceInlines();
            }
        }
    }

    private void RenderSourceInlines()
    {
        _activeMatchContainer = null;
        var inlines = Inlines ??= new InlineCollection();
        inlines.Clear();
        AddSimpleMarkdownInlines(inlines, SourceText, Foreground, FontFamily, FontSize, FontWeight, FontStyle, HighlightIdentifiers, IdentifierForeground);
    }

    private void RenderSearchHighlight()
    {
        _activeMatchContainer = null;
        var inlines = Inlines ??= new InlineCollection();
        inlines.Clear();

        var query = _searchQuery;
        var displayText = GetDisplayText(SourceText);
        if (!_searchActive || string.IsNullOrEmpty(query))
        {
            RenderSourceInlines();
            return;
        }

        var matches = SearchHighlight.FindMatches(displayText, query);
        if (matches.Count == 0)
        {
            AddSimpleMarkdownInlines(inlines, SourceText, Foreground, FontFamily, FontSize, FontWeight, FontStyle, HighlightIdentifiers, IdentifierForeground);
            return;
        }

        var cursor = 0;
        var matchIndex = 0;
        foreach (var match in matches)
        {
            if (match.Start > cursor)
            {
                inlines.Add(MakeRun(displayText[cursor..match.Start], FontWeight));
            }

            var active = matchIndex == _searchActiveLocalIndex;
            var container = MakeHighlightInline(displayText[match.Start..(match.Start + match.Length)], active);
            inlines.Add(container);
            if (active)
            {
                _activeMatchContainer = container;
            }

            cursor = match.Start + match.Length;
            matchIndex++;
        }

        if (cursor < displayText.Length)
        {
            inlines.Add(MakeRun(displayText[cursor..], FontWeight));
        }
    }

    private Run MakeRun(string text, FontWeight fontWeight) => new(text)
    {
        Foreground = Foreground,
        FontFamily = FontFamily,
        FontSize = FontSize,
        FontWeight = fontWeight,
        FontStyle = FontStyle,
    };

    private InlineUIContainer MakeHighlightInline(string text, bool active)
    {
        var run = new Run(text)
        {
            Foreground = SolidColorBrush.Parse("#0A0A0A"),
            FontFamily = FontFamily,
            FontSize = FontSize,
            FontWeight = active ? FontWeight.Bold : FontWeight,
            FontStyle = FontStyle,
        };

        return new InlineUIContainer
        {
            BaselineAlignment = BaselineAlignment.Center,
            Child = new Border
            {
                Background = SolidColorBrush.Parse(active ? "#FF9900" : "#FFEB3B"),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(1, 0),
                Child = new TextBlock
                {
                    Text = run.Text,
                    Foreground = run.Foreground,
                    FontFamily = run.FontFamily,
                    FontSize = run.FontSize,
                    FontWeight = run.FontWeight,
                    FontStyle = run.FontStyle,
                    TextWrapping = TextWrapping.NoWrap,
                },
            },
        };
    }

    private static string GetDisplayText(string sourceText) =>
        MarkdownSearchTextProjector.GetTableCellDisplayText(sourceText);

    private static void AddSimpleMarkdownInlines(
        InlineCollection inlines,
        string content,
        IBrush? foreground,
        FontFamily fontFamily,
        double fontSize,
        FontWeight fontWeight,
        FontStyle fontStyle,
        bool highlightIdentifiers,
        IBrush identifierForeground)
    {
        if (string.IsNullOrEmpty(content))
        {
            return;
        }

        var index = 0;
        while (index < content.Length)
        {
            var boldStart = content.IndexOf("**", index, StringComparison.Ordinal);
            var codeStart = content.IndexOf('`', index);
            var nextStart = MinPositive(boldStart, codeStart);
            if (nextStart < 0)
            {
                AddTextRuns(inlines, content[index..], foreground, fontFamily, fontSize, fontWeight, fontStyle, highlightIdentifiers, identifierForeground);
                return;
            }

            if (nextStart > index)
            {
                AddTextRuns(inlines, content[index..nextStart], foreground, fontFamily, fontSize, fontWeight, fontStyle, highlightIdentifiers, identifierForeground);
            }

            if (nextStart == boldStart)
            {
                var boldEnd = content.IndexOf("**", boldStart + 2, StringComparison.Ordinal);
                if (boldEnd < 0)
                {
                    AddTextRuns(inlines, content[boldStart..], foreground, fontFamily, fontSize, fontWeight, fontStyle, highlightIdentifiers, identifierForeground);
                    return;
                }

                AddTextRuns(inlines, content[(boldStart + 2)..boldEnd], foreground, fontFamily, fontSize, FontWeight.Bold, fontStyle, highlightIdentifiers, identifierForeground);
                index = boldEnd + 2;
                continue;
            }

            var codeEnd = content.IndexOf('`', codeStart + 1);
            if (codeEnd < 0)
            {
                AddTextRuns(inlines, content[codeStart..], foreground, fontFamily, fontSize, fontWeight, fontStyle, highlightIdentifiers, identifierForeground);
                return;
            }

            AddTextRuns(
                inlines,
                content[(codeStart + 1)..codeEnd],
                SolidColorBrush.Parse("#D8DEE9"),
                MarkdownFontFamilies.Mono,
                fontSize,
                fontWeight,
                fontStyle,
                highlightIdentifiers,
                identifierForeground,
                SolidColorBrush.Parse("#252D3C"));
            index = codeEnd + 1;
        }
    }

    private static void AddTextRuns(
        InlineCollection inlines,
        string text,
        IBrush? foreground,
        FontFamily fontFamily,
        double fontSize,
        FontWeight fontWeight,
        FontStyle fontStyle,
        bool highlightIdentifiers,
        IBrush identifierForeground,
        IBrush? background = null)
    {
        if (!highlightIdentifiers)
        {
            inlines.Add(MakeRun(text, foreground, fontFamily, fontSize, fontWeight, fontStyle, background));
            return;
        }

        var cursor = 0;
        foreach (System.Text.RegularExpressions.Match match in MarkdownContentBlock.IdentifierPattern.Matches(text))
        {
            if (match.Index > cursor)
            {
                inlines.Add(MakeRun(text[cursor..match.Index], foreground, fontFamily, fontSize, fontWeight, fontStyle, background));
            }

            inlines.Add(MakeRun(match.Value, identifierForeground, fontFamily, fontSize, fontWeight, fontStyle, background));
            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length)
        {
            inlines.Add(MakeRun(text[cursor..], foreground, fontFamily, fontSize, fontWeight, fontStyle, background));
        }
    }

    private static Run MakeRun(
        string text,
        IBrush? foreground,
        FontFamily fontFamily,
        double fontSize,
        FontWeight fontWeight,
        FontStyle fontStyle,
        IBrush? background = null) => new(text)
    {
        Foreground = foreground,
        FontFamily = fontFamily,
        FontSize = fontSize,
        FontWeight = fontWeight,
        FontStyle = fontStyle,
        Background = background,
    };

    private static int MinPositive(int first, int second) => (first, second) switch
    {
        (< 0, < 0) => -1,
        (< 0, _) => second,
        (_, < 0) => first,
        _ => Math.Min(first, second),
    };
}
