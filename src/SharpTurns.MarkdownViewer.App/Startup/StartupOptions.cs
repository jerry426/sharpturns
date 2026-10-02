using System.Globalization;
using Avalonia;
using SharpTurns.Markdown.Rendering;

namespace SharpTurns.MarkdownViewer.App.Startup;

internal sealed record StartupOptions(
    string? MarkdownFilePath,
    string? WorkspaceDirectory,
    string? ErrorMessage)
{
    internal const double DefaultWindowWidth = MarkdownViewerWindowDefaults.DefaultWidth;
    internal const double DefaultWindowHeight = MarkdownViewerWindowDefaults.DefaultHeight;
    internal const double MinWindowWidth = MarkdownViewerWindowDefaults.MinWidth;
    internal const double MinWindowHeight = MarkdownViewerWindowDefaults.MinHeight;
    internal const double MaxWindowDimension = MarkdownViewerWindowDefaults.MaxDimension;

    public static StartupOptions Empty { get; } = new(null, null, null);

    public double? WindowWidth { get; init; }

    public double? WindowHeight { get; init; }

    public PixelPoint? ScreenPoint { get; init; }

    public static StartupOptions Parse(IReadOnlyList<string> arguments)
    {
        string? suppliedPath = null;
        string? workspaceDirectory = null;
        double? windowWidth = null;
        double? windowHeight = null;
        PixelPoint? screenPoint = null;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is "--workspace" or "--directory")
            {
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                {
                    return Invalid($"{argument} requires a directory path.");
                }

                workspaceDirectory = arguments[index];
                continue;
            }

            if (TryReadOptionValue(argument, "--workspace=", out var workspaceValue) ||
                TryReadOptionValue(argument, "--directory=", out workspaceValue))
            {
                if (string.IsNullOrWhiteSpace(workspaceValue))
                {
                    return Invalid("The workspace directory cannot be empty.");
                }

                workspaceDirectory = workspaceValue;
                continue;
            }

            if (argument is "--window-width" or "--window-height")
            {
                var optionName = argument;
                if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
                {
                    return Invalid($"{optionName} requires a window dimension.");
                }

                if (!TryParseWindowDimension(arguments[index], optionName, out var dimension, out var errorMessage))
                {
                    return Invalid(errorMessage);
                }

                if (optionName == "--window-width")
                {
                    windowWidth = dimension;
                }
                else
                {
                    windowHeight = dimension;
                }

                continue;
            }

            if (TryReadOptionValue(argument, "--window-width=", out var inlineWidth))
            {
                if (!TryParseWindowDimension(inlineWidth, "--window-width", out var dimension, out var errorMessage))
                {
                    return Invalid(errorMessage);
                }

                windowWidth = dimension;
                continue;
            }

            if (TryReadOptionValue(argument, "--window-height=", out var inlineHeight))
            {
                if (!TryParseWindowDimension(inlineHeight, "--window-height", out var dimension, out var errorMessage))
                {
                    return Invalid(errorMessage);
                }

                windowHeight = dimension;
                continue;
            }

            if (argument == "--screen-point" || argument.StartsWith("--screen-point=", StringComparison.Ordinal))
            {
                var value = argument == "--screen-point"
                    ? (++index < arguments.Count ? arguments[index] : string.Empty)
                    : argument["--screen-point=".Length..];
                var coordinates = value.Split(',');
                if (coordinates.Length != 2 ||
                    !int.TryParse(coordinates[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
                    !int.TryParse(coordinates[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
                {
                    return Invalid("--screen-point requires two integer pixel coordinates: x,y.");
                }

                screenPoint = new PixelPoint(x, y);
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                return Invalid($"Unrecognized startup option: {argument}");
            }

            if (suppliedPath is not null)
            {
                return Invalid("Only one Markdown file or directory may be supplied at startup.");
            }

            suppliedPath = argument;
        }

        var normalizedWorkspace = NormalizeDirectory(workspaceDirectory);
        if (workspaceDirectory is not null && normalizedWorkspace is null)
        {
            return Invalid($"The startup workspace does not exist: {workspaceDirectory}");
        }

        if (string.IsNullOrWhiteSpace(suppliedPath))
        {
            return new StartupOptions(null, normalizedWorkspace, null)
            {
                WindowWidth = windowWidth,
                WindowHeight = windowHeight,
                ScreenPoint = screenPoint,
            };
        }

        if (Uri.TryCreate(suppliedPath, UriKind.Absolute, out var remoteUri) &&
            (remoteUri.Scheme == Uri.UriSchemeHttp || remoteUri.Scheme == Uri.UriSchemeHttps))
        {
            if (!MarkdownDocumentLink.IsRemoteMarkdown(remoteUri))
            {
                return Invalid("The startup URL must reference a .md or .markdown document and must not include credentials.");
            }

            return new StartupOptions(remoteUri.AbsoluteUri, normalizedWorkspace, null)
            {
                WindowWidth = windowWidth,
                WindowHeight = windowHeight,
                ScreenPoint = screenPoint,
            };
        }

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(suppliedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid($"The startup path is invalid: {ex.Message}");
        }

        if (Directory.Exists(normalizedPath))
        {
            if (normalizedWorkspace is not null)
            {
                return Invalid("Supply either a positional directory or --workspace, not both.");
            }

            return new StartupOptions(null, normalizedPath, null)
            {
                WindowWidth = windowWidth,
                WindowHeight = windowHeight,
                ScreenPoint = screenPoint,
            };
        }

        if (!File.Exists(normalizedPath))
        {
            return Invalid($"The startup path does not exist: {normalizedPath}");
        }

        if (!IsMarkdownPath(normalizedPath))
        {
            return Invalid("The startup file must use the .md or .markdown extension.");
        }

        var initialDirectory = normalizedWorkspace ?? Path.GetDirectoryName(normalizedPath);
        return new StartupOptions(normalizedPath, initialDirectory, null)
        {
            WindowWidth = windowWidth,
            WindowHeight = windowHeight,
            ScreenPoint = screenPoint,
        };
    }

    private static StartupOptions Invalid(string message) =>
        new(null, null, message);

    private static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            var normalized = Path.GetFullPath(directory);
            return Directory.Exists(normalized) ? normalized : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryParseWindowDimension(
        string? value,
        string optionName,
        out double dimension,
        out string errorMessage)
    {
        if (!double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed))
        {
            dimension = 0;
            errorMessage = $"{optionName} must be a number between {FormatDimension(optionName == "--window-width" ? MinWindowWidth : MinWindowHeight)} and {FormatDimension(MaxWindowDimension)}.";
            return false;
        }

        var minimum = optionName == "--window-width" ? MinWindowWidth : MinWindowHeight;
        if (parsed < minimum || parsed > MaxWindowDimension)
        {
            dimension = 0;
            errorMessage = $"{optionName} must be between {FormatDimension(minimum)} and {FormatDimension(MaxWindowDimension)}.";
            return false;
        }

        dimension = Math.Round(parsed);
        errorMessage = string.Empty;
        return true;
    }

    private static string FormatDimension(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static bool IsMarkdownPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadOptionValue(string argument, string prefix, out string value)
    {
        if (argument.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = argument[prefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }
}
