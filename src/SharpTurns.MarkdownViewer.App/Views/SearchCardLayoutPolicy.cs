namespace SharpTurns.MarkdownViewer.App.Views;

internal static class SearchCardLayoutPolicy
{
    public static bool ShouldWrapAppearanceControls(
        double availableWidth,
        double searchControlsWidth,
        double appearanceControlsWidth,
        double columnSpacing) =>
        availableWidth > 0 &&
        searchControlsWidth > 0 &&
        appearanceControlsWidth > 0 &&
        searchControlsWidth + columnSpacing + appearanceControlsWidth > availableWidth;
}
