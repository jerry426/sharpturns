using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace SharpTurns.App.Services;

/// <summary>
/// Writes export Markdown as a minimal Word document, as the Workbench does: headings, quotes, lists, and code blocks
/// become paragraph styles, and inline Markdown is stripped. Uses the Workbench's default page and font settings.
/// </summary>
public static partial class TurnExportDocxWriter
{
    // US Letter, portrait, 1" margins; Aptos 11 pt with 6 pt after paragraphs and 1.15 line spacing; Menlo 9 pt code.
    private const int PageWidthTwips = 12240;
    private const int PageHeightTwips = 15840;
    private const int MarginTwips = 1440;
    private const string NormalFontFamily = "Aptos";
    private const int NormalFontHalfPoints = 22;
    private const int ParagraphSpacingAfterTwips = 120;
    private const int LineSpacingTwips = 276;
    private const string CodeFontFamily = "Menlo";
    private const int CodeFontHalfPoints = 18;

    public static void Save(string markdown, string filePath)
    {
        if (File.Exists(filePath)) File.Delete(filePath);
        using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", ContentTypesXml);
        WriteEntry(archive, "_rels/.rels", RelationshipsXml);
        WriteEntry(archive, "word/_rels/document.xml.rels", DocumentRelationshipsXml);
        WriteEntry(archive, "word/styles.xml", StylesXml);
        WriteEntry(archive, "word/document.xml", CreateDocumentXml(markdown));
    }

    internal static string CreateDocumentXml(string markdown)
    {
        var body = new StringBuilder();
        var inCodeBlock = false;
        var codeBuffer = new StringBuilder();
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var previousNonBlankWasListItem = false;
        var previousLineWasBlank = false;

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
                AppendParagraph(body, StripInlineMarkdown(heading.Groups[2].Value), $"Heading{Math.Min(3, heading.Groups[1].Value.Length)}");
                previousNonBlankWasListItem = false;
            }
            else if (line.StartsWith('>'))
            {
                AppendParagraph(body, StripInlineMarkdown(line.TrimStart('>', ' ')), "Quote");
                previousNonBlankWasListItem = false;
            }
            else if (IsTurnSeparator(line))
            {
                AppendParagraph(body, new string('─', 70), null);
                previousNonBlankWasListItem = false;
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
        }

        if (inCodeBlock && codeBuffer.Length > 0) AppendParagraph(body, codeBuffer.ToString().TrimEnd(), "CodeBlock");

        return $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
{{body}}    <w:sectPr><w:pgSz w:w="{{PageWidthTwips}}" w:h="{{PageHeightTwips}}"/><w:pgMar w:top="{{MarginTwips}}" w:right="{{MarginTwips}}" w:bottom="{{MarginTwips}}" w:left="{{MarginTwips}}"/></w:sectPr>
  </w:body>
</w:document>
""";
    }

    private static void AppendParagraph(StringBuilder builder, string text, string? style)
    {
        builder.Append("    <w:p>");
        if (style is not null)
        {
            builder.Append("<w:pPr><w:pStyle w:val=\"").Append(style).Append("\"/>");
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

    private static readonly string StylesXml = $$"""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/><w:pPr><w:spacing w:after="{{ParagraphSpacingAfterTwips}}" w:line="{{LineSpacingTwips}}" w:lineRule="auto"/></w:pPr><w:rPr><w:rFonts w:ascii="{{NormalFontFamily}}" w:hAnsi="{{NormalFontFamily}}"/><w:sz w:val="{{NormalFontHalfPoints}}"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="240" w:after="120"/></w:pPr><w:rPr><w:b/><w:sz w:val="32"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="Heading2"><w:name w:val="heading 2"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="200" w:after="100"/></w:pPr><w:rPr><w:b/><w:sz w:val="28"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="Heading3"><w:name w:val="heading 3"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="160" w:after="80"/></w:pPr><w:rPr><w:b/><w:sz w:val="24"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="CodeBlock"><w:name w:val="Code Block"/><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="80" w:after="80"/><w:shd w:val="clear" w:color="auto" w:fill="F3F4F6"/></w:pPr><w:rPr><w:rFonts w:ascii="{{CodeFontFamily}}" w:hAnsi="{{CodeFontFamily}}"/><w:sz w:val="{{CodeFontHalfPoints}}"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="Quote"><w:name w:val="Quote"/><w:basedOn w:val="Normal"/><w:pPr><w:ind w:left="360"/><w:spacing w:before="80" w:after="80"/></w:pPr><w:rPr><w:i/><w:color w:val="666666"/></w:rPr></w:style>
  <w:style w:type="paragraph" w:styleId="ListParagraph"><w:name w:val="List Paragraph"/><w:basedOn w:val="Normal"/><w:pPr><w:ind w:left="360"/></w:pPr></w:style>
</w:styles>
""";

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"\*\*([^*]+)\*\*|\*([^*]+)\*")]
    private static partial Regex BoldItalicRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^(\s*)([-*+] |\d+\.\s+)(.+)$")]
    private static partial Regex ListItemRegex();
}
