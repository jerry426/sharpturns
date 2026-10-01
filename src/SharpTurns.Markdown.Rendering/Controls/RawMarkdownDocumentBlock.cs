using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using SharpTurns.Markdown.Rendering.Styling;

namespace SharpTurns.Markdown.Rendering;

/// <summary>
/// Presents Markdown source verbatim through the existing searchable renderer.
/// A plain fenced code block preserves source punctuation while reusing the
/// renderer's all-match and active-match highlighting implementation.
/// </summary>
public sealed class RawMarkdownDocumentBlock : ContentControl, ISearchableDocumentView
{
    private static readonly FontFamily MonospaceFontFamily = MarkdownFontFamilies.Mono;

    private readonly MarkdownContentBlock _renderer;

    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<RawMarkdownDocumentBlock, string?>(nameof(SourceText));

    public static readonly StyledProperty<IBrush> SourceForegroundProperty =
        AvaloniaProperty.Register<RawMarkdownDocumentBlock, IBrush>(
            nameof(SourceForeground),
            SolidColorBrush.Parse("#CFD7E6"));

    public static readonly StyledProperty<double> SourceFontSizeProperty =
        AvaloniaProperty.Register<RawMarkdownDocumentBlock, double>(nameof(SourceFontSize), 14);

    public static readonly StyledProperty<double> SourceLineHeightProperty =
        AvaloniaProperty.Register<RawMarkdownDocumentBlock, double>(nameof(SourceLineHeight), 21);

    public RawMarkdownDocumentBlock()
    {
        _renderer = new MarkdownContentBlock
        {
            ContentFontFamily = MonospaceFontFamily,
            ContentForeground = SourceForeground,
            ContentFontSize = SourceFontSize,
            ContentLineHeight = SourceLineHeight,
            CodeFontSize = SourceFontSize,
        };
        _renderer.Classes.Add("conversationMarkdown");
        _renderer.Classes.Add("markdownViewer");
        _renderer.Classes.Add("rawMarkdown");
        _renderer.ContextMenu = CreateContextMenu();
        Content = _renderer;
    }

    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public IBrush SourceForeground
    {
        get => GetValue(SourceForegroundProperty);
        set => SetValue(SourceForegroundProperty, value);
    }

    public double SourceFontSize
    {
        get => GetValue(SourceFontSizeProperty);
        set => SetValue(SourceFontSizeProperty, value);
    }

    public double SourceLineHeight
    {
        get => GetValue(SourceLineHeightProperty);
        set => SetValue(SourceLineHeightProperty, value);
    }

    public bool CanCopy => !string.IsNullOrEmpty(_renderer.SelectedText);

    public int GetSearchMatchCount(string query) => _renderer.GetSearchMatchCount(query);

    public void ApplySearchHighlight(string query, int activeLocalIndex) =>
        _renderer.ApplySearchHighlight(query, activeLocalIndex);

    public void ClearSearchHighlight() => _renderer.ClearSearchHighlight();

    public Control? GetActiveMatchContainer() => _renderer.GetActiveMatchContainer()?.Child;

    public async Task<bool> CopySelectionAsync()
    {
        var selection = _renderer.SelectedText;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (string.IsNullOrEmpty(selection) || clipboard is null)
        {
            return false;
        }

        await clipboard.SetTextAsync(selection).ConfigureAwait(true);
        return true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceTextProperty)
        {
            _renderer.Markdown = CreateFencedSource(change.GetNewValue<string?>() ?? string.Empty);
        }
        else if (change.Property == SourceForegroundProperty)
        {
            _renderer.ContentForeground = change.GetNewValue<IBrush>();
        }
        else if (change.Property == SourceFontSizeProperty)
        {
            var fontSize = change.GetNewValue<double>();
            _renderer.ContentFontSize = fontSize;
            _renderer.CodeFontSize = fontSize;
        }
        else if (change.Property == SourceLineHeightProperty)
        {
            _renderer.ContentLineHeight = change.GetNewValue<double>();
        }
    }

    internal static string CreateFencedSource(string source)
    {
        var longestBacktickRun = 0;
        var currentBacktickRun = 0;
        foreach (var character in source)
        {
            if (character == '`')
            {
                currentBacktickRun++;
                longestBacktickRun = Math.Max(longestBacktickRun, currentBacktickRun);
            }
            else
            {
                currentBacktickRun = 0;
            }
        }

        var fence = new string('`', Math.Max(3, longestBacktickRun + 1));
        var separatorBeforeClosingFence = source.EndsWith('\n') ? string.Empty : "\n";
        return $"{fence}\n{source}{separatorBeforeClosingFence}{fence}";
    }

    private ContextMenu CreateContextMenu()
    {
        var copySourceItem = new MenuItem { Header = "Copy Raw Markdown Source" };
        copySourceItem.Click += async (_, _) =>
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
            {
                await clipboard.SetTextAsync(SourceText ?? string.Empty).ConfigureAwait(true);
            }
        };

        return new ContextMenu
        {
            ItemsSource = new[] { copySourceItem },
        };
    }
}
