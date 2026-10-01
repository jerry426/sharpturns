namespace SharpTurns.Markdown.Rendering;

/// <summary>
/// Shared, case-insensitive search-match helpers used by the conversation
/// search highlighter (<see cref="HighlightableTextBlock"/> and
/// <see cref="MarkdownContentBlock"/>). Matching is non-overlapping, matching
/// the legacy Python QML <c>indexOf</c> search behavior.
/// </summary>
internal static class SearchHighlight
{
    public readonly record struct Match(int Start, int Length);

    /// <summary>
    /// Finds all non-overlapping, case-insensitive matches of <paramref name="query"/>
    /// within <paramref name="text"/>.
    /// </summary>
    public static List<Match> FindMatches(string text, string query)
    {
        var results = new List<Match>();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
        {
            return results;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        var i = 0;
        while (i <= text.Length - query.Length)
        {
            var idx = text.IndexOf(query, i, comparison);
            if (idx < 0)
            {
                break;
            }

            results.Add(new Match(idx, query.Length));
            i = idx + query.Length;
        }

        return results;
    }

    /// <summary>
    /// Counts all non-overlapping, case-insensitive matches without allocating
    /// the match list.
    /// </summary>
    public static int CountMatches(string text, string query)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        var count = 0;
        var i = 0;
        while (i <= text.Length - query.Length)
        {
            var idx = text.IndexOf(query, i, comparison);
            if (idx < 0)
            {
                break;
            }

            count++;
            i = idx + query.Length;
        }

        return count;
    }
}