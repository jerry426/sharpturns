using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpTurns.App.Services;

/// <summary>The DOCX export defaults: page layout, typography, and formatting, saved as JSON.</summary>
public sealed record DocxExportSettings(
    string PageSize,
    string Orientation,
    double MarginTopInches,
    double MarginRightInches,
    double MarginBottomInches,
    double MarginLeftInches,
    string NormalFontFamily,
    int NormalFontSizePt,
    int ParagraphSpacingAfterPt,
    string LineSpacing,
    string HeadingScale,
    string CodeFontFamily,
    int CodeFontSizePt,
    bool CodeBlockShading,
    bool IncludeTurnSeparators,
    bool IncludeEmojiHeadings,
    bool StartEachTurnOnNewPage)
{
    public static DocxExportSettings Default { get; } = new(
        PageSize: "letter",
        Orientation: "portrait",
        MarginTopInches: 1.0,
        MarginRightInches: 1.0,
        MarginBottomInches: 1.0,
        MarginLeftInches: 1.0,
        NormalFontFamily: "Aptos",
        NormalFontSizePt: 11,
        ParagraphSpacingAfterPt: 6,
        LineSpacing: "1.15",
        HeadingScale: "standard",
        CodeFontFamily: "Menlo",
        CodeFontSizePt: 9,
        CodeBlockShading: true,
        IncludeTurnSeparators: true,
        IncludeEmojiHeadings: true,
        StartEachTurnOnNewPage: false);

    public static IReadOnlyList<string> PageSizeOptions { get; } = ["letter", "a4"];

    public static IReadOnlyList<string> OrientationOptions { get; } = ["portrait", "landscape"];

    public static IReadOnlyList<double> MarginOptions { get; } = [0.5, 0.75, 1.0, 1.25, 1.5];

    public static IReadOnlyList<string> NormalFontOptions { get; } = ["Aptos", "Arial", "Helvetica", "Times New Roman", "Georgia", "Calibri"];

    public static IReadOnlyList<string> LineSpacingOptions { get; } = ["single", "1.15", "1.5", "double"];

    public static IReadOnlyList<string> HeadingScaleOptions { get; } = ["compact", "standard", "large"];

    public static IReadOnlyList<string> CodeFontOptions { get; } = ["Menlo", "Monaco", "Courier New", "Consolas"];

    public string SummaryLabel
    {
        get
        {
            var marginLabel = MarginsAreUniform
                ? string.Create(CultureInfo.InvariantCulture, $"{MarginTopInches:0.##}\" margins")
                : "custom margins";
            return $"{FormatOption(PageSize)} {Orientation}, {marginLabel}, {NormalFontFamily} {NormalFontSizePt}pt, {ParagraphSpacingAfterPt}pt paragraph spacing";
        }
    }

    public bool MarginsAreUniform => AreClose(MarginTopInches, MarginRightInches)
        && AreClose(MarginTopInches, MarginBottomInches)
        && AreClose(MarginTopInches, MarginLeftInches);

    public DocxExportSettings Normalize() => Normalize(this);

    public static DocxExportSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            return Normalize(node);
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    public static DocxExportSettings Normalize(DocxExportSettings settings) => Normalize(ToJsonObject(settings));

    public static string ToJson(DocxExportSettings settings) => ToJsonObject(settings.Normalize()).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public int PageWidthTwips
    {
        get
        {
            var (width, height) = PageSize == "a4" ? (11906, 16838) : (12240, 15840);
            return Orientation == "landscape" ? height : width;
        }
    }

    public int PageHeightTwips
    {
        get
        {
            var (width, height) = PageSize == "a4" ? (11906, 16838) : (12240, 15840);
            return Orientation == "landscape" ? width : height;
        }
    }

    public int MarginTopTwips => InchesToTwips(MarginTopInches);

    public int MarginRightTwips => InchesToTwips(MarginRightInches);

    public int MarginBottomTwips => InchesToTwips(MarginBottomInches);

    public int MarginLeftTwips => InchesToTwips(MarginLeftInches);

    public int NormalFontHalfPoints => PointsToHalfPoints(NormalFontSizePt);

    public int CodeFontHalfPoints => PointsToHalfPoints(CodeFontSizePt);

    public int ParagraphSpacingAfterTwips => PointsToTwips(ParagraphSpacingAfterPt);

    public int LineSpacingTwips => LineSpacing switch
    {
        "single" => 240,
        "1.5" => 360,
        "double" => 480,
        _ => 276,
    };

    public (int Heading1, int Heading2, int Heading3) HeadingHalfPoints => HeadingScale switch
    {
        "compact" => (28, 24, 22),
        "large" => (38, 32, 28),
        _ => (32, 28, 24),
    };

    private static DocxExportSettings Normalize(JsonObject? value)
    {
        value ??= [];
        var defaults = Default;
        return new DocxExportSettings(
            PageSize: ReadOption(value, "pageSize", defaults.PageSize, PageSizeOptions),
            Orientation: ReadOption(value, "orientation", defaults.Orientation, OrientationOptions),
            MarginTopInches: Clamp(ReadDouble(value, "marginTopInches", defaults.MarginTopInches), 0.25, 2.0),
            MarginRightInches: Clamp(ReadDouble(value, "marginRightInches", defaults.MarginRightInches), 0.25, 2.0),
            MarginBottomInches: Clamp(ReadDouble(value, "marginBottomInches", defaults.MarginBottomInches), 0.25, 2.0),
            MarginLeftInches: Clamp(ReadDouble(value, "marginLeftInches", defaults.MarginLeftInches), 0.25, 2.0),
            NormalFontFamily: ReadCleanString(value, "normalFontFamily", defaults.NormalFontFamily),
            NormalFontSizePt: Clamp(ReadInt(value, "normalFontSizePt", defaults.NormalFontSizePt), 9, 14),
            ParagraphSpacingAfterPt: Clamp(ReadInt(value, "paragraphSpacingAfterPt", defaults.ParagraphSpacingAfterPt), 0, 18),
            LineSpacing: ReadOption(value, "lineSpacing", defaults.LineSpacing, LineSpacingOptions),
            HeadingScale: ReadOption(value, "headingScale", defaults.HeadingScale, HeadingScaleOptions),
            CodeFontFamily: ReadCleanString(value, "codeFontFamily", defaults.CodeFontFamily),
            CodeFontSizePt: Clamp(ReadInt(value, "codeFontSizePt", defaults.CodeFontSizePt), 8, 12),
            CodeBlockShading: ReadBool(value, "codeBlockShading", defaults.CodeBlockShading),
            IncludeTurnSeparators: ReadBool(value, "includeTurnSeparators", defaults.IncludeTurnSeparators),
            IncludeEmojiHeadings: ReadBool(value, "includeEmojiHeadings", defaults.IncludeEmojiHeadings),
            StartEachTurnOnNewPage: ReadBool(value, "startEachTurnOnNewPage", defaults.StartEachTurnOnNewPage));
    }

    private static JsonObject ToJsonObject(DocxExportSettings settings) => new()
    {
        ["pageSize"] = settings.PageSize,
        ["orientation"] = settings.Orientation,
        ["marginTopInches"] = settings.MarginTopInches,
        ["marginRightInches"] = settings.MarginRightInches,
        ["marginBottomInches"] = settings.MarginBottomInches,
        ["marginLeftInches"] = settings.MarginLeftInches,
        ["normalFontFamily"] = settings.NormalFontFamily,
        ["normalFontSizePt"] = settings.NormalFontSizePt,
        ["paragraphSpacingAfterPt"] = settings.ParagraphSpacingAfterPt,
        ["lineSpacing"] = settings.LineSpacing,
        ["headingScale"] = settings.HeadingScale,
        ["codeFontFamily"] = settings.CodeFontFamily,
        ["codeFontSizePt"] = settings.CodeFontSizePt,
        ["codeBlockShading"] = settings.CodeBlockShading,
        ["includeTurnSeparators"] = settings.IncludeTurnSeparators,
        ["includeEmojiHeadings"] = settings.IncludeEmojiHeadings,
        ["startEachTurnOnNewPage"] = settings.StartEachTurnOnNewPage,
    };

    private static string ReadOption(JsonObject value, string propertyName, string fallback, IReadOnlyList<string> options)
    {
        var raw = ReadString(value, propertyName)?.Trim();
        return raw is not null && options.Contains(raw) ? raw : fallback;
    }

    private static string ReadCleanString(JsonObject value, string propertyName, string fallback)
    {
        var raw = ReadString(value, propertyName)?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return raw.Length > 80 ? raw[..80] : raw;
    }

    private static string? ReadString(JsonObject value, string propertyName)
    {
        try
        {
            return value[propertyName]?.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static double ReadDouble(JsonObject value, string propertyName, double fallback)
    {
        try
        {
            var node = value[propertyName];
            if (node is JsonValue jsonValue)
            {
                if (jsonValue.TryGetValue<double>(out var doubleValue))
                {
                    return doubleValue;
                }

                if (jsonValue.TryGetValue<string>(out var stringValue)
                    && double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue))
                {
                    return parsedValue;
                }
            }
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }

        return fallback;
    }

    private static int ReadInt(JsonObject value, string propertyName, int fallback)
    {
        try
        {
            var node = value[propertyName];
            if (node is JsonValue jsonValue)
            {
                if (jsonValue.TryGetValue<int>(out var intValue))
                {
                    return intValue;
                }

                if (jsonValue.TryGetValue<decimal>(out var decimalValue))
                {
                    return (int)Math.Round(decimalValue);
                }

                if (jsonValue.TryGetValue<string>(out var stringValue)
                    && int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
                {
                    return parsedValue;
                }
            }
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }

        return fallback;
    }

    private static bool ReadBool(JsonObject value, string propertyName, bool fallback)
    {
        try
        {
            return value[propertyName]?.GetValue<bool?>() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }

    private static double Clamp(double value, double minimum, double maximum) => Math.Round(Math.Max(minimum, Math.Min(maximum, value)), 2);

    private static int Clamp(int value, int minimum, int maximum) => Math.Max(minimum, Math.Min(maximum, value));

    private static int InchesToTwips(double value) => (int)Math.Round(value * 1440);

    private static int PointsToHalfPoints(int value) => (int)Math.Round(value * 2.0);

    private static int PointsToTwips(int value) => (int)Math.Round(value * 20.0);

    private static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.001;

    private static string FormatOption(string value) => value.Length == 0
        ? value
        : char.ToUpperInvariant(value[0]) + value[1..];
}
