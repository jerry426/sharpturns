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
        Assert.StartsWith("The following JSON blocks are retained conversation background", blocks[0].Text);
        // Text-only turns keep the phase 1 bytes, so existing sessions stay resumable.
        Assert.Equal("""{"turn":10,"summary":null,"messages":[{"role":"user","content":"first"},{"role":"assistant","content":"answer"}]}""",
            blocks[1].Text);
        // Replay text stays literal and token-cheap rather than HTML-escaped.
        Assert.Contains("second `code` & <tags>", blocks[2].Text);
        Assert.Contains("réponse", blocks[2].Text);
        // Earlier blocks keep their exact text as the conversation grows.
        Assert.Equal(blocks[1].Text, ClaudeCodeContext.SeedHistoryBlocks([.. turns, Turn(3, 12, "third", "more")])[1].Text);
    }

    [Fact]
    public void ReplayKeepsDialogueInOrderAndLeavesToolActivityOut()
    {
        var turn = Turn(1, 10, "prompt", "first answer") with
        {
            Parts =
            [
                new(1, "user", "text", "prompt"),
                new(2, "assistant", "text", "first answer"),
                TurnParts.FromTool(3, new("toolu_1", "Bash", """{"command":"ls"}""", "file.txt", "Completed", false)),
                TurnParts.FromQuestion(4, new("Which color?", "Blue")),
                TurnParts.FromQuestion(5, new("Which size?", null)),
                new(6, "user", "text", "also check tests"),
                new(7, "assistant", "text", "done"),
            ],
        };

        using var json = JsonDocument.Parse(ClaudeCodeContext.SeedHistoryBlocks([turn])[1].Text);
        var messages = json.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => (m.GetProperty("role").GetString(), m.GetProperty("content").GetString())).ToArray();

        Assert.Equal(new (string?, string?)[]
        {
            ("user", "prompt"), ("assistant", "first answer"),
            ("assistant", "Which color?"), ("user", "Blue"),
            ("assistant", "Which size?"), ("user", "(The user declined to answer.)"),
            ("user", "also check tests"), ("assistant", "done"),
        }, messages);
        Assert.Equal(ClaudeCodeContext.Fingerprint([turn]), ClaudeCodeContext.Fingerprint([turn with
        {
            Parts = [.. turn.Parts.Where(p => p.PartType != TurnParts.Tool)],
        }]));
    }

    [Fact]
    public void ImagesReplayAsNumberedDescriptorsAndBlocks()
    {
        var image = new ImageAttachment("shot.png", "image/png", "iVBORw0KGgo=");
        var turn = Turn(1, 10, "look", "seen") with
        {
            Parts = [new(1, "user", "text", "look"), TurnParts.FromImage(2, image), new(3, "assistant", "text", "seen")],
        };

        var block = ClaudeCodeContext.SeedHistoryBlocks([turn])[1];

        Assert.Equal([image], block.Images);
        using var json = JsonDocument.Parse(block.Text);
        var descriptor = json.RootElement.GetProperty("messages")[0].GetProperty("images")[0];
        Assert.Equal(1, descriptor.GetProperty("image_block").GetInt32());
        Assert.Equal("shot.png", descriptor.GetProperty("file_name").GetString());
        Assert.NotEqual(ClaudeCodeContext.Fingerprint([turn]), ClaudeCodeContext.Fingerprint([turn with
        {
            Parts = [turn.Parts[0], TurnParts.FromImage(2, image with { Data = "R0lGODlh" }), turn.Parts[2]],
        }]));
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
