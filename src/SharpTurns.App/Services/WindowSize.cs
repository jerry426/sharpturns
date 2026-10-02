using System.Globalization;

namespace SharpTurns.App.Services;

/// <summary>A window size in device-independent pixels, saved in the settings table as "width,height".</summary>
public readonly record struct WindowSize(double Width, double Height)
{
    public const double MaxDimension = 10000;

    public string Label => $"{FormatDimension(Width)} × {FormatDimension(Height)}";

    public string ToSetting() => $"{FormatDimension(Width)},{FormatDimension(Height)}";

    /// <summary>A missing or unreadable setting, or one outside the limits, reads as <paramref name="defaultSize"/>.</summary>
    public static WindowSize Parse(string? setting, WindowSize defaultSize, WindowSize minimum)
    {
        var parts = setting?.Split(',');
        return parts is [var width, var height]
               && TryParseDimension(width, minimum.Width, out var w) && TryParseDimension(height, minimum.Height, out var h)
            ? new WindowSize(w, h)
            : defaultSize;
    }

    /// <summary>A whole number of pixels from <paramref name="minimum"/> to <see cref="MaxDimension"/>.</summary>
    public static bool TryParseDimension(string? text, double minimum, out double dimension)
    {
        dimension = 0;
        if (!double.TryParse(text?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            || parsed < minimum || parsed > MaxDimension)
            return false;
        dimension = Math.Round(parsed);
        return true;
    }

    public static string FormatDimension(double value) => Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
}
