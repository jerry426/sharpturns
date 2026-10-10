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
        // Each block names both the turn's ID and its number within the conversation.
        Assert.Equal("""{"turn_id":10,"turn_number":1,"summary":null,"messages":[{"role":"user","content":"first"},{"role":"assistant","content":"answer"}]}""",
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
    public void HiddenTurnsAreLeftOutOfTheReplay()
    {
        var first = Turn(1, 10, "first", "answer");
        var second = Turn(2, 11, "second", "more");

        var blocks = ClaudeCodeContext.SeedHistoryBlocks([first with { IsHydrated = false }, second]);

        Assert.Equal(2, blocks.Count);
        Assert.Contains("\"turn_id\":11", blocks[1].Text);
        Assert.Empty(ClaudeCodeContext.SeedHistoryBlocks([first with { IsHydrated = false }]));
        Assert.NotEqual(ClaudeCodeContext.Fingerprint([first, second]),
            ClaudeCodeContext.Fingerprint([first with { IsHydrated = false }, second]));
    }

    [Fact]
    public void CompressedTurnsReplayTheirUserInputsAndSummaryWithImageDescriptors()
    {
        var image = new ImageAttachment("shot.png", "image/png", "iVBORw0KGgo=");
        var turn = Turn(1, 10, "look", "seen") with
        {
            Parts =
            [
                new(1, "user", "text", "look"),
                TurnParts.FromImage(2, image),
                new(3, "assistant", "text", "checking"),
                TurnParts.FromQuestion(4, new("Which color?", "Blue")),
                new(5, "user", "text", "also this"),
                new(6, "assistant", "text", "seen"),
            ],
            Summary = "## Work Summary\n\n- Looked.\n\n## Final Assistant Response — Verbatim\n\nseen",
        };

        var block = ClaudeCodeContext.SeedHistoryBlocks([turn])[1];

        Assert.Empty(block.Images);
        using var json = JsonDocument.Parse(block.Text);
        // The summary is a record labeled as the app's, carrying the work summary; the response is the assistant's own message.
        var record = json.RootElement.GetProperty("summary").GetString();
        Assert.StartsWith("[SHARPTURNS RECORD — COMPRESSED TURN 1]\nWritten by the SharpTurns app, not by the user or the assistant.", record);
        Assert.Contains("\nThe assistant message that ends this turn is the assistant's final response, verbatim.\n\n", record);
        Assert.EndsWith("\n\n## Work Summary\n\n- Looked.", record);
        var messages = json.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal(new (string?, string?)[]
        {
            ("user", "look\n\n[HISTORICAL ATTACHMENT DESCRIPTORS]\n"
                + "Image contents are omitted from this summarized replay of a prior turn; only their attachment descriptors are included.\n"
                + "- Image 1: file_name=\"shot.png\", media_type=\"image/png\", size_bytes=8\n[/HISTORICAL ATTACHMENT DESCRIPTORS]"),
            ("assistant", "Which color?"), ("user", "Blue"), ("user", "also this"), ("assistant", "seen"),
        }, messages.Select(m => (m.GetProperty("role").GetString(), m.GetProperty("content").GetString())));
        Assert.All(messages, m => Assert.False(m.TryGetProperty("images", out _)));
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(block.Text), ClaudeCodeContext.ReplayBytes(turn));
    }

    [Fact]
    public void CompressedTurnsReplayTheImagesChosenToStay()
    {
        var kept = new ImageAttachment("kept.png", "image/png", "iVBORw0KGgo=", IncludeInFutureReplay: true);
        var dropped = new ImageAttachment("dropped.png", "image/png", "R0lGODlh");
        var turn = Turn(1, 10, "look", "seen") with
        {
            Parts = [new(1, "user", "text", "look"), TurnParts.FromImage(2, dropped), TurnParts.FromImage(3, kept), new(4, "assistant", "text", "seen")],
            Summary = "## Work Summary\n\n- Looked.\n\n## Final Assistant Response — Verbatim\n\nseen",
        };

        var block = ClaudeCodeContext.SeedHistoryBlocks([turn])[1];

        Assert.Equal([kept], block.Images);
        using var json = JsonDocument.Parse(block.Text);
        var message = json.RootElement.GetProperty("messages")[0];
        Assert.Equal("look\n\n[HISTORICAL ATTACHMENT DESCRIPTORS]\n"
            + "Selected image contents are included in this replay of a prior turn; other images are represented only by their attachment descriptors.\n"
            + "- Image 1: file_name=\"dropped.png\", media_type=\"image/png\", size_bytes=6, contents=omitted\n"
            + "- Image 2: file_name=\"kept.png\", media_type=\"image/png\", size_bytes=8, contents=included\n"
            + "[/HISTORICAL ATTACHMENT DESCRIPTORS]", message.GetProperty("content").GetString());
        var descriptor = Assert.Single(message.GetProperty("images").EnumerateArray());
        Assert.Equal((1, "kept.png"), (descriptor.GetProperty("image_block").GetInt32(), descriptor.GetProperty("file_name").GetString()));
        Assert.Equal(1, ClaudeCodeContext.ReplayedImageCount(turn));
        Assert.Equal(0, ClaudeCodeContext.ReplayedImageCount(turn with { IsHydrated = false }));

        // In full, every image is replayed and the choice doesn't change the replay; the summarizer never sees contents.
        var full = turn with { Summary = null };
        Assert.Equal(2, ClaudeCodeContext.ReplayedImageCount(full));
        Assert.Equal(ClaudeCodeContext.Fingerprint([full]), ClaudeCodeContext.Fingerprint([full with
        {
            Parts = [.. full.Parts.Select(p => p.PartType == TurnParts.Image ? TurnParts.WithImageReplay(p, false) : p)],
        }]));
        Assert.DoesNotContain("contents=", TurnCompression.Analyze(full, out _)!.SummarizerSource);
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
