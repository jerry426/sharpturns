using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.App.Services;

namespace SharpTurns.App.ViewModels;

/// <summary>The Workbench's DOCX export defaults dialog: edits a copy of the settings, which Save returns.</summary>
public sealed partial class DocxExportDefaultsDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private string _pageSize = "";

    [ObservableProperty]
    private string _orientation = "";

    [ObservableProperty]
    private double _marginTopInches;

    [ObservableProperty]
    private double _marginRightInches;

    [ObservableProperty]
    private double _marginBottomInches;

    [ObservableProperty]
    private double _marginLeftInches;

    [ObservableProperty]
    private string _normalFontFamily = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private decimal? _normalFontSizePt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private decimal? _paragraphSpacingAfterPt;

    [ObservableProperty]
    private string _lineSpacing = "";

    [ObservableProperty]
    private string _headingScale = "";

    [ObservableProperty]
    private string _codeFontFamily = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private decimal? _codeFontSizePt;

    [ObservableProperty]
    private bool _codeBlockShading;

    [ObservableProperty]
    private bool _includeTurnSeparators;

    [ObservableProperty]
    private bool _includeEmojiHeadings;

    [ObservableProperty]
    private bool _startEachTurnOnNewPage;

    public DocxExportDefaultsDialogViewModel(DocxExportSettings settings) => Apply(settings);

    public IReadOnlyList<string> PageSizeOptions => DocxExportSettings.PageSizeOptions;

    public IReadOnlyList<string> OrientationOptions => DocxExportSettings.OrientationOptions;

    public IReadOnlyList<double> MarginOptions => DocxExportSettings.MarginOptions;

    public IReadOnlyList<string> NormalFontOptions => DocxExportSettings.NormalFontOptions;

    public IReadOnlyList<string> LineSpacingOptions => DocxExportSettings.LineSpacingOptions;

    public IReadOnlyList<string> HeadingScaleOptions => DocxExportSettings.HeadingScaleOptions;

    public IReadOnlyList<string> CodeFontOptions => DocxExportSettings.CodeFontOptions;

    /// <summary>The number boxes keep their values in range, but can be left empty.</summary>
    public bool CanSave => NormalFontSizePt is not null && ParagraphSpacingAfterPt is not null && CodeFontSizePt is not null;

    [RelayCommand]
    private void ResetToDefaults() => Apply(DocxExportSettings.Default);

    public DocxExportSettings CreateSettings() => new DocxExportSettings(
        PageSize,
        Orientation,
        MarginTopInches,
        MarginRightInches,
        MarginBottomInches,
        MarginLeftInches,
        NormalFontFamily,
        (int)Math.Round(NormalFontSizePt.GetValueOrDefault()),
        (int)Math.Round(ParagraphSpacingAfterPt.GetValueOrDefault()),
        LineSpacing,
        HeadingScale,
        CodeFontFamily,
        (int)Math.Round(CodeFontSizePt.GetValueOrDefault()),
        CodeBlockShading,
        IncludeTurnSeparators,
        IncludeEmojiHeadings,
        StartEachTurnOnNewPage).Normalize();

    private void Apply(DocxExportSettings settings)
    {
        var normalized = settings.Normalize();
        PageSize = normalized.PageSize;
        Orientation = normalized.Orientation;
        MarginTopInches = normalized.MarginTopInches;
        MarginRightInches = normalized.MarginRightInches;
        MarginBottomInches = normalized.MarginBottomInches;
        MarginLeftInches = normalized.MarginLeftInches;
        NormalFontFamily = normalized.NormalFontFamily;
        NormalFontSizePt = normalized.NormalFontSizePt;
        ParagraphSpacingAfterPt = normalized.ParagraphSpacingAfterPt;
        LineSpacing = normalized.LineSpacing;
        HeadingScale = normalized.HeadingScale;
        CodeFontFamily = normalized.CodeFontFamily;
        CodeFontSizePt = normalized.CodeFontSizePt;
        CodeBlockShading = normalized.CodeBlockShading;
        IncludeTurnSeparators = normalized.IncludeTurnSeparators;
        IncludeEmojiHeadings = normalized.IncludeEmojiHeadings;
        StartEachTurnOnNewPage = normalized.StartEachTurnOnNewPage;
    }
}
