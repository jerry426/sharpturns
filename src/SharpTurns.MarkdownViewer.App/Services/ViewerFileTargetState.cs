namespace SharpTurns.MarkdownViewer.App.Services;

internal sealed class ViewerFileTargetState(string? initialFilePath)
{
    private string? _pendingFilePath = initialFilePath;

    public void MarkOpening(string filePath) => _pendingFilePath = filePath;

    public void MarkOpeningCompleted(string filePath)
    {
        if (PathsEqual(_pendingFilePath, filePath))
        {
            _pendingFilePath = null;
        }
    }

    public bool Matches(string filePath, string? currentFilePath) =>
        PathsEqual(_pendingFilePath, filePath) || PathsEqual(currentFilePath, filePath);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }
}
