using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Markdown.Rendering.Styling;

namespace SharpTurns.MarkdownViewer.App.ViewModels;

public sealed class MarkdownViewerAppearanceViewModel : ObservableObject
{
    public const double BaseFontSize = 15;
    public const double MinimumFontSizeOffset = -5;
    public const double MaximumFontSizeOffset = 5;
    public const double DefaultFontSizeOffset = 0;
    public const double MinimumBackgroundIntensity = 0;
    public const double MaximumBackgroundIntensity = 1.5;
    public const double DefaultBackgroundIntensity = 0.85;
    public const double MinimumTextIntensity = OklchColorUtility.MinimumTextIntensity;
    public const double MaximumTextIntensity = OklchColorUtility.MaximumTextIntensity;
    public const double DefaultTextIntensity = 0.9;

    private static readonly Color DocumentBackgroundBase = Color.Parse("#151923");
    private double _fontSizeOffset = DefaultFontSizeOffset;
    private double _backgroundIntensity = DefaultBackgroundIntensity;
    private double _textIntensity = DefaultTextIntensity;

    public MarkdownViewerAppearanceViewModel()
    {
        ResetCommand = new RelayCommand(Reset);
    }

    public double FontSizeOffset
    {
        get => _fontSizeOffset;
        set
        {
            var clamped = Clamp(value, MinimumFontSizeOffset, MaximumFontSizeOffset);
            if (SetProperty(ref _fontSizeOffset, clamped))
            {
                OnPropertyChanged(nameof(FontSize));
                OnPropertyChanged(nameof(FontSizeLabel));
                OnPropertyChanged(nameof(LineHeight));
                OnPropertyChanged(nameof(CodeFontSize));
            }
        }
    }

    public double FontSize => BaseFontSize + FontSizeOffset;

    public string FontSizeLabel => $"{FontSize:0}px";

    public double LineHeight => FontSize + 7;

    public double CodeFontSize => Math.Max(10, FontSize - 2);

    public double BackgroundIntensity
    {
        get => _backgroundIntensity;
        set
        {
            var clamped = Clamp(value, MinimumBackgroundIntensity, MaximumBackgroundIntensity);
            if (SetProperty(ref _backgroundIntensity, clamped))
            {
                OnPropertyChanged(nameof(BackgroundIntensityLabel));
                OnPropertyChanged(nameof(DocumentBackground));
            }
        }
    }

    public string BackgroundIntensityLabel => $"{BackgroundIntensity:P0}";

    public IBrush DocumentBackground => new SolidColorBrush(ScaleBrightness(DocumentBackgroundBase, BackgroundIntensity));

    public double TextIntensity
    {
        get => _textIntensity;
        set
        {
            var clamped = OklchColorUtility.ClampIntensity(value);
            if (SetProperty(ref _textIntensity, clamped))
            {
                OnPropertyChanged(nameof(TextIntensityLabel));
                OnPropertyChanged(nameof(ContentForeground));
                OnPropertyChanged(nameof(InlineCodeForeground));
                OnPropertyChanged(nameof(IdentifierForeground));
            }
        }
    }

    public string TextIntensityLabel => $"{TextIntensity:P0}";

    public IBrush ContentForeground => TextBrush("#CFD7E6");

    public IBrush InlineCodeForeground => TextBrush("#E5C07B");

    public IBrush IdentifierForeground => TextBrush("#00FFFF");

    public IRelayCommand ResetCommand { get; }

    private void Reset()
    {
        FontSizeOffset = DefaultFontSizeOffset;
        BackgroundIntensity = DefaultBackgroundIntensity;
        TextIntensity = DefaultTextIntensity;
    }

    private IBrush TextBrush(string color) => new SolidColorBrush(
        OklchColorUtility.AdjustTextIntensity(Color.Parse(color), TextIntensity));

    private static double Clamp(double value, double minimum, double maximum)
    {
        if (double.IsNaN(value))
        {
            return minimum;
        }

        return Math.Min(maximum, Math.Max(minimum, value));
    }

    private static Color ScaleBrightness(Color color, double intensity) => Color.FromRgb(
        ScaleChannel(color.R, intensity),
        ScaleChannel(color.G, intensity),
        ScaleChannel(color.B, intensity));

    private static byte ScaleChannel(byte value, double intensity) =>
        (byte)Math.Round(Math.Min(255, Math.Max(0, value * intensity)));
}
