using System.Text.Json;
using SharpTurns.ClaudeCli;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ClaudeCliActivityTests
{
    [Fact]
    public void StreamedToolInputIsCapturedAndTheResultFinishesTheCall()
    {
        var activity = new ClaudeCliActivity();
        activity.Observe(Event("""{"type":"stream_event","event":{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"Bash","input":{}}}}"""));
        activity.Observe(Event("""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"command\":"}}}"""));
        activity.Observe(Event("""{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"ls\"}"}}}"""));
        activity.Observe(Event("""{"type":"stream_event","event":{"type":"content_block_stop","index":1}}"""));

        var running = Assert.Single(activity.TakeChanges());
        Assert.Equal(("toolu_1", "Bash", """{"command":"ls"}""", ClaudeCliActivity.Running, false),
            (running.ToolUseId, running.Name, running.Input, running.Status, running.IsFinished));

        activity.Observe(Event("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","is_error":true,"content":[{"type":"text","text":"exit 1"}]}]}}"""));

        var failed = Assert.Single(activity.TakeChanges());
        Assert.Equal((ClaudeCliActivity.Failed, true, true, "exit 1"), (failed.Status, failed.IsFinished, failed.IsError, failed.Result));
        Assert.Empty(activity.TakeChanges());
    }

    [Fact]
    public void DenialIsFinalAndFinishClosesCallsWithoutResults()
    {
        var activity = new ClaudeCliActivity();
        activity.Observe(Event("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"a.txt"}},{"type":"tool_use","id":"toolu_2","name":"AskUserQuestion","input":{}}]}}"""));
        activity.Observe(Event("""{"type":"assistant","parent_tool_use_id":"toolu_9","message":{"content":[{"type":"tool_use","id":"toolu_3","name":"Grep","input":{}}]}}"""));
        var write = new ClaudeCliPermissionRequest("request-1", "Write", JsonSerializer.SerializeToElement(new { file_path = ".claude/settings.json" }), "toolu_4");
        activity.Permission(write, "Waiting for your approval");
        activity.Permission(write, "Denied by you", final: true);

        // Questions have their own cards, and subagent calls are not the main agent's activity.
        Assert.Equal(["toolu_1", "toolu_4"], activity.TakeChanges().Select(c => c.ToolUseId));

        activity.Observe(Event("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_4","content":"denied"}]}}"""));
        activity.Finish("Stopped before a result");

        var changes = activity.TakeChanges();
        Assert.Equal(("Denied by you", true), (changes.Single(c => c.ToolUseId == "toolu_4").Status, changes.Single(c => c.ToolUseId == "toolu_4").IsError));
        Assert.Equal("Stopped before a result", changes.Single(c => c.ToolUseId == "toolu_1").Status);
        activity.Observe(Event("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"late"}]}}"""));
        Assert.Empty(activity.TakeChanges());
    }

    [Fact]
    public void UsageSumsResultCyclesAndTracksTheFirstAndLatestRequests()
    {
        var usage = new ClaudeCliUsageTracker();
        Assert.True(usage.Observe(Event("""{"type":"stream_event","event":{"type":"message_start","message":{"usage":{"input_tokens":5,"cache_read_input_tokens":900,"cache_creation_input_tokens":95,"output_tokens":1}}}}""")));
        Assert.True(usage.Observe(Event("""{"type":"stream_event","event":{"type":"message_start","message":{"usage":{"input_tokens":7,"cache_read_input_tokens":1100,"cache_creation_input_tokens":3,"output_tokens":1}}}}""")));
        Assert.True(usage.Observe(Event("""{"type":"result","usage":{"input_tokens":10,"cache_read_input_tokens":1800,"cache_creation_input_tokens":190,"output_tokens":40}}""")));
        Assert.True(usage.Observe(Event("""{"type":"result","usage":{"input_tokens":3,"output_tokens":2}}""")));
        // A subagent's requests are not the conversation's context.
        Assert.False(usage.Observe(Event("""{"type":"stream_event","parent_tool_use_id":"toolu_1","event":{"type":"message_start","message":{"usage":{"input_tokens":99}}}}""")));
        Assert.False(usage.Observe(Event("""{"type":"result","usage":{"output_tokens":"bad"}}""")));

        Assert.Equal(new ClaudeCliUsage(2003, 1800, 42, 1110, 2, 1000, 900), usage.Current);
    }

    [Fact]
    public async Task InputQueueHoldsFiveMessagesAndClosesOnlyWhenEmpty()
    {
        var queue = new ClaudeCliInputQueue();
        Assert.False(queue.TryEnqueue("  ", out _));
        for (var i = 0; i < ClaudeCliInputQueue.Capacity; i++) Assert.True(queue.TryEnqueue($" message {i} ", out _));
        Assert.False(queue.TryEnqueue("one too many", out _));

        await queue.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(queue.TryClose());
        var taken = queue.Take();
        Assert.Equal("message 0", taken[0].Text);
        Assert.True(Guid.TryParse(taken[0].Uuid, out _));
        Assert.False(queue.WaitAsync(CancellationToken.None).IsCompleted);

        Assert.True(queue.TryClose());
        Assert.False(queue.TryEnqueue("late", out _));
        await queue.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(queue.Take());
    }

    [Fact]
    public void QuestionsParseAndAnswersKeepTheOriginalInput()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            questions = new object[]
            {
                new { question = "Which color?", header = "Color", multiSelect = true,
                    options = new object[] { new { label = "Blue", description = "Calm" }, new { label = "Red", preview = "#f00" } } },
                new { question = "Anything else?" },
            },
        });

        var questions = ClaudeCliQuestions.Parse(input);

        Assert.Equal(("Which color?", "Color", true), (questions[0].Text, questions[0].Header, questions[0].MultiSelect));
        Assert.Equal(new ClaudeCliQuestionOption[] { new("Blue", "Calm", null), new("Red", null, "#f00") }, questions[0].Options);
        Assert.Empty(questions[1].Options);
        var answered = ClaudeCliQuestions.Answer(input, new Dictionary<string, string> { ["Which color?"] = "Blue, Red" });
        Assert.Equal("Blue, Red", answered.GetProperty("answers").GetProperty("Which color?").GetString());
        Assert.Equal(2, answered.GetProperty("questions").GetArrayLength());
        Assert.Throws<InvalidDataException>(() => ClaudeCliQuestions.Parse(JsonSerializer.SerializeToElement(new { questions = Array.Empty<object>() })));
        Assert.Throws<InvalidDataException>(() => ClaudeCliQuestions.Parse(JsonSerializer.SerializeToElement(new
        {
            questions = new[] { new { question = "Pick", options = new[] { new { label = "" } } } },
        })));
    }

    private static ClaudeCliEvent Event(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ClaudeCliProtocol.ParseEvent(document.RootElement);
    }
}
