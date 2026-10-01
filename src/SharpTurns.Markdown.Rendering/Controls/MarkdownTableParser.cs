namespace SharpTurns.Markdown.Rendering;

public enum MarkdownTableAlignment
{
    Left,
    Center,
    Right,
}

public sealed record MarkdownTableColumn(
    string Header,
    MarkdownTableAlignment Alignment);

public sealed record MarkdownTableBlock(
    IReadOnlyList<MarkdownTableColumn> Columns,
    IReadOnlyList<IReadOnlyList<string>> Rows,
    string SourceMarkdown);

public abstract record MarkdownDocumentSegment;

public sealed record MarkdownTextSegment(string Markdown) : MarkdownDocumentSegment;

public sealed record MarkdownTableSegment(MarkdownTableBlock Table) : MarkdownDocumentSegment;

public static class MarkdownTableParser
{
    public static MarkdownTableBlock? ParseFirst(string? markdown) =>
        ParseDocument(markdown).OfType<MarkdownTableSegment>().FirstOrDefault()?.Table;

    public static IReadOnlyList<MarkdownDocumentSegment> ParseDocument(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        var normalized = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var segments = new List<MarkdownDocumentSegment>();
        var pendingTextLines = new List<string>();
        var inFence = false;
        string? fenceMarker = null;

        for (var index = 0; index < lines.Length;)
        {
            var trimmedStart = lines[index].TrimStart();

            if (!inFence && TryParseTableAt(lines, index, out var table, out var consumedLines))
            {
                FlushPendingText(segments, pendingTextLines);
                segments.Add(new MarkdownTableSegment(table));
                index += consumedLines;
                continue;
            }

            pendingTextLines.Add(lines[index]);

            if (inFence)
            {
                if (fenceMarker is not null && IsClosingFenceLine(trimmedStart, fenceMarker))
                {
                    inFence = false;
                    fenceMarker = null;
                }
            }
            else if (TryGetFenceMarker(trimmedStart, out var marker))
            {
                inFence = true;
                fenceMarker = marker;
            }

            index++;
        }

        FlushPendingText(segments, pendingTextLines);
        return segments;
    }

    private static void FlushPendingText(List<MarkdownDocumentSegment> segments, List<string> pendingTextLines)
    {
        if (pendingTextLines.Count == 0)
        {
            return;
        }

        var markdown = string.Join('\n', pendingTextLines).Trim('\n');
        pendingTextLines.Clear();
        if (!string.IsNullOrWhiteSpace(markdown))
        {
            segments.Add(new MarkdownTextSegment(markdown));
        }
    }

    private static bool TryParseTableAt(
        IReadOnlyList<string> lines,
        int startIndex,
        out MarkdownTableBlock table,
        out int consumedLines)
    {
        table = new MarkdownTableBlock([], [], string.Empty);
        consumedLines = 0;

        if (startIndex >= lines.Count - 1 ||
            !TrySplitPipeRow(lines[startIndex], out var headerCells) ||
            !TryParseSeparator(lines[startIndex + 1], headerCells.Count, out var alignments))
        {
            return false;
        }

        var rows = new List<IReadOnlyList<string>>();
        var sourceLines = new List<string> { lines[startIndex], lines[startIndex + 1] };
        var rowIndex = startIndex + 2;
        while (rowIndex < lines.Count && TrySplitPipeRow(lines[rowIndex], out var rowCells))
        {
            rows.Add(NormalizeCellCount(rowCells, headerCells.Count));
            sourceLines.Add(lines[rowIndex]);
            rowIndex++;
        }

        var columns = headerCells
            .Select((header, index) => new MarkdownTableColumn(header, alignments[index]))
            .ToList();
        table = new MarkdownTableBlock(columns, rows, string.Join('\n', sourceLines));
        consumedLines = sourceLines.Count;
        return true;
    }

    private static bool TrySplitPipeRow(string line, out List<string> cells)
    {
        cells = [];
        if (string.IsNullOrWhiteSpace(line) || !line.Contains('|', StringComparison.Ordinal))
        {
            return false;
        }

        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed[0] == '|')
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.Length > 0 && trimmed[^1] == '|')
        {
            trimmed = trimmed[..^1];
        }

        cells = trimmed.Split('|').Select(cell => cell.Trim()).ToList();
        return cells.Count > 0 && cells.Any(cell => cell.Length > 0);
    }

    private static bool TryParseSeparator(
        string line,
        int expectedColumns,
        out List<MarkdownTableAlignment> alignments)
    {
        alignments = [];
        if (expectedColumns <= 0 || !TrySplitPipeRow(line, out var cells) || cells.Count != expectedColumns)
        {
            return false;
        }

        foreach (var cell in cells)
        {
            var trimmed = cell.Trim();
            if (trimmed.Length < 3)
            {
                return false;
            }

            var startColon = trimmed[0] == ':';
            var endColon = trimmed[^1] == ':';
            var dashSpan = trimmed.Trim(':');
            if (dashSpan.Length < 3 || dashSpan.Any(character => character != '-'))
            {
                return false;
            }

            alignments.Add((startColon, endColon) switch
            {
                (true, true) => MarkdownTableAlignment.Center,
                (false, true) => MarkdownTableAlignment.Right,
                _ => MarkdownTableAlignment.Left,
            });
        }

        return true;
    }

    private static IReadOnlyList<string> NormalizeCellCount(IReadOnlyList<string> cells, int columnCount)
    {
        if (cells.Count == columnCount)
        {
            return cells;
        }

        var normalized = new string[columnCount];
        for (var i = 0; i < normalized.Length; i++)
        {
            normalized[i] = i < cells.Count ? cells[i] : string.Empty;
        }

        return normalized;
    }

    private static bool TryGetFenceMarker(string trimmedStart, out string marker)
    {
        marker = string.Empty;
        if (trimmedStart.Length < 3)
        {
            return false;
        }

        var fenceChar = trimmedStart[0];
        if (fenceChar is not ('`' or '~') || trimmedStart[1] != fenceChar || trimmedStart[2] != fenceChar)
        {
            return false;
        }

        var length = 3;
        while (length < trimmedStart.Length && trimmedStart[length] == fenceChar)
        {
            length++;
        }

        marker = trimmedStart[..length];
        return true;
    }

    private static bool IsClosingFenceLine(string trimmedStart, string fenceMarker) =>
        trimmedStart.StartsWith(fenceMarker, StringComparison.Ordinal);
}
