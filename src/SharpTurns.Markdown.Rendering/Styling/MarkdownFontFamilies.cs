using Avalonia.Media;

namespace SharpTurns.Markdown.Rendering.Styling;

/// <summary>
/// Shared font families for rendered Markdown and conversation text. Sans text uses the
/// Inter family embedded through the Avalonia.Fonts.Inter package so every platform
/// renders the same typography instead of resolving a system fallback font.
/// </summary>
public static class MarkdownFontFamilies
{
    /// <summary>Embedded Inter (weights Thin through Bold ship with the package).</summary>
    public static readonly FontFamily Sans =
        new("avares://Avalonia.Fonts.Inter/Assets#Inter");

    /// <summary>
    /// Embedded DejaVu Sans Mono (Regular, Bold, Oblique, BoldOblique). System font
    /// fallbacks such as "Menlo" do not exist on Linux, where fontconfig substitutes a
    /// proportional font and silently breaks the monospace toggle, so embed the faces.
    /// </summary>
    public static readonly FontFamily Mono =
        new("avares://SharpTurns.Markdown.Rendering/Assets/Fonts#DejaVu Sans Mono");
}