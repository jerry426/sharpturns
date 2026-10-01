using LiveMarkdown.Avalonia;

namespace SharpTurns.Markdown.Rendering;

public static class MarkdownSearchTextProjector
{
    public static IReadOnlyList<string> Project(
        string? markdown,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var searchableText = new List<string>();
        var projector = new MarkdownTextProjector();
        foreach (var segment in MarkdownTableParser.ParseDocument(markdown))
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (segment)
            {
                case MarkdownTextSegment text:
                    var projection = projector.Project(
                        new ObservableStringBuilderSnapshot(text.Markdown, Version: 0),
                        cancellationToken);
                    searchableText.AddRange(projection.Buffers.Select(buffer => buffer.Text.ToString()));
                    break;

                case MarkdownTableSegment table:
                    AppendTableText(searchableText, table.Table);
                    break;
            }
        }

        return searchableText;
    }

    public static int CountMatches(
        string? markdown,
        TextSearchPattern pattern,
        CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var text in Project(markdown, cancellationToken))
        {
            count += pattern.FindRanges(text).Count();
        }

        return count;
    }

    internal static string GetTableCellDisplayText(string sourceText) =>
        sourceText.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);

    private static void AppendTableText(List<string> searchableText, MarkdownTableBlock table)
    {
        searchableText.AddRange(table.Columns.Select(column => GetTableCellDisplayText(column.Header)));
        foreach (var row in table.Rows)
        {
            searchableText.AddRange(row.Select(GetTableCellDisplayText));
        }
    }
}
