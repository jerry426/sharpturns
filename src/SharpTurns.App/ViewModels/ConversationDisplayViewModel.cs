using System.Globalization;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Markdown.Rendering.Styling;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The conversation display controls and search, shared by every conversation. They last until the app closes. Font,
/// background, and text changes update application resources that the views use through DynamicResource.
/// </summary>
public sealed partial class ConversationDisplayViewModel : ObservableObject
{
    public const double BaseFontSize = 15;
    public const int AllTurns = 0;
    public const int VisibleTurns = 1;
    public const int HiddenTurns = 2;
    private const double DefaultBackgroundIntensity = 0.85;
    private const double DefaultTextIntensity = 0.9;
    private static readonly Color TurnBackground = Color.Parse("#151923");
    private static readonly Color CompressedTurnBackground = Color.Parse("#0E1B2A");
    // The Text*Brush resources' colors from App.axaml, which the text intensity adjusts.
    private readonly Dictionary<string, Color> _textColors = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMarkdownSourceShown))]
    private bool _isMarkdownRenderingEnabled = true;

    [ObservableProperty]
    private bool _isMonospaceFontEnabled;

    /// <summary>Opens or closes every compressed card's Work Summary; closed by default.</summary>
    [ObservableProperty]
    private bool _isWorkSummaryExpanded;

    /// <summary>Keeps the conversation scrolled to the bottom as turns stream; off, the reader controls the scroll.</summary>
    [ObservableProperty]
    private bool _isAutoScrollEnabled = true;

    /// <summary>Added to the 15 px base, from -5 to 5.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontSizeLabel))]
    private double _fontSizeOffset;

    /// <summary>Scales the turn cards' background brightness, from 0 to 1.5.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundIntensityLabel))]
    private double _backgroundIntensity = DefaultBackgroundIntensity;

    /// <summary>Dims or brightens the app's text colors, from 0.6 to 1.4.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextIntensityLabel))]
    private double _textIntensity = DefaultTextIntensity;

    /// <summary>The Show picker: all turns, the turns in Claude's context, or the hidden ones.</summary>
    [ObservableProperty]
    private int _turnFilterIndex = VisibleTurns;

    /// <summary>Search in turns. A new query clears the active match.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearchActive), nameof(SearchMatchStatusLabel))]
    [NotifyCanExecuteChangedFor(nameof(ClearSearchCommand))]
    private string _searchQuery = "";

    /// <summary>The match navigated to, across the shown turns; -1 until the user moves to one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchMatchStatusLabel))]
    private int _currentMatchIndex = -1;

    /// <summary>Set by the view once it has counted the matches in the shown turns.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchMatchStatusLabel))]
    [NotifyCanExecuteChangedFor(nameof(NextSearchMatchCommand), nameof(PreviousSearchMatchCommand))]
    private int _totalSearchMatches;

    public ConversationDisplayViewModel()
    {
        if (Application.Current?.Resources is { } resources)
        {
            // Resolved by key: enumerating the dictionary can return XAML's deferred entries instead of the brushes.
            foreach (var name in resources.Keys.OfType<string>().ToArray())
            {
                if (name.StartsWith("Text", StringComparison.Ordinal) && name.EndsWith("Brush", StringComparison.Ordinal)
                    && resources.TryGetResource(name, null, out var value) && value is ISolidColorBrush brush)
                    _textColors[name] = brush.Color;
            }
        }
        ApplyResources();
    }

    public bool IsMarkdownSourceShown => !IsMarkdownRenderingEnabled;

    public double FontSize => BaseFontSize + FontSizeOffset;

    public string FontSizeLabel => string.Create(CultureInfo.CurrentCulture, $"{FontSize:0}px");

    public string BackgroundIntensityLabel => BackgroundIntensity.ToString("P0", CultureInfo.CurrentCulture);

    public string TextIntensityLabel => TextIntensity.ToString("P0", CultureInfo.CurrentCulture);

    public IReadOnlyList<string> TurnFilterOptions { get; } = ["All Turns", "Visible", "Hidden"];

    /// <summary>Whether the Show picker includes a turn; a live turn counts as visible.</summary>
    public bool Shows(TurnViewModel turn) => TurnFilterIndex switch
    {
        VisibleTurns => turn.IsHydrated,
        HiddenTurns => !turn.IsHydrated,
        _ => true,
    };

    public bool IsSearchActive => !string.IsNullOrWhiteSpace(SearchQuery);

    /// <summary>"3 of 12", "12 matches", or "0 matches"; empty without a query.</summary>
    public string SearchMatchStatusLabel => !IsSearchActive ? ""
        : TotalSearchMatches == 0 ? "0 matches"
        : CurrentMatchIndex >= 0 ? string.Create(CultureInfo.CurrentCulture, $"{CurrentMatchIndex + 1:N0} of {TotalSearchMatches:N0}")
        : string.Create(CultureInfo.CurrentCulture, $"{TotalSearchMatches:N0} match{(TotalSearchMatches == 1 ? "" : "es")}");

    partial void OnSearchQueryChanged(string value) => CurrentMatchIndex = -1;

    /// <summary>Keeps the active match in range when the matches shrink.</summary>
    public void SetSearchTotalMatches(int count)
    {
        TotalSearchMatches = count;
        if (CurrentMatchIndex >= count) CurrentMatchIndex = count - 1;
    }

    private bool CanNavigateSearchMatch() => TotalSearchMatches > 0;

    // Both directions wrap around.
    [RelayCommand(CanExecute = nameof(CanNavigateSearchMatch))]
    private void NextSearchMatch() => CurrentMatchIndex = CurrentMatchIndex + 1 >= TotalSearchMatches ? 0 : CurrentMatchIndex + 1;

    [RelayCommand(CanExecute = nameof(CanNavigateSearchMatch))]
    private void PreviousSearchMatch() => CurrentMatchIndex = CurrentMatchIndex <= 0 ? TotalSearchMatches - 1 : CurrentMatchIndex - 1;

    [RelayCommand(CanExecute = nameof(IsSearchActive))]
    private void ClearSearch() => SearchQuery = "";

    [RelayCommand]
    private void Reset()
    {
        IsMarkdownRenderingEnabled = true;
        IsMonospaceFontEnabled = false;
        IsWorkSummaryExpanded = false;
        FontSizeOffset = 0;
        BackgroundIntensity = DefaultBackgroundIntensity;
        TextIntensity = DefaultTextIntensity;
    }

    partial void OnIsMonospaceFontEnabledChanged(bool value) => ApplyResources();

    partial void OnFontSizeOffsetChanged(double value) => ApplyResources();

    partial void OnBackgroundIntensityChanged(double value) => ApplyResources();

    partial void OnTextIntensityChanged(double value) => ApplyResources();

    private void ApplyResources()
    {
        if (Application.Current?.Resources is not { } resources) return;
        resources["ConversationFontSize"] = FontSize;
        resources["ConversationLineHeight"] = FontSize + 7;
        resources["ConversationCodeFontSize"] = Math.Max(10, FontSize - 2);
        resources["ConversationFontFamily"] = IsMonospaceFontEnabled ? MarkdownFontFamilies.Mono : MarkdownFontFamilies.Sans;
        resources["TurnBackgroundBrush"] = new SolidColorBrush(Scale(TurnBackground, BackgroundIntensity));
        resources["CompressedTurnBackgroundBrush"] = new SolidColorBrush(Scale(CompressedTurnBackground, BackgroundIntensity));
        resources["ConversationTextIntensity"] = TextIntensity;
        foreach (var (key, color) in _textColors)
            resources[key] = new SolidColorBrush(OklchColorUtility.AdjustTextIntensity(color, TextIntensity));
    }

    private static Color Scale(Color color, double intensity) =>
        Color.FromRgb(Channel(color.R, intensity), Channel(color.G, intensity), Channel(color.B, intensity));

    private static byte Channel(byte value, double intensity) => (byte)Math.Round(Math.Clamp(value * intensity, 0, 255));
}
