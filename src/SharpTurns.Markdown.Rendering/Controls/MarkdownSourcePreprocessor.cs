namespace SharpTurns.Markdown.Rendering;

// Rewrites Markdown source before LiveMarkdown parses it. Task-list markers
// become checkbox glyphs, and surplus blank lines become marker paragraphs
// that MarkdownContentBlock replaces with native-height spacers.
internal static class MarkdownSourcePreprocessor
{
    private const string PreservedBlankLineMarkerSource = "&#8203;&#8288;&#8203;";
    private const string PreservedBlankLineMarkerText = "\u200B\u2060\u200B";

    internal static bool MayContainInlineCode(string markdown) => markdown.Contains('`', StringComparison.Ordinal);

    internal static bool MayContainTaskList(string markdown) => markdown.Contains("[ ]", StringComparison.Ordinal)
        || markdown.Contains("[x]", StringComparison.OrdinalIgnoreCase);

    internal static bool MayContainConsecutiveBlankLines(string markdown)
    {
        var consecutiveBlankLines = 0;
        foreach (var line in NormalizeLineEndings(markdown).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                consecutiveBlankLines++;
                if (consecutiveBlankLines >= 2)
                {
                    return true;
                }
            }
            else
            {
                consecutiveBlankLines = 0;
            }
        }

        return false;
    }

    internal static string PrepareForRendering(string markdown)
    {
        var mayContainTaskList = MayContainTaskList(markdown);
        var mayContainConsecutiveBlankLines = MayContainConsecutiveBlankLines(markdown);
        if (!mayContainTaskList && !mayContainConsecutiveBlankLines)
        {
            return markdown;
        }

        var normalized = NormalizeLineEndings(markdown);
        var lines = normalized.Split('\n');
        var changed = false;
        if (mayContainTaskList)
        {
            for (var index = 0; index < lines.Length; index++)
            {
                if (TryRenderTaskListLine(lines[index], out var renderedLine))
                {
                    lines[index] = renderedLine;
                    changed = true;
                }
            }
        }

        var rendered = mayContainConsecutiveBlankLines
            ? PreserveConsecutiveBlankLines(lines, ref changed)
            : string.Join('\n', lines);
        return changed ? rendered : markdown;
    }

    private static string NormalizeLineEndings(string markdown) =>
        markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string PreserveConsecutiveBlankLines(IReadOnlyList<string> lines, ref bool changed)
    {
        var renderedLines = new List<string>(lines.Count);
        var inFence = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;

        for (var index = 0; index < lines.Count;)
        {
            var line = lines[index];
            if (!string.IsNullOrWhiteSpace(line))
            {
                renderedLines.Add(line);
                UpdateFenceState(line, ref inFence, ref fenceCharacter, ref fenceLength);
                index++;
                continue;
            }

            var blankRunEnd = index + 1;
            while (blankRunEnd < lines.Count && string.IsNullOrWhiteSpace(lines[blankRunEnd]))
            {
                blankRunEnd++;
            }

            var blankLineCount = blankRunEnd - index;
            var isBoundedByContent = index > 0 && blankRunEnd < lines.Count;
            if (inFence || blankLineCount < 2 || !isBoundedByContent ||
                IsIndentedCodeGap(lines[index - 1], lines[blankRunEnd]))
            {
                for (var blankIndex = index; blankIndex < blankRunEnd; blankIndex++)
                {
                    renderedLines.Add(lines[blankIndex]);
                }

                index = blankRunEnd;
                continue;
            }

            // CommonMark collapses every run of blank lines into one block
            // separator. Keep that first separator unchanged, then encode the
            // surplus lines in an isolated zero-width paragraph. The rendered
            // paragraph is replaced with a native-height spacer after parsing,
            // so marker characters never remain selectable or copyable.
            renderedLines.Add(lines[index]);
            var surplusBlankLineCount = blankLineCount - 1;
            for (var surplusIndex = 0; surplusIndex < surplusBlankLineCount; surplusIndex++)
            {
                var hardBreakSuffix = surplusIndex < surplusBlankLineCount - 1 ? "  " : string.Empty;
                renderedLines.Add(PreservedBlankLineMarkerSource + hardBreakSuffix);
            }

            renderedLines.Add(string.Empty);
            changed = true;
            index = blankRunEnd;
        }

        return string.Join('\n', renderedLines);
    }

    private static void UpdateFenceState(
        string line,
        ref bool inFence,
        ref char fenceCharacter,
        ref int fenceLength)
    {
        if (!TryGetFenceRun(line, out var character, out var length, out var hasOnlyTrailingWhitespace))
        {
            return;
        }

        if (!inFence)
        {
            inFence = true;
            fenceCharacter = character;
            fenceLength = length;
        }
        else if (character == fenceCharacter && length >= fenceLength && hasOnlyTrailingWhitespace)
        {
            inFence = false;
            fenceCharacter = '\0';
            fenceLength = 0;
        }
    }

    private static bool TryGetFenceRun(
        string line,
        out char fenceCharacter,
        out int fenceLength,
        out bool hasOnlyTrailingWhitespace)
    {
        fenceCharacter = '\0';
        fenceLength = 0;
        hasOnlyTrailingWhitespace = false;

        var markerStart = 0;
        while (markerStart < line.Length && line[markerStart] == ' ')
        {
            markerStart++;
        }

        if (markerStart > 3 || markerStart >= line.Length || line[markerStart] is not ('`' or '~'))
        {
            return false;
        }

        fenceCharacter = line[markerStart];
        var markerEnd = markerStart;
        while (markerEnd < line.Length && line[markerEnd] == fenceCharacter)
        {
            markerEnd++;
        }

        fenceLength = markerEnd - markerStart;
        hasOnlyTrailingWhitespace = line[markerEnd..].All(char.IsWhiteSpace);
        return fenceLength >= 3;
    }

    private static bool IsIndentedCodeGap(string precedingLine, string followingLine) =>
        HasIndentedCodePrefix(precedingLine) && HasIndentedCodePrefix(followingLine);

    private static bool HasIndentedCodePrefix(string line) =>
        line.StartsWith('\t') || line.TakeWhile(character => character == ' ').Count() >= 4;

    private static bool TryRenderTaskListLine(string line, out string renderedLine)
    {
        renderedLine = line;
        var markerStart = 0;
        while (markerStart < line.Length && line[markerStart] == ' ')
        {
            markerStart++;
        }

        if (line.Length < markerStart + 6 ||
            (line[markerStart] != '-' && line[markerStart] != '*' && line[markerStart] != '+') ||
            line[markerStart + 1] != ' ' ||
            line[markerStart + 2] != '[' ||
            line[markerStart + 4] != ']' ||
            line[markerStart + 5] != ' ')
        {
            return false;
        }

        var check = line[markerStart + 3];
        if (check != ' ' && check != 'x' && check != 'X')
        {
            return false;
        }

        var checkboxGlyph = check == ' ' ? "☐" : "☑";
        renderedLine = string.Concat(line[..markerStart], line[markerStart], " ", checkboxGlyph, " ", line[(markerStart + 6)..]);
        return true;
    }

    internal static bool MayContainList(string markdown)
    {
        var lines = markdown.Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) ||
                trimmed.StartsWith("* ", StringComparison.Ordinal) ||
                trimmed.StartsWith("+ ", StringComparison.Ordinal))
            {
                return true;
            }

            var dotIndex = trimmed.IndexOf('.', StringComparison.Ordinal);
            if (dotIndex > 0 && dotIndex <= 3 && trimmed.Length > dotIndex + 1 && trimmed[dotIndex + 1] == ' ')
            {
                var allDigits = true;
                for (var i = 0; i < dotIndex; i++)
                {
                    if (!char.IsDigit(trimmed[i]))
                    {
                        allDigits = false;
                        break;
                    }
                }

                if (allDigits)
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static int GetPreservedBlankLineCount(string text)
    {
        var lines = NormalizeLineEndings(text).Split('\n');
        return lines.Length > 0 && lines.All(line => line == PreservedBlankLineMarkerText)
            ? lines.Length
            : 0;
    }
}
