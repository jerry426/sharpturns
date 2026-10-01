using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;
using Markdig;

namespace SharpTurns.Markdown.Rendering;

internal sealed record RenderedMarkdownClipboardPayload(
    string Markdown,
    string Html);

internal static class RenderedMarkdownClipboard
{
    private static readonly Regex HtmlImageRegex = new(
        "<img\\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlLinkAttributeRegex = new(
        "\\s+href=\"(?<url>[^\"]*)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly MarkdownPipeline HtmlPipeline = CreateHtmlPipeline();

    private static MarkdownPipeline CreateHtmlPipeline()
    {
        var builder = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .DisableHtml();
        MarkdownContentBlock.RemoveGenericAttributes(builder);
        return builder.Build();
    }

    public static RenderedMarkdownClipboardPayload? Create(
        string sourceMarkdown,
        string plainText,
        IReadOnlyList<MarkdownTextBlock> blocks)
    {
        var selectedBlocks = blocks.Where(HasSelection).ToArray();
        if (selectedBlocks.Length == 0 || string.IsNullOrEmpty(plainText))
        {
            return null;
        }

        var selectableBlocks = blocks.Where(block => block.EscapedTextLength > 0).ToArray();
        var wholeDocumentSelected = !string.IsNullOrEmpty(sourceMarkdown)
            && selectableBlocks.Length > 0
            && selectableBlocks.All(IsFullySelected);

        var markdown = wholeDocumentSelected
            ? sourceMarkdown
            : SerializePartialSelection(selectedBlocks);
        if (string.IsNullOrWhiteSpace(markdown))
        {
            markdown = plainText;
        }

        var body = SanitizeGeneratedHtml(Markdig.Markdown.ToHtml(markdown, HtmlPipeline));
        var html = "<html><head><meta charset=\"utf-8\"></head><body>" + body + "</body></html>";
        return new RenderedMarkdownClipboardPayload(markdown, html);
    }

    public static DataTransfer CreateDataTransfer(RenderedMarkdownClipboardPayload payload)
    {
        var item = new DataTransferItem();
        // Plain-text destinations do not understand the Markdown-specific
        // platform format and may otherwise derive lossy text from HTML.
        // Publish the same raw Markdown as the universal text fallback.
        item.SetText(payload.Markdown);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsIOS())
        {
            item.Set(DataFormat.CreateStringPlatformFormat("public.html"), payload.Html);
            item.Set(DataFormat.CreateStringPlatformFormat("net.daringfireball.markdown"), payload.Markdown);
        }
        else if (OperatingSystem.IsWindows())
        {
            item.Set(
                DataFormat.CreateBytesPlatformFormat("HTML Format"),
                CreateWindowsHtmlClipboardBytes(payload.Html));
            item.Set(DataFormat.CreateStringPlatformFormat("text/markdown"), payload.Markdown);
        }
        else
        {
            item.Set(DataFormat.CreateStringPlatformFormat("text/html"), payload.Html);
            item.Set(DataFormat.CreateStringPlatformFormat("text/markdown"), payload.Markdown);
        }

        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }

    internal static byte[] CreateWindowsHtmlClipboardBytes(string html)
    {
        const string startMarker = "<!--StartFragment-->";
        const string endMarker = "<!--EndFragment-->";
        const string headerTemplate =
            "Version:1.0\r\n" +
            "StartHTML:{0:D10}\r\n" +
            "EndHTML:{1:D10}\r\n" +
            "StartFragment:{2:D10}\r\n" +
            "EndFragment:{3:D10}\r\n";

        var fragmentStartIndex = html.IndexOf("<body>", StringComparison.OrdinalIgnoreCase);
        var fragmentEndIndex = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        var beforeFragment = fragmentStartIndex >= 0 ? fragmentStartIndex + "<body>".Length : 0;
        var afterFragment = fragmentEndIndex >= beforeFragment ? fragmentEndIndex : html.Length;
        var markedHtml = html.Insert(afterFragment, endMarker).Insert(beforeFragment, startMarker);

        var placeholderHeader = string.Format(headerTemplate, 0, 0, 0, 0);
        var startHtml = Encoding.UTF8.GetByteCount(placeholderHeader);
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(markedHtml[..(beforeFragment + startMarker.Length)]);
        var endFragmentMarkerIndex = markedHtml.IndexOf(endMarker, StringComparison.Ordinal);
        var endFragment = startHtml + Encoding.UTF8.GetByteCount(markedHtml[..endFragmentMarkerIndex]);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(markedHtml);
        var header = string.Format(headerTemplate, startHtml, endHtml, startFragment, endFragment);
        var encoded = Encoding.UTF8.GetBytes(header + markedHtml);
        return [.. encoded, 0];
    }

    private static string SerializePartialSelection(IReadOnlyList<MarkdownTextBlock> blocks)
    {
        var output = new StringBuilder();
        MarkdownBlockKind? previousKind = null;

        foreach (var block in blocks)
        {
            var markdown = SerializeBlock(block, out var kind);
            if (string.IsNullOrEmpty(markdown))
            {
                continue;
            }

            if (output.Length > 0)
            {
                output.Append(previousKind == MarkdownBlockKind.ListItem && kind == MarkdownBlockKind.ListItem
                    ? Environment.NewLine
                    : Environment.NewLine + Environment.NewLine);
            }

            output.Append(markdown);
            previousKind = kind;
        }

        return output.ToString();
    }

    private static string SerializeBlock(MarkdownTextBlock block, out MarkdownBlockKind kind)
    {
        var start = Math.Min(block.SelectionStart, block.SelectionEnd);
        var end = Math.Max(block.SelectionStart, block.SelectionEnd);
        var isCodeBlock = HasAncestor<LiveMarkdown.Avalonia.CodeBlock>(block);
        if (isCodeBlock)
        {
            kind = MarkdownBlockKind.Code;
            return SerializeCodeSelection(block);
        }

        var serializer = new InlineSelectionSerializer(start, end, InlineStyle.From(block), literalText: false);
        var content = block.Inlines is { Count: > 0 } inlines
            ? serializer.Serialize(inlines)
            : serializer.SerializeText(block.Text ?? string.Empty);

        if (TryGetHeadingLevel(block, out var headingLevel))
        {
            kind = MarkdownBlockKind.Block;
            return new string('#', headingLevel) + " " + content;
        }

        if (TryGetListPrefix(block, out var listPrefix))
        {
            kind = MarkdownBlockKind.ListItem;
            content = RestoreTaskListMarker(content);
            var continuationIndent = new string(' ', listPrefix.Length);
            content = string.Join(
                Environment.NewLine,
                content.Split(Environment.NewLine).Select((line, index) => index == 0 ? line : continuationIndent + line));
            return listPrefix + content;
        }

        if (HasAncestorClass(block, "QuoteBlock"))
        {
            kind = MarkdownBlockKind.Block;
            return string.Join(
                Environment.NewLine,
                content.Split(Environment.NewLine).Select(line => "> " + line));
        }

        kind = MarkdownBlockKind.Block;
        return EscapeBlockStarts(content);
    }

    private static string CreateFencedCode(string content)
    {
        var fenceLength = Math.Max(3, LongestRun(content, '`') + 1);
        var fence = new string('`', fenceLength);
        return fence + Environment.NewLine + content + Environment.NewLine + fence;
    }

    internal static string SerializeCodeSelection(MarkdownTextBlock block)
    {
        var start = Math.Min(block.SelectionStart, block.SelectionEnd);
        var end = Math.Max(block.SelectionStart, block.SelectionEnd);
        var serializer = new InlineSelectionSerializer(start, end, InlineStyle.From(block), literalText: true);
        var content = block.Inlines is { Count: > 0 } inlines
            ? serializer.Serialize(inlines)
            : serializer.SerializeText(block.Text ?? string.Empty);
        return CreateFencedCode(content);
    }

    private static int LongestRun(string text, char character)
    {
        var longest = 0;
        var current = 0;
        foreach (var value in text)
        {
            current = value == character ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static bool HasSelection(MarkdownTextBlock block)
        => block.SelectionStart != block.SelectionEnd;

    private static bool IsFullySelected(MarkdownTextBlock block)
    {
        var start = Math.Min(block.SelectionStart, block.SelectionEnd);
        var end = Math.Max(block.SelectionStart, block.SelectionEnd);
        return start == 0 && end >= block.EscapedTextLength;
    }

    private static bool HasAncestor<T>(Control control) where T : Visual
        => control.GetVisualAncestors().OfType<T>().Any();

    private static bool HasAncestorClass(Control control, string className)
        => control.GetVisualAncestors().OfType<Control>().Any(ancestor => ancestor.Classes.Contains(className));

    private static bool TryGetHeadingLevel(Control control, out int level)
    {
        foreach (var ancestor in control.GetVisualAncestors().OfType<Control>())
        {
            for (var candidate = 1; candidate <= 6; candidate++)
            {
                if (ancestor.Classes.Contains($"Heading{candidate}Block"))
                {
                    level = candidate;
                    return true;
                }
            }
        }

        level = 0;
        return false;
    }

    private static bool TryGetListPrefix(Control control, out string prefix)
    {
        var listGrid = control.GetVisualAncestors().OfType<MarkdownListGrid>().FirstOrDefault();
        if (listGrid is null)
        {
            prefix = string.Empty;
            return false;
        }

        Visual itemRoot = control;
        while (itemRoot.GetVisualParent() is { } parent && parent != listGrid)
        {
            itemRoot = parent;
        }

        var row = itemRoot is Control itemControl ? Grid.GetRow(itemControl) : -1;
        var marker = listGrid.Children
            .OfType<TextBlock>()
            .FirstOrDefault(candidate => Grid.GetColumn(candidate) == 0 && Grid.GetRow(candidate) == row);
        var markerText = marker?.Classes.Contains("ListBlockNumber") == true
            ? marker.Text ?? "1."
            : "-";
        var nesting = Math.Max(0, control.GetVisualAncestors().OfType<MarkdownListGrid>().Count() - 1);
        prefix = new string(' ', nesting * 4) + markerText + " ";
        return true;
    }

    private static string RestoreTaskListMarker(string content)
    {
        if (content.StartsWith("☑ ", StringComparison.Ordinal))
        {
            return "[x] " + content[2..];
        }

        if (content.StartsWith("☐ ", StringComparison.Ordinal))
        {
            return "[ ] " + content[2..];
        }

        return content;
    }

    private static string EscapeBlockStarts(string content)
    {
        return string.Join(Environment.NewLine, content.Split(Environment.NewLine).Select(EscapeLineStart));

        static string EscapeLineStart(string line)
        {
            var contentStart = 0;
            while (contentStart < line.Length && contentStart < 3 && line[contentStart] == ' ')
            {
                contentStart++;
            }

            if (contentStart >= line.Length)
            {
                return line;
            }

            var remainder = line[contentStart..];
            var markerLength = remainder[0] switch
            {
                '#' or '>' when remainder.Length == 1 || char.IsWhiteSpace(remainder[1]) => 1,
                '-' or '+' when remainder.Length > 1 && char.IsWhiteSpace(remainder[1]) => 1,
                _ => OrderedListMarkerLength(remainder),
            };

            return markerLength > 0 ? line.Insert(contentStart, "\\") : line;
        }

        static int OrderedListMarkerLength(string text)
        {
            var index = 0;
            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }

            return index > 0
                && index + 1 < text.Length
                && text[index] is '.' or ')'
                && char.IsWhiteSpace(text[index + 1])
                    ? index + 1
                    : 0;
        }
    }

    private static string SanitizeGeneratedHtml(string html)
    {
        html = HtmlImageRegex.Replace(html, string.Empty);
        return HtmlLinkAttributeRegex.Replace(html, match =>
        {
            var decoded = WebUtility.HtmlDecode(match.Groups["url"].Value);
            return Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https" or "mailto"
                    ? match.Value
                    : string.Empty;
        });
    }

    private static string EscapeMarkdown(string text)
    {
        var output = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '>')
            {
                output.Append('\\');
            }

            output.Append(character);
        }

        return output.ToString();
    }

    private enum MarkdownBlockKind
    {
        Block,
        ListItem,
        Code,
    }

    private readonly record struct InlineStyle(
        FontWeight FontWeight,
        FontStyle FontStyle,
        bool Strikethrough,
        FontFamily? FontFamily)
    {
        public static InlineStyle From(MarkdownTextBlock block) => new(
            block.FontWeight,
            block.FontStyle,
            HasStrikethrough(block.TextDecorations),
            block.FontFamily);

        public static InlineStyle From(Inline element) => new(
            element.FontWeight,
            element.FontStyle,
            HasStrikethrough(element.TextDecorations),
            element.FontFamily);
    }

    private sealed class InlineSelectionSerializer(
        int selectionStart,
        int selectionEnd,
        InlineStyle rootStyle,
        bool literalText)
    {
        private int _offset;

        public string Serialize(InlineCollection inlines)
            => Serialize(inlines, rootStyle);

        public string SerializeText(string text)
        {
            var selected = TakeText(text);
            return literalText ? selected : EscapeMarkdown(selected);
        }

        public string TakeText(string text)
        {
            var runStart = _offset;
            var runEnd = runStart + text.Length;
            _offset = runEnd;
            if (runEnd <= selectionStart || runStart >= selectionEnd)
            {
                return string.Empty;
            }

            var start = Math.Max(selectionStart - runStart, 0);
            var end = Math.Min(selectionEnd - runStart, text.Length);
            return text[start..end];
        }

        private string Serialize(InlineCollection inlines, InlineStyle inheritedStyle)
        {
            var output = new StringBuilder();
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case Run run:
                        output.Append(SerializeRun(run, inheritedStyle));
                        break;
                    case LineBreak:
                        output.Append(TakeText(Environment.NewLine));
                        break;
                    case Span span:
                        output.Append(SerializeSpan(span, inheritedStyle));
                        break;
                    case InlineUIContainer container:
                        output.Append(SerializeContainer(container));
                        break;
                }
            }

            return output.ToString();
        }

        private string SerializeRun(Run run, InlineStyle inheritedStyle)
        {
            var selected = TakeText(run.Text ?? string.Empty);
            if (string.IsNullOrEmpty(selected))
            {
                return string.Empty;
            }

            if (literalText)
            {
                return selected;
            }

            var escaped = EscapeMarkdown(selected);
            var runStyle = InlineStyle.From(run);
            if (IsInlineCode(runStyle, inheritedStyle))
            {
                return WrapInlineCode(selected);
            }

            return ApplyStyle(escaped, runStyle, inheritedStyle);
        }

        private string SerializeSpan(Span span, InlineStyle inheritedStyle)
        {
            var spanStyle = InlineStyle.From(span);
            var selected = Serialize(span.Inlines, spanStyle);
            if (string.IsNullOrEmpty(selected))
            {
                return string.Empty;
            }

            if (literalText)
            {
                return selected;
            }

            var styled = ApplyStyle(selected, spanStyle, inheritedStyle);
            if (span is Link { HRef: { } href })
            {
                styled = $"[{styled}]({EscapeLinkTarget(href.ToString())})";
            }

            return styled;
        }

        private string SerializeContainer(InlineUIContainer container)
        {
            var selected = TakeText("\uFFFC");
            if (string.IsNullOrEmpty(selected))
            {
                return string.Empty;
            }

            var text = FindLogicalText(container.Child);
            if (literalText)
            {
                return text;
            }

            return container.Classes.Contains("Code") ? WrapInlineCode(text) : EscapeMarkdown(text);
        }

        private static string ApplyStyle(string text, InlineStyle style, InlineStyle inherited)
        {
            if (style.Strikethrough && !inherited.Strikethrough)
            {
                text = "~~" + text + "~~";
            }

            if (style.FontStyle == FontStyle.Italic && inherited.FontStyle != FontStyle.Italic)
            {
                text = "*" + text + "*";
            }

            if (style.FontWeight == FontWeight.Bold && inherited.FontWeight != FontWeight.Bold)
            {
                text = "**" + text + "**";
            }

            return text;
        }

        private static bool IsInlineCode(InlineStyle style, InlineStyle inherited)
        {
            var family = style.FontFamily?.ToString() ?? string.Empty;
            return style.FontFamily != inherited.FontFamily
                && (family.Contains("Menlo", StringComparison.OrdinalIgnoreCase)
                    || family.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                    || family.Contains("Courier New", StringComparison.OrdinalIgnoreCase)
                    || family.Contains("DejaVu Sans Mono", StringComparison.OrdinalIgnoreCase));
        }

        private static string WrapInlineCode(string text)
        {
            var delimiter = new string('`', Math.Max(1, LongestRun(text, '`') + 1));
            var needsPadding = text.StartsWith('`')
                || text.EndsWith('`')
                || text.StartsWith(' ')
                || text.EndsWith(' ');
            var padding = needsPadding ? " " : string.Empty;
            return delimiter + padding + text + padding + delimiter;
        }

        private static string EscapeLinkTarget(string href)
            => href.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace(")", "\\)", StringComparison.Ordinal)
                .Replace(" ", "%20", StringComparison.Ordinal);

        private static string FindLogicalText(Control? control)
        {
            if (control is null)
            {
                return string.Empty;
            }

            if (control is MarkdownTextBlock markdownTextBlock)
            {
                return markdownTextBlock.ActualText;
            }

            if (control is TextBlock textBlock)
            {
                return textBlock.Text ?? string.Empty;
            }

            return string.Concat(control.GetVisualDescendants().OfType<TextBlock>().Select(child => child.Text));
        }
    }

    private static bool HasStrikethrough(TextDecorationCollection? decorations)
        => decorations?.Any(decoration => decoration.Location == TextDecorationLocation.Strikethrough) == true;
}
