using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace SharpTurns.App.Services;

/// <summary>
/// Writes export Markdown as a minimal Word document: headings, quotes, lists, and code blocks
/// become paragraph styles, and inline Markdown is stripped. Page layout and fonts come from the DOCX export defaults.
/// </summary>
public static partial class TurnExportDocxWriter
{
    public static void Save(string markdown, string filePath, DocxExportSettings settings)
    {
        settings = settings.Normalize();
        if (File.Exists(filePath)) File.Delete(filePath);
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", ContentTypesXml);
        WriteEntry(archive, "_rels/.rels", RelationshipsXml);
        WriteEntry(archive, "word/_rels/document.xml.rels", DocumentRelationshipsXml);
        WriteEntry(archive, "word/styles.xml", CreateStylesXml(settings));
        WriteEntry(archive, "word/document.xml", CreateDocumentXml(markdown, settings));
    }

    internal static string CreateDocumentXml(string markdown, DocxExportSettings settings)
    {
        var body = new StringBuilder();
        var inCodeBlock = false;
        var codeBuffer = new StringBuilder();
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var previousNonBlankWasListItem = false;
        var previousLineWasBlank = false;
        var hasBodyContent = false;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex].TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    AppendParagraph(body, codeBuffer.ToString().TrimEnd(), "CodeBlock");
                    codeBuffer.Clear();
                    previousNonBlankWasListItem = false;
                    hasBodyContent = true;
                }
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (inCodeBlock)
            {
                codeBuffer.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                // A blank line inside a list doesn't open a gap between its items.
                if (!(IsNextNonBlankLineListItem(lines, lineIndex + 1) && (previousNonBlankWasListItem || previousLineWasBlank)))
                    AppendParagraph(body, string.Empty, null);
                previousLineWasBlank = true;
                continue;
            }
            previousLineWasBlank = false;

            if (HeadingRegex().Match(line) is { Success: true } heading)
            {
                var level = Math.Min(3, heading.Groups[1].Value.Length);
                var headingText = StripInlineMarkdown(heading.Groups[2].Value);
                if (!settings.IncludeEmojiHeadings) headingText = EmojiRegex().Replace(headingText, string.Empty).Trim();
                // Each turn's export starts with a "## 🔹 Turn N" heading.
                var pageBreakBefore = settings.StartEachTurnOnNewPage && hasBodyContent && level == 2
                    && headingText.Contains("Turn", StringComparison.OrdinalIgnoreCase);
                AppendParagraph(body, headingText, $"Heading{level}", pageBreakBefore);
                previousNonBlankWasListItem = false;
            }
            else if (line.StartsWith('>'))
            {
                AppendParagraph(body, StripInlineMarkdown(line.TrimStart('>', ' ')), "Quote");
                previousNonBlankWasListItem = false;
            }
            else if (IsTurnSeparator(line))
            {
                if (settings.IncludeTurnSeparators) AppendParagraph(body, new string('─', 70), null);
                previousNonBlankWasListItem = false;
                continue;
            }
            else if (ListItemRegex().Match(line) is { Success: true } listItem)
            {
                AppendParagraph(body, "• " + StripInlineMarkdown(listItem.Groups[3].Value), "ListParagraph");
                previousNonBlankWasListItem = true;
            }
            else
            {
                AppendParagraph(body, StripInlineMarkdown(line), null);
                previousNonBlankWasListItem = false;
            }
            hasBodyContent = true;
        }

        if (inCodeBlock && codeBuffer.Length > 0) AppendParagraph(body, codeBuffer.ToString().TrimEnd(), "CodeBlock");

        var orientation = settings.Orientation == "landscape" ? " w:orient=\"landscape\"" : string.Empty;
        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
{{body}}    <w:sectPr><w:pgSz w:w="{{settings.PageWidthTwips}}" w:h="{{settings.PageHeightTwips}}"{{orientation}}/><w:pgMar w:top="{{settings.MarginTopTwips}}" w:right="{{settings.MarginRightTwips}}" w:bottom="{{settings.MarginBottomTwips}}" w:left="{{settings.MarginLeftTwips}}"/></w:sectPr>
  </w:body>
</w:document>
""";
    }

    private static void AppendParagraph(StringBuilder builder, string text, string? style, bool pageBreakBefore = false)
    {
        builder.Append("    <w:p>");
        if (style is not null || pageBreakBefore)
        {
            builder.Append("<w:pPr>");
            if (pageBreakBefore) builder.Append("<w:pageBreakBefore/>");
            if (style is not null) builder.Append("<w:pStyle w:val=\"").Append(style).Append("\"/>");
            if (style == "ListParagraph") builder.Append("<w:ind w:left=\"360\"/>");
            builder.Append("</w:pPr>");
        }

        if (text.Length > 0)
        {
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            builder.Append("<w:r><w:t xml:space=\"preserve\">").Append(XmlEscape(lines[0])).Append("</w:t></w:r>");
            foreach (var line in lines.Skip(1))
                builder.Append("<w:r><w:br/><w:t xml:space=\"preserve\">").Append(XmlEscape(line)).Append("</w:t></w:r>");
        }

        builder.AppendLine("</w:p>");
    }

    private static string StripInlineMarkdown(string text)
    {
        var stripped = BoldItalicRegex().Replace(text, "$1$2");
        stripped = InlineCodeRegex().Replace(stripped, "$1");
        return LinkRegex().Replace(stripped, "$1");
    }

    private static bool IsTurnSeparator(string line)
    {
        var trimmed = line.Trim();
        return trimmed == "---" || (trimmed.Length > 0 && trimmed.All(ch => ch is '■' or '▪'));
    }

    private static bool IsNextNonBlankLineListItem(string[] lines, int startIndex)
    {
        for (var index = startIndex; index < lines.Length; index++)
        {
            if (!string.IsNullOrWhiteSpace(lines[index])) return ListItemRegex().IsMatch(lines[index].TrimEnd());
        }
        return false;
    }

    private static string XmlEscape(string value)
    {
        using var writer = new StringWriter();
        using (var xmlWriter = XmlWriter.Create(writer, new XmlWriterSettings { ConformanceLevel = ConformanceLevel.Fragment }))
            xmlWriter.WriteString(value);
        return writer.ToString();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        using var stream = archive.CreateEntry(path, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private const string ContentTypesXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
</Types>
""";

    private const string RelationshipsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>
""";

    private const string DocumentRelationshipsXml = """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>
""";

    private static string CreateStylesXml(DocxExportSettings settings)
    {
        var (heading1, heading2, heading3) = settings.HeadingHalfPoints;
        var codeShading = settings.CodeBlockShading ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F3F4F6\"/>" : string.Empty;
        var normalFont = XmlEscape(settings.NormalFontFamily);
        var codeFont = XmlEscape(settings.CodeFontFamily);
        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:pPr><w:spacing w:after="{{settings.ParagraphSpacingAfterTwips}}" w:line="{{settings.LineSpacingTwips}}" w:lineRule="auto"/></w:pPr><w:rPr><w:rFonts w:ascii="{{normalFont}}" w:hAnsi="{{normalFont}}"/><w:sz w:val="{{settings.NormalFontHalfPoints}}"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="240" w:after="120"/></w:pPr><w:rPr><w:b/><w:sz w:val="{{heading1}}"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="200" w:after="100"/></w:pPr><w:rPr><w:b/><w:sz w:val="{{heading2}}"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="160" w:after="80"/></w:pPr><w:rPr><w:b/><w:sz w:val="{{heading3}}"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="CodeBlock"><w:name w:val="Code Block"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="80" w:after="80"/>{{codeShading}}</w:pPr><w:rPr><w:rFonts w:ascii="{{codeFont}}" w:hAnsi="{{codeFont}}"/><w:sz w:val="{{settings.CodeFontHalfPoints}}"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="Quote"><w:name w:val="Quote"/><w:basedOn w:val="Normal"/><w:pPr><w:ind w:left="360"/><w:spacing w:before="80" w:after="80"/></w:pPr><w:rPr><w:i/><w:color w:val="666666"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/><w:pPr><w:ind w:left="360"/></w:pPr></w:style>
</w:styles>
""";
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"[☀-➿]|[\uD83C-\uDBFF][\uDC00-\uDFFF]")]
    private static partial Regex EmojiRegex();

    [GeneratedRegex(@"\*\*([^*]+)\*\*|\*([^*]+)\*")]
    private static partial Regex BoldItalicRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^(\s*)([-*+] |\d+\.\s+)(.+)$")]
    private static partial Regex ListItemRegex();
}
