using System.Text.Json;
using SharpTurns.Core;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ClaudeCodeContextTests
{
    private const string Policy = "policy-1";
    private const string Directory = "/work/project";

    [Fact]
    public void SeedBlocksAreThePreambleThenOneStableBlockPerTurnInOrder()
    {
        var turns = new[] { Turn(2, 11, "second `code` & <tags>", "réponse"), Turn(1, 10, "first", "answer") };
        Assert.Empty(ClaudeCodeContext.SeedHistoryBlocks([]));

        var blocks = ClaudeCodeContext.SeedHistoryBlocks(turns);

        Assert.Equal(3, blocks.Count);
        Assert.StartsWith("The following JSON blocks are retained conversation background", blocks[0]);
        using var first = JsonDocument.Parse(blocks[1]);
        Assert.Equal(10, first.RootElement.GetProperty("turn").GetInt64());
        var messages = first.RootElement.GetProperty("messages");
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("first", messages[0].GetProperty("content").GetString());
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        // Replay text stays literal and token-cheap rather than HTML-escaped.
        Assert.Contains("second `code` & <tags>", blocks[2]);
        Assert.Contains("réponse", blocks[2]);
        // Earlier blocks keep their exact text as the conversation grows.
        Assert.Equal(blocks[1], ClaudeCodeContext.SeedHistoryBlocks([.. turns, Turn(3, 12, "third", "more")])[1]);
    }

    [Fact]
    public void FingerprintFollowsVisibleTextOnly()
    {
        var turn = Turn(1, 10, "prompt", "answer");
        var fingerprint = ClaudeCodeContext.Fingerprint([turn]);

        var withThinking = turn with { Parts = [.. turn.Parts, new(4, "assistant", "thinking", "private reasoning")] };
        var edited = turn with { Parts = [turn.Parts[0], turn.Parts[1] with { Content = "different answer" }] };

        Assert.Equal(fingerprint, ClaudeCodeContext.Fingerprint([withThinking]));
        Assert.NotEqual(fingerprint, ClaudeCodeContext.Fingerprint([edited]));
        Assert.NotEqual(fingerprint, ClaudeCodeContext.Fingerprint([]));
    }

    [Fact]
    public void ResumeRequiresASettledSessionWithTheSameDirectoryHistoryAndPolicy()
    {
        var state = new ClaudeCodeSessionState("700c7fa5-e552-450e-8712-3ebb74e2857c", Directory, "F", InFlight: false, Policy);

        Assert.True(ClaudeCodeContext.CanResume(state, Directory, "F", Policy));
        Assert.False(ClaudeCodeContext.CanResume(null, Directory, "F", Policy));
        Assert.False(ClaudeCodeContext.CanResume(state with { InFlight = true }, Directory, "F", Policy));
        Assert.False(ClaudeCodeContext.CanResume(state with { SessionId = null }, Directory, "F", Policy));
        Assert.False(ClaudeCodeContext.CanResume(state, "/work/other", "F", Policy));
        Assert.False(ClaudeCodeContext.CanResume(state, Directory, "G", Policy));
        Assert.False(ClaudeCodeContext.CanResume(state, Directory, "F", "policy-2"));
    }

    private static ConversationTurn Turn(int number, long id, string prompt, string answer) =>
        new(id, 1, number, TurnStatus.Completed, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            [new(1, "user", "text", prompt), new(3, "assistant", "text", answer)]);
}
