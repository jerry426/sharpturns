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
    public void SharedPipelineKeepsPlainBraces(string text)
    {
        // A renderer's type initializer runs before it can build the shared pipeline.
        RuntimeHelpers.RunClassConstructor(typeof(MarkdownContentBlock).TypeHandle);

        var document = Markdig.Markdown.Parse(text, MarkdownUpdateProducer.DefaultPipeline);

        Assert.Equal(text, string.Concat(document.Descendants<LiteralInline>().Select(literal => literal.Content.ToString())));
    }
}
