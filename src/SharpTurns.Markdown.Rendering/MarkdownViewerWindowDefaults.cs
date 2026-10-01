using System.Globalization;

using Avalonia;
using Avalonia.Controls;

namespace SharpTurns.Markdown.Rendering;

/// <summary>
/// Shared default and validation dimensions for the standalone Markdown Viewer window.
/// </summary>
public static class MarkdownViewerWindowDefaults
{
    public const double DefaultWidth = 1400;
    public const double DefaultHeight = 1200;
    public const double DefaultExplorerWidth = 450;
    public const double MinExplorerWidth = 220;
    public const double MinDocumentWidth = 300;
    public const double MinWidth = 760;
    public const double MinHeight = 500;
    public const double MaxDimension = 10000;

    public static GridLength DefaultExplorerColumnWidth =>
        new(DefaultExplorerWidth, GridUnitType.Pixel);

    public static string DefaultWidthText => FormatDimension(DefaultWidth);

    public static string DefaultHeightText => FormatDimension(DefaultHeight);

    public static string FormatDimension(double value) =>
        Math.Round(value).ToString("0", CultureInfo.InvariantCulture);
}
