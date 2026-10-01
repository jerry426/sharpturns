using Avalonia.Controls;

namespace SharpTurns.Markdown.Rendering;

/// <summary>
/// Search operations shared by rendered Markdown and raw-source document views.
/// </summary>
public interface ISearchableDocumentView
{
    int GetSearchMatchCount(string query);

    void ApplySearchHighlight(string query, int activeLocalIndex);

    void ClearSearchHighlight();

    Control? GetActiveMatchContainer();
}
