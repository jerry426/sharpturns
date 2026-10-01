using Avalonia;
using Avalonia.Controls;

namespace SharpTurns.Markdown.Rendering;

/// <summary>
/// A read-only <see cref="TextBox"/> used for raw conversation content. Using
/// Avalonia's text editor rather than its text-display controls provides native,
/// reliable drag selection and clipboard behavior for multiline text.
/// </summary>
/// <remarks>
/// The active in-context search result is represented by the editor selection.
/// Avalonia's text editor does not expose multiple independent highlight ranges,
/// so inactive matches are counted but are not decorated in raw-text mode.
/// </remarks>
public sealed class HighlightableTextBlock : TextBox
{
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
    /// Selects the active search match. A value of -1 counts matches without
    /// changing the user's visible raw-text selection.
    /// </summary>
    public void ApplySearchHighlight(string query, int activeLocalIndex)
    {
        _searchQuery = query;
        _searchActiveLocalIndex = activeLocalIndex;
        ApplyActiveSearchSelection();
    }

    /// <summary>
    /// Removes all search highlighting and returns to plain <see cref="Text"/>
    /// rendering.
    /// </summary>
    public void ClearSearchHighlight()
    {
        _searchQuery = null;
        _searchActiveLocalIndex = -1;
        ClearSelection();
    }

    /// <summary>
    /// Returns the visual representing the active match (for scroll-into-view),
    /// or null when this block holds no active match.
    /// </summary>
    public Control? GetActiveMatchContainer()
        => SelectionStart != SelectionEnd && _searchActiveLocalIndex >= 0 ? this : null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty && !string.IsNullOrEmpty(_searchQuery))
        {
            ApplyActiveSearchSelection();
        }
    }

    private void ApplyActiveSearchSelection()
    {
        var text = Text ?? string.Empty;
        if (string.IsNullOrEmpty(_searchQuery) || _searchActiveLocalIndex < 0)
        {
            ClearSelection();
            return;
        }

        var matches = SearchHighlight.FindMatches(text, _searchQuery!);
        if (_searchActiveLocalIndex >= matches.Count)
        {
            ClearSelection();
            return;
        }

        var match = matches[_searchActiveLocalIndex];
        SelectionStart = match.Start;
        SelectionEnd = match.Start + match.Length;
    }
}
