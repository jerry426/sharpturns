using Avalonia.Media;

namespace SharpTurns.Markdown.Rendering.Styling;

public static class OklchColorUtility
{
    public const double MinimumTextIntensity = 0.6;
    public const double DefaultTextIntensity = 1.0;
    public const double MaximumTextIntensity = 1.4;

    private const double LightnessBoostAtMaximum = 0.50;
    private const double LightnessReductionAtMinimum = 0.45;
    private const double ChromaBoostAtMaximum = 0.12;
    private const double ChromaReductionAtMinimum = 0.20;

    public static Color AdjustTextIntensity(Color color, double intensity)
    {
        intensity = ClampIntensity(intensity);
        if (Math.Abs(intensity - DefaultTextIntensity) < 0.000_001)
        {
            return color;
        }

        var oklch = ToOklch(color);
        var adjusted = intensity > DefaultTextIntensity
            ? Brighten(oklch, intensity)
            : Dim(oklch, intensity);

        return FromOklchInGamut(adjusted, color.A);
    }

    public static double ClampIntensity(double value)
    {
        if (double.IsNaN(value))
        {
            return DefaultTextIntensity;
        }

        return Math.Min(MaximumTextIntensity, Math.Max(MinimumTextIntensity, value));
    }

    public static OklchColor ToOklch(Color color)
    {
        var red = SrgbToLinear(color.R / 255.0);
        var green = SrgbToLinear(color.G / 255.0);
        var blue = SrgbToLinear(color.B / 255.0);

        var l = 0.4122214708 * red + 0.5363325363 * green + 0.0514459929 * blue;
        var m = 0.2119034982 * red + 0.6806995451 * green + 0.1073969566 * blue;
        var s = 0.0883024619 * red + 0.2817188376 * green + 0.6299787005 * blue;

        var lRoot = Math.Cbrt(l);
        var mRoot = Math.Cbrt(m);
        var sRoot = Math.Cbrt(s);

        var labL = 0.2104542553 * lRoot + 0.7936177850 * mRoot - 0.0040720468 * sRoot;
        var labA = 1.9779984951 * lRoot - 2.4285922050 * mRoot + 0.4505937099 * sRoot;
        var labB = 0.0259040371 * lRoot + 0.7827717662 * mRoot - 0.8086757660 * sRoot;

        var chroma = Math.Sqrt(labA * labA + labB * labB);
        var hue = Math.Atan2(labB, labA) * 180 / Math.PI;
        if (hue < 0)
        {
            hue += 360;
        }

        return new OklchColor(labL, chroma, hue);
    }

    private static OklchColor Brighten(OklchColor color, double intensity)
    {
        var amount = (intensity - DefaultTextIntensity) / (MaximumTextIntensity - DefaultTextIntensity);
        var lightness = color.Lightness + ((1 - color.Lightness) * amount * LightnessBoostAtMaximum);
        var chroma = color.Chroma * (1 + (amount * ChromaBoostAtMaximum));
        return new OklchColor(Clamp01(lightness), chroma, color.HueDegrees);
    }

    private static OklchColor Dim(OklchColor color, double intensity)
    {
        var amount = (DefaultTextIntensity - intensity) / (DefaultTextIntensity - MinimumTextIntensity);
        var lightness = color.Lightness * (1 - (amount * LightnessReductionAtMinimum));
        var chroma = color.Chroma * (1 - (amount * ChromaReductionAtMinimum));
        return new OklchColor(Clamp01(lightness), Math.Max(0, chroma), color.HueDegrees);
    }

    private static Color FromOklchInGamut(OklchColor color, byte alpha)
    {
        if (TryConvertToSrgb(color, alpha, out var converted))
        {
            return converted;
        }

        var low = 0.0;
        var high = color.Chroma;
        var best = new OklchColor(color.Lightness, 0, color.HueDegrees);

        for (var i = 0; i < 28; i++)
        {
            var mid = (low + high) / 2;
            var candidate = new OklchColor(color.Lightness, mid, color.HueDegrees);
            if (TryConvertToSrgb(candidate, alpha, out _))
            {
                best = candidate;
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        _ = TryConvertToSrgb(best, alpha, out converted);
        return converted;
    }

    private static bool TryConvertToSrgb(OklchColor color, byte alpha, out Color converted)
    {
        var hueRadians = color.HueDegrees * Math.PI / 180;
        var labA = color.Chroma * Math.Cos(hueRadians);
        var labB = color.Chroma * Math.Sin(hueRadians);

        var lRoot = color.Lightness + 0.3963377774 * labA + 0.2158037573 * labB;
        var mRoot = color.Lightness - 0.1055613458 * labA - 0.0638541728 * labB;
        var sRoot = color.Lightness - 0.0894841775 * labA - 1.2914855480 * labB;

        var l = lRoot * lRoot * lRoot;
        var m = mRoot * mRoot * mRoot;
        var s = sRoot * sRoot * sRoot;

        var redLinear = +4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        var greenLinear = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        var blueLinear = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        var inGamut = IsInGamut(redLinear) && IsInGamut(greenLinear) && IsInGamut(blueLinear);
        converted = Color.FromArgb(
            alpha,
            ToByte(LinearToSrgb(redLinear)),
            ToByte(LinearToSrgb(greenLinear)),
            ToByte(LinearToSrgb(blueLinear)));
        return inGamut;
    }

    private static double SrgbToLinear(double value) => value <= 0.04045
        ? value / 12.92
        : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double value) => value <= 0.0031308
        ? 12.92 * value
        : (1.055 * Math.Pow(Math.Max(0, value), 1 / 2.4)) - 0.055;

    private static bool IsInGamut(double value) => value >= -0.000_000_5 && value <= 1.000_000_5;

    private static byte ToByte(double value) => (byte)Math.Round(Clamp01(value) * 255, MidpointRounding.AwayFromZero);

    private static double Clamp01(double value) => Math.Min(1, Math.Max(0, value));
}

public readonly record struct OklchColor(double Lightness, double Chroma, double HueDegrees);
