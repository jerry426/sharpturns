using System.Runtime.CompilerServices;
using LiveMarkdown.Avalonia;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SharpTurns.Markdown.Rendering;
using Xunit;

namespace SharpTurns.Tests;

public sealed class MarkdownRenderingTests
{
    [Theory]
    [InlineData("containing {}")]
    [InlineData("use {name} here")]
    [InlineData("a {.cls} b")]
    [InlineData("List<T> and <summary> tag")]
    [InlineData("math $x$ and $a and b$ x")]
    [InlineData("2^10 and x ~sub~ y ++ins++ ==mark==")]
    [InlineData("say \"\"quoted\"\" here")]
    public void SharedPipelineKeepsPlainText(string text)
    {
        // Touching the renderer type runs the rendering assembly's module
        // initializer, as the app does before anything builds the shared pipeline.
        RuntimeHelpers.RunClassConstructor(typeof(MarkdownContentBlock).TypeHandle);

        var document = Markdig.Markdown.Parse(text, MarkdownUpdateProducer.DefaultPipeline);

        Assert.Equal(text, string.Concat(document.Descendants<LiteralInline>().Select(literal => literal.Content.ToString())));
    }

    [Fact]
    public void SharedPipelineKeepsStrikethrough()
    {
        RuntimeHelpers.RunClassConstructor(typeof(MarkdownContentBlock).TypeHandle);

        var document = Markdig.Markdown.Parse("a ~~gone~~ b", MarkdownUpdateProducer.DefaultPipeline);

        var emphasis = Assert.Single(document.Descendants<EmphasisInline>());
        Assert.Equal('~', emphasis.DelimiterChar);
        Assert.Equal(2, emphasis.DelimiterCount);
    }

    [Fact]
    public void SharedPipelineKeepsRomanListMarkersAsText()
    {
        RuntimeHelpers.RunClassConstructor(typeof(MarkdownContentBlock).TypeHandle);

        var document = Markdig.Markdown.Parse("I. first", MarkdownUpdateProducer.DefaultPipeline);

        Assert.Empty(document.Descendants<ListBlock>());
    }

    [Fact]
    public void PreprocessorRendersTaskListMarkersAsGlyphs()
    {
        var rendered = MarkdownSourcePreprocessor.PrepareForRendering("- [ ] todo\n  * [X] done\n- [y] other");

        Assert.Equal("- ☐ todo\n  * ☑ done\n- [y] other", rendered);
    }

    [Fact]
    public void PreprocessorEncodesSurplusBlankLinesAsMarkerParagraph()
    {
        const string marker = "&#8203;&#8288;&#8203;";

        var rendered = MarkdownSourcePreprocessor.PrepareForRendering("a\r\n\r\n\r\n\r\nb");

        Assert.Equal($"a\n\n{marker}  \n{marker}\n\nb", rendered);
    }

    [Theory]
    [InlineData("a\n\nb")]
    [InlineData("\n\n\na")]
    [InlineData("a\n\n\n")]
    [InlineData("```\na\n\n\nb\n```")]
    [InlineData("    a\n\n\n    b")]
    public void PreprocessorLeavesOtherBlankLinesUnchanged(string markdown)
    {
        Assert.Equal(markdown, MarkdownSourcePreprocessor.PrepareForRendering(markdown));
    }

    [Theory]
    [InlineData("​⁠​", 1)]
    [InlineData("​⁠​\r\n​⁠​", 2)]
    [InlineData("​⁠​\ntext", 0)]
    [InlineData("text", 0)]
    public void PreprocessorCountsRenderedBlankLineMarkers(string text, int expected)
    {
        Assert.Equal(expected, MarkdownSourcePreprocessor.GetPreservedBlankLineCount(text));
    }
}
