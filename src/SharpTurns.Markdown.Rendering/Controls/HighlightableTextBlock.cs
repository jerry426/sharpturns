using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace SharpTurns.Markdown.Rendering;

/// <summary>
/// A read-only <see cref="TextBox"/> used for raw conversation content. Using
/// Avalonia's text editor rather than its text-display controls provides native,
/// reliable drag selection and clipboard behavior for multiline text.
/// </summary>
/// <remarks>
/// Avalonia's text editor does not expose multiple independent highlight ranges,
/// so search matches are drawn by an overlay layered over the template's text
/// presenter, in the same colors as the rendered Markdown highlights.
/// </remarks>
public sealed class HighlightableTextBlock : TextBox
{
    private readonly SearchMatchOverlay _overlay = new();
    private string? _searchQuery;
    private int _searchActiveLocalIndex = -1;

    protected override Type StyleKeyOverride => typeof(TextBox);

    public HighlightableTextBlock()
    {
        IsReadOnly = true;
        AcceptsReturn = true;
        Background = null;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        ScrollViewer.SetHorizontalScrollBarVisibility(this, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(this, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
    }

    /// <summary>
    /// Returns the number of case-insensitive matches of <paramref name="query"/>
    /// in the current <see cref="Avalonia.Controls.TextBlock.Text"/>.
    /// </summary>
    public int GetSearchMatchCount(string query)
        => SearchHighlight.CountMatches(Text ?? string.Empty, query);

    /// <summary>
    /// Highlights every match, marking the active one. A value of -1 highlights
    /// the matches with none active. The user's selection is left alone.
    /// </summary>
    public void ApplySearchHighlight(string query, int activeLocalIndex)
    {
        _searchQuery = query;
        _searchActiveLocalIndex = activeLocalIndex;
        UpdateOverlay();
    }

    /// <summary>
    /// Removes all search highlighting and returns to plain <see cref="Text"/>
    /// rendering.
    /// </summary>
    public void ClearSearchHighlight()
    {
        _searchQuery = null;
        _searchActiveLocalIndex = -1;
        UpdateOverlay();
    }

    /// <summary>
    /// Returns the visual representing the active match (for scroll-into-view),
    /// or null when this block holds no active match.
    /// </summary>
    public Control? GetActiveMatchContainer()
        => _searchActiveLocalIndex >= 0 && _searchActiveLocalIndex < _overlay.Matches.Count ? this : null;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        (_overlay.GetVisualParent() as Panel)?.Children.Remove(_overlay);
        var presenter = e.NameScope.Find<TextPresenter>("PART_TextPresenter");
        _overlay.Presenter = presenter;
        // The Fluent template stacks the placeholder and the presenter in a Panel;
        // adding the overlay last draws it above the text.
        if (presenter?.GetVisualParent() is Panel panel)
        {
            panel.Children.Add(_overlay);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !string.IsNullOrEmpty(_searchQuery))
        {
            UpdateOverlay();
        }
    }

    private void UpdateOverlay()
    {
        _overlay.Matches = string.IsNullOrEmpty(_searchQuery)
            ? []
            : SearchHighlight.FindMatches(Text ?? string.Empty, _searchQuery!);
        _overlay.ActiveIndex = _searchActiveLocalIndex;
        _overlay.InvalidateVisual();
    }

    /// <summary>
    /// Fills each match's text bounds and redraws the matched text in dark ink,
    /// using a copy of the presenter's layout clipped to those bounds.
    /// </summary>
    private sealed class SearchMatchOverlay : Control
    {
        // The rendered Markdown highlight colors (MarkdownContentBlock).
        private static readonly IBrush MatchBackgroundBrush = SolidColorBrush.Parse("#FFEB3B");
        private static readonly IBrush ActiveMatchBackgroundBrush = SolidColorBrush.Parse("#FF9900");
        private static readonly IBrush MatchForegroundBrush = SolidColorBrush.Parse("#0A0A0A");

        private TextPresenter? _presenter;
        private TextLayout? _sourceLayout;
        private TextLayout? _inkLayout;

        public SearchMatchOverlay()
        {
            IsHitTestVisible = false;
        }

        public IReadOnlyList<SearchHighlight.Match> Matches { get; set; } = [];

        public int ActiveIndex { get; set; } = -1;

        public TextPresenter? Presenter
        {
            get => _presenter;
            set
            {
                if (_presenter is not null) _presenter.PropertyChanged -= OnPresenterPropertyChanged;
                _presenter = value;
                if (_presenter is not null) _presenter.PropertyChanged += OnPresenterPropertyChanged;
            }
        }

        public override void Render(DrawingContext context)
        {
            if (_presenter is not { } presenter || Matches.Count == 0
                || presenter.TranslatePoint(default, this) is not { } origin)
            {
                return;
            }

            var layout = presenter.TextLayout;
            var ink = GetInkLayout(presenter, layout);
            using (context.PushTransform(Matrix.CreateTranslation(origin.X, origin.Y)))
            {
                for (var i = 0; i < Matches.Count; i++)
                {
                    var brush = i == ActiveIndex ? ActiveMatchBackgroundBrush : MatchBackgroundBrush;
                    foreach (var rect in layout.HitTestTextRange(Matches[i].Start, Matches[i].Length))
                    {
                        context.FillRectangle(brush, rect.Inflate(new Thickness(1, 0)), 3);
                        using (context.PushClip(rect))
                        {
                            ink.Draw(context, default);
                        }
                    }
                }
            }
        }

        // The presenter replaces its layout whenever its text, size, or font changes.
        private TextLayout GetInkLayout(TextPresenter presenter, TextLayout layout)
        {
            if (_inkLayout is not null && ReferenceEquals(layout, _sourceLayout))
            {
                return _inkLayout;
            }

            _inkLayout?.Dispose();
            _sourceLayout = layout;
            _inkLayout = new TextLayout(
                presenter.Text,
                new Typeface(presenter.FontFamily, presenter.FontStyle, presenter.FontWeight, presenter.FontStretch),
                presenter.FontSize,
                MatchForegroundBrush,
                presenter.TextAlignment,
                presenter.TextWrapping,
                flowDirection: presenter.FlowDirection,
                maxWidth: layout.MaxWidth,
                maxHeight: layout.MaxHeight,
                lineHeight: presenter.LineHeight,
                letterSpacing: presenter.LetterSpacing,
                fontFeatures: presenter.FontFeatures);
            return _inkLayout;
        }

        private void OnPresenterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (Matches.Count > 0)
            {
                InvalidateVisual();
            }
        }
    }
}
