using SharpTurns.App.ViewModels;
using SharpTurns.App.Views;
using SharpTurns.Core;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ConversationSearchTests
{
    [Fact]
    public void TheIndexCountsShownTextInDisplayOrder()
    {
        static TurnViewModel Saved(long id, params TurnPart[] parts) =>
            new(new ConversationTurn(id, 1, (int)id, TurnStatus.Completed, null, DateTimeOffset.UnixEpoch, null, parts));
        var first = Saved(1,
            new(1, "user", TurnParts.Text, "Find the **bug**"),
            new(2, "assistant", TurnParts.Text, "The **bug** is here.\n\n`bug` again"),
            TurnParts.FromTool(3, new ToolCallRecord("t1", "Grep", "{\"pattern\":\"bug\"}", "bug found", "completed", false)),
            TurnParts.FromQuestion(4, new QuestionRecord("Is it a bug?", "Yes, a bug.")));
        var second = Saved(2,
            new(1, "user", TurnParts.Text, "No match"),
            new(2, "user", TurnParts.Text, "bug report"));
        TurnViewModel[] turns = [first, second];

        var index = ConversationSearchIndex.Build("BUG", ConversationSearchIndex.CaptureSources(turns, renderMarkdown: true));

        // The collapsed tool card is left out; the prompt, response, question, answer, and the message sent during a
        // turn each count, case-insensitively.
        Assert.Equal(
            [ConversationSearchSegmentKind.Prompt, ConversationSearchSegmentKind.Text, ConversationSearchSegmentKind.Question,
                ConversationSearchSegmentKind.Answer, ConversationSearchSegmentKind.Prompt, ConversationSearchSegmentKind.Text],
            index.Entries.Select(e => e.Kind));
        Assert.Equal([1, 2, 1, 1, 0, 1], index.Entries.Select(e => e.MatchCount));
        Assert.Equal(6, index.TotalMatches);
        var response = first.Items.OfType<TextItemViewModel>().Single();
        Assert.True(index.TryGetEntry(response, ConversationSearchSegmentKind.Text, out var entry));
        Assert.Equal(1, entry.FirstMatchIndex);
        Assert.Same(second, index.Entries[4].Item);

        // An open tool card adds its input and result.
        first.Items.OfType<ToolItemViewModel>().Single().IsExpanded = true;
        Assert.Equal(8, ConversationSearchIndex.Build("bug", ConversationSearchIndex.CaptureSources(turns, renderMarkdown: true)).TotalMatches);

        // Rendered Markdown is searched as displayed; its source, and the plain prompt, include the markup.
        Assert.Equal(2, ConversationSearchIndex.Build("**", ConversationSearchIndex.CaptureSources(turns, renderMarkdown: true)).TotalMatches);
        Assert.Equal(4, ConversationSearchIndex.Build("**", ConversationSearchIndex.CaptureSources(turns, renderMarkdown: false)).TotalMatches);
    }

    [Fact]
    public void MatchNavigationWrapsAndFollowsTheCount()
    {
        var display = new ConversationDisplayViewModel { SearchQuery = "bug" };
        Assert.True(display.IsSearchActive);
        Assert.Equal("0 matches", display.SearchMatchStatusLabel);
        Assert.False(display.NextSearchMatchCommand.CanExecute(null));

        display.SetSearchTotalMatches(3);
        Assert.Equal("3 matches", display.SearchMatchStatusLabel);
        display.NextSearchMatchCommand.Execute(null);
        Assert.Equal("1 of 3", display.SearchMatchStatusLabel);
        display.PreviousSearchMatchCommand.Execute(null);
        Assert.Equal("3 of 3", display.SearchMatchStatusLabel);
        display.NextSearchMatchCommand.Execute(null);
        Assert.Equal(0, display.CurrentMatchIndex);

        display.PreviousSearchMatchCommand.Execute(null);
        display.SetSearchTotalMatches(2);
        Assert.Equal("2 of 2", display.SearchMatchStatusLabel);
        display.SetSearchTotalMatches(1);
        Assert.Equal("1 of 1", display.SearchMatchStatusLabel);

        display.SearchQuery = "fix";
        Assert.Equal(-1, display.CurrentMatchIndex);
        display.ClearSearchCommand.Execute(null);
        Assert.Equal("", display.SearchQuery);
        Assert.Equal("", display.SearchMatchStatusLabel);
        Assert.False(display.ClearSearchCommand.CanExecute(null));
    }
}
