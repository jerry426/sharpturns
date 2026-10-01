using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.ClaudeCli;
using SharpTurns.Core;
using Xunit;

namespace SharpTurns.Tests;

public sealed class TurnViewModelTests
{
    [Theory]
    [InlineData("Plain text so far", 0)]
    [InlineData("Intro\n```cs\nvar x = 1;\n", 0)]
    [InlineData("Intro\n```cs\nvar x = 1;\n```", 0)] // The closing line may still grow.
    [InlineData("Intro\n```cs\nvar x = 1;\n```\nAfter", 27)]
    [InlineData("````\n```\nnested\n````\n", 21)]
    [InlineData("~~~\ncode\n~~~\nmore\n```\nopen", 0)]
    public void OnlyClosedCodeFencesAreStable(string content, int expected) =>
        Assert.Equal(expected, TurnViewModel.StableMarkdownLength(content));

    [Fact]
    public void StreamingTextRendersCompletedCodeBlocksAndEndsAtTheNextCard()
    {
        var turn = new TurnViewModel("prompt", []);
        turn.AppendText("Intro\n```\ncode\n");
        turn.AppendText("```\nRest");
        turn.AppendText(" of it");
        turn.UpdateTool(new("toolu_1", "Bash", """{"command":"ls"}""", null, "Running", false));
        turn.UpdateTool(new("toolu_1", "Bash", """{"command":"ls"}""", "a.txt", "Completed", false));
        turn.AppendText("Done.");

        Assert.Equal(4, turn.Items.Count);
        var rendered = Assert.IsType<TextItemViewModel>(turn.Items[0]);
        Assert.Equal(("Intro\n```\ncode\n```", false), (rendered.Text, rendered.IsStreaming));
        Assert.Equal(("Rest of it", false), (((TextItemViewModel)turn.Items[1]).Text, ((TextItemViewModel)turn.Items[1]).IsStreaming));
        Assert.Equal("Completed", Assert.IsType<ToolItemViewModel>(turn.Items[2]).Tool.Status);
        Assert.True(((TextItemViewModel)turn.Items[3]).IsStreaming);

        turn.Finish(new ConversationTurn(1, 1, 1, TurnStatus.Stopped, null, DateTimeOffset.UnixEpoch, null, [],
            new TurnUsage(12_345, 9_000, 678, 45_600)));

        Assert.False(((TextItemViewModel)turn.Items[3]).IsStreaming);
        Assert.Equal("Stopped.", turn.Outcome);
        Assert.Equal("12.3k input tokens (73% cached) · 678 output · context 45.6k", turn.UsageText);
    }

    [Fact]
    public void SavedTurnsRebuildTheirItemsInOrder()
    {
        var image = new ImageAttachment("shot.png", "image/png", "iVBORw0KGgo=");
        var turn = new TurnViewModel(new ConversationTurn(1, 1, 1, TurnStatus.Completed, null, DateTimeOffset.UnixEpoch, null,
        [
            new(1, "user", TurnParts.Text, "prompt"),
            TurnParts.FromImage(2, image),
            new(3, "assistant", TurnParts.Thinking, "hidden"),
            new(4, "assistant", TurnParts.Text, "First."),
            TurnParts.FromTool(5, new("toolu_1", "Read", """{"file_path":"/a/b.txt"}""", "text", "Completed", false)),
            TurnParts.FromQuestion(6, new("Which?", null)),
            new(7, "user", TurnParts.Text, "also this"),
            new(8, "assistant", TurnParts.Text, "Second."),
        ]));

        Assert.Equal("prompt", turn.UserText);
        Assert.Equal([image], turn.Images.Select(i => i.Attachment));
        Assert.Equal([typeof(TextItemViewModel), typeof(ToolItemViewModel), typeof(QuestionItemViewModel),
            typeof(UserMessageItemViewModel), typeof(TextItemViewModel)], turn.Items.Select(i => i.GetType()));
        Assert.Equal("/a/b.txt", ((ToolItemViewModel)turn.Items[1]).Summary);
        Assert.Equal("No answer; the question was declined.", ((QuestionItemViewModel)turn.Items[2]).AnswerText);
        Assert.False(turn.IsRunning);
        Assert.Null(turn.UsageText);
    }

    [Fact]
    public void ToolInputShowsOneFieldPerLineWithMultilineValuesIntact()
    {
        var tool = new ToolItemViewModel(new("toolu_1", "Edit",
            """{"file_path":"a.cs","old_string":"x\ny","replace_all":false}""", null, "Waiting for your approval", false));

        Assert.Equal("a.cs", tool.Summary);
        Assert.Equal("file_path: a.cs\nold_string:\nx\ny\nreplace_all: false", tool.InputText);
        Assert.Equal((true, false, false), (tool.IsWaiting, tool.IsRunning, tool.IsSucceeded));
    }

    [Fact]
    public void QuestionDialogAnswersWithOptionsOrCustomText()
    {
        var single = new QuestionDialogViewModel(new("Which?", null,
            [new("Blue", null, null), new("Red", null, null)], MultiSelect: false), "Project", "Conversation");
        Assert.False(single.CanSubmit);
        single.Options[0].IsSelected = true;
        single.Options[1].IsSelected = true;
        Assert.Equal("Red", single.Answer);
        single.UseCustomAnswer = true;
        Assert.False(single.CanSubmit);
        single.CustomAnswer = "  Green  ";
        Assert.Equal(("Green", false), (single.Answer, single.Options.Any(o => o.IsSelected)));

        var multi = new QuestionDialogViewModel(new("Which?", null,
            [new("Blue", null, null), new("Red", null, null)], MultiSelect: true), "Project", "Conversation");
        multi.Options[0].IsSelected = true;
        multi.Options[1].IsSelected = true;
        Assert.Equal("Blue, Red", multi.Answer);

        var open = new QuestionDialogViewModel(new("Anything else?", null, [], MultiSelect: false), "Project", "Conversation");
        Assert.True(open.UseCustomAnswer);
    }

    [Fact]
    public void QuestionMarkdownListsTheHeaderQuestionAndOptions() =>
        Assert.Equal("**Color**\n\nWhich color?\n\n- **Blue**: Calm\n- **Red**",
            ClaudeTurnRunner.QuestionMarkdown(new("Which color?", "Color",
                [new ClaudeCliQuestionOption("Blue", "Calm", null), new("Red", null, null)], MultiSelect: false)));
}
