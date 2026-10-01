using System.Globalization;
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
        // Turns saved before request counts were recorded show totals only.
        Assert.Equal(($"Input Tokens: {12_345:N0}", "First-Request Cache Hits: not recorded", $"Context Size: {45_600:N0} tokens"),
            (turn.InputTokensLabel, turn.FirstRequestCacheLabel, turn.ContextLabel));
    }

    [Fact]
    public void TheRailAndCopiesDescribeTheSavedTurn()
    {
        var created = new DateTimeOffset(2026, 10, 1, 15, 19, 0, TimeSpan.Zero);
        var turn = new TurnViewModel(new ConversationTurn(18620, 7, 30, TurnStatus.Completed, null, created, created.AddSeconds(6.3),
        [
            new(1, "user", TurnParts.Text, "prompt"),
            TurnParts.FromImage(2, new ImageAttachment("shot.png", "image/png", "iVBORw0KGgo=")),
            new(3, "assistant", TurnParts.Text, "First."),
            TurnParts.FromTool(4, new("toolu_1", "Bash", """{"command":"ls"}""", "a.txt", "Completed", false)),
            new(5, "assistant", TurnParts.Text, "Second."),
            new(6, "user", TurnParts.Text, "also this"),
            TurnParts.FromQuestion(7, new("Which?", "Blue")),
        ], new TurnUsage(189_351, 106_003, 219, 95_000, 2, 94_000, 11_590), "claude-opus-5-5"));

        Assert.Equal(("Turn - 30", "id: 18620", $"Duration: {6.3:0.#}s", "claude-opus-5-5"),
            (turn.TurnLabel, turn.IdLabel, turn.DurationLabel, turn.ModelLabel));
        Assert.Equal($"Input Tokens: {189_351:N0} · 2 requests", turn.InputTokensLabel);
        Assert.Equal($"Input Cache Hits: {106_003:N0} ({56.0:0.0}%)", turn.CacheHitLabel);
        Assert.Equal($"Input Cache Misses: {83_348:N0} ({44.0:0.0}%)", turn.CacheMissLabel);
        Assert.Equal($"First-Request Cache Hits: {11_590:N0} ({12.3:0.0}%)", turn.FirstRequestCacheLabel);
        Assert.Contains(turn.CacheMissLabel, turn.FormatMetrics());

        var timestamp = created.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        Assert.Equal($"Conversation ID: 7\nTurn ID: 18620\nTurn #30\nTimestamp: {timestamp}\n\n## User\n\nprompt\n\n[Image: shot.png]"
            + "\n\n## Assistant\n\nFirst.\n\nSecond.\n\n## User (sent during the turn)\n\nalso this\n\n## Assistant\n\nWhich?\n\n**Answer:** Blue",
            turn.FormatForClipboard(7));
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
        Assert.Equal([image], turn.Images.Select(i => i.Preview.Attachment));
        Assert.Equal([typeof(TextItemViewModel), typeof(ToolItemViewModel), typeof(QuestionItemViewModel),
            typeof(UserMessageItemViewModel), typeof(TextItemViewModel)], turn.Items.Select(i => i.GetType()));
        Assert.Equal("/a/b.txt", ((ToolItemViewModel)turn.Items[1]).Summary);
        Assert.Equal("No answer; the question was declined.", ((QuestionItemViewModel)turn.Items[2]).AnswerText);
        Assert.False(turn.IsRunning);
        Assert.False(turn.HasTokenUsage);
    }

    [Fact]
    public void ImageCardsFollowTheSavedReplayChoiceAndCompression()
    {
        var turn = new TurnViewModel(new ConversationTurn(1, 1, 1, TurnStatus.Completed, null, DateTimeOffset.UnixEpoch, null,
        [
            new(1, "user", TurnParts.Text, "prompt"),
            TurnParts.FromImage(2, new ImageAttachment("a.png", "image/png", "iVBORw0KGgo=")),
            TurnParts.FromImage(3, new ImageAttachment("b.png", "image/png", "iVBORw0KGgo=")),
            new(4, "assistant", TurnParts.Text, "Done."),
        ]));
        var second = turn.Images[1];
        Assert.Equal((3, false, "Keep when compressed"), (second.Part!.Sequence, second.IncludeInFutureReplay, second.ReplayChoiceLabel));

        var record = turn.Record!;
        turn.ApplyRecord(record with
        {
            Summary = "## Work Summary\n- a",
            Parts = record.Parts.Select(p => p.Sequence == 3 ? TurnParts.WithImageReplay(p, true) : p).ToArray(),
        });

        Assert.Equal((true, "Include image in future turns"), (second.IncludeInFutureReplay, second.ReplayChoiceLabel));
        Assert.False(turn.Images[0].IncludeInFutureReplay);
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
