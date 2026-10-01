using System.IO.Compression;
using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.Core;
using Xunit;

namespace SharpTurns.Tests;

public sealed class TurnExportFormatterTests
{
    private static readonly Conversation Conversation = new(7, 1, "Export test", null, null, DateTimeOffset.UnixEpoch);
    private static readonly Project Project = new(1, "Demo", "/work/demo");

    [Fact]
    public void AFullTurnExportsItsDialogueAndToolActivityInOrder()
    {
        var turn = Turn(1, string.Join('\n', Enumerable.Range(1, 12).Select(i => $"line {i}")));

        var markdown = TurnExportFormatter.FormatMarkdown(turn, Conversation, Project, new TurnExportOptions());

        Assert.True(Order(markdown, "## 🔹 Turn 1", "> **Turn ID:** 101", "> **Project:** Demo", "### User\n\nRun the tests",
            "> **Attachment 1:** `shot.png` · image/png · 3 B", "### Assistant\n\nRunning them.", "#### 🔧 Tool Call: `Bash`",
            "\"command\": \"dotnet test\"", "#### ✅ Tool Result", "line 10", "*[Tool result truncated",
            "### User (sent during the turn)\n\nAlso lint", "### ❓ Question", "### User (answer)\n\nYes", "### Assistant\n\nAll green."));
        Assert.DoesNotContain("line 11", markdown);
        Assert.Contains("line 12", TurnExportFormatter.FormatMarkdown(turn, Conversation, Project, new(FullToolResults: true)));
        var plain = TurnExportFormatter.FormatMarkdown(turn, Conversation, Project, new(IncludeToolDetails: false, IncludeMetadata: false));
        Assert.DoesNotContain("Tool Call", plain);
        Assert.DoesNotContain("Turn ID", plain);
        // MacDown needs a blank line before a list after a paragraph, but not between its items.
        Assert.Equal("Intro:\n\n- a\n- b\n\n1. c", TurnExportFormatter.FixMacDownListSpacing("Intro:\n- a\n- b\n\n1. c"));
    }

    [Fact]
    public void ACompressedTurnExportsItsUserInputsAndSummaryUnlessShownInFull()
    {
        var turn = Turn(1, "ok") with { Summary = "## Work Summary\n\n- Ran the tests.\n\n## Final Assistant Response — Verbatim\n\nAll green." };

        var markdown = TurnExportFormatter.FormatMarkdown(turn, Conversation, Project, new TurnExportOptions());
        var full = TurnExportFormatter.FormatMarkdown(turn, Conversation, Project, new(FullContent: true));

        Assert.True(Order(markdown, "Run the tests", "Also lint", "### User (answer)", "### Summary", "- Ran the tests."));
        Assert.DoesNotContain("Running them.", markdown);
        Assert.DoesNotContain("Tool Call", markdown);
        Assert.Contains("Running them.", full);
        Assert.Contains("Tool Call", full);
        Assert.DoesNotContain("### Summary", full);
    }

    [Fact]
    public void AConversationExportLeavesHiddenTurnsOutUnlessIncludedAndConvertsToTextAndDocx()
    {
        var turns = new[] { Turn(1, "ok"), Turn(2, "ok") with { IsHydrated = false } };
        var export = TurnExportDialogViewModel.ForConversation(turns, Conversation, Project);

        var shown = export.GenerateContent();
        export.IncludeHiddenTurns = true;
        var all = export.GenerateContent();
        export.IsText = true;
        var text = export.GenerateContent();

        Assert.True(Order(all, "# Export test", "> **Turns:** 2", "## 🔹 Turn 1", "## 🔹 Turn 2", "> **Hidden from Claude's context**"));
        Assert.True(Order(shown, "> **Turns:** 1", "> **Total Turns:** 2", "## 🔹 Turn 1"));
        Assert.DoesNotContain("Turn 2", shown);
        Assert.Equal("conversation_7_Export test.txt", export.DefaultFileName);
        Assert.StartsWith("Export test", text);
        Assert.DoesNotContain("**", text);
        Assert.DoesNotContain("```", text);

        var path = Path.Combine(Path.GetTempPath(), $"sharpturns-export-{Guid.NewGuid():N}.docx");
        try
        {
            TurnExportDocxWriter.Save("# Title <&>\n\n- item\n\n```\ncode\n```", path);
            using var archive = ZipFile.OpenRead(path);
            using var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open());
            var document = reader.ReadToEnd();
            Assert.Contains("<w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t xml:space=\"preserve\">Title &lt;&amp;&gt;", document);
            Assert.Contains("• item", document);
            Assert.Contains("<w:pStyle w:val=\"CodeBlock\"/></w:pPr><w:r><w:t xml:space=\"preserve\">code", document);
            Assert.NotNull(archive.GetEntry("word/styles.xml"));
        }
        finally { File.Delete(path); }
    }

    private static ConversationTurn Turn(int number, string toolResult) => new(100 + number, Conversation.Id, number, TurnStatus.Completed,
        null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(5),
        [
            new TurnPart(0, "user", TurnParts.Text, "Run the tests"),
            TurnParts.FromImage(1, new ImageAttachment("shot.png", "image/png", "AAAA")),
            new TurnPart(2, "assistant", TurnParts.Text, "Running them."),
            TurnParts.FromTool(3, new ToolCallRecord("t1", "Bash", "{\"command\":\"dotnet test\"}", toolResult, "completed", false)),
            new TurnPart(4, "user", TurnParts.Text, "Also lint"),
            TurnParts.FromQuestion(5, new QuestionRecord("Lint too?", "Yes")),
            new TurnPart(6, "assistant", TurnParts.Text, "All green."),
        ],
        new TurnUsage(1000, 800, 50, 1000));

    // Each marker appears, in order, with line endings normalized.
    private static bool Order(string text, params string[] markers)
    {
        text = text.ReplaceLineEndings("\n");
        var index = 0;
        foreach (var marker in markers)
        {
            index = text.IndexOf(marker, index, StringComparison.Ordinal);
            if (index < 0) return false;
            index += marker.Length;
        }
        return true;
    }
}
