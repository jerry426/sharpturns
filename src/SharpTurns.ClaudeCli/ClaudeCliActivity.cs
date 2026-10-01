using System.Text;
using System.Text.Json;

namespace SharpTurns.ClaudeCli;

/// <summary>A native tool call observed in CLI output. The CLI runs the tool; the host only displays it.</summary>
/// <param name="Input">Raw JSON input, bounded; null until observed.</param>
/// <param name="Result">The tool result's text, bounded; null until observed.</param>
public sealed record ClaudeCliToolActivity(string ToolUseId, string Name, string? Input, string? Result, string Status,
    bool IsFinished, bool IsError);

/// <summary>
/// Tracks the main agent's native tool calls from stream events, completed messages, tool results, and host
/// permission requests. Not thread-safe: callers serialize every call.
/// </summary>
public sealed class ClaudeCliActivity
{
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    private const int MaxChars = 20_000;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Entry> _changed = [];
    private readonly Dictionary<int, Entry> _streamBlocks = [];
    private bool _finished;

    public void Observe(ClaudeCliEvent e)
    {
        var data = e.Data;
        if (_finished || data.ValueKind != JsonValueKind.Object
            || data.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind != JsonValueKind.Null)
            return;
        if (e.Type == "stream_event" && data.TryGetProperty("event", out var stream) && stream.ValueKind == JsonValueKind.Object)
        {
            var type = ClaudeCliProtocol.String(stream, "type");
            if (type == "message_start") _streamBlocks.Clear();
            var hasIndex = stream.TryGetProperty("index", out var indexValue)
                && indexValue.ValueKind == JsonValueKind.Number && indexValue.TryGetInt32(out _);
            var index = hasIndex ? indexValue.GetInt32() : -1;
            if (type == "content_block_start" && stream.TryGetProperty("content_block", out var block))
            {
                ObserveBlock(block, completeInput: false);
                _streamBlocks.Remove(index);
                if (hasIndex && ClaudeCliProtocol.String(block, "id") is { } id && _entries.TryGetValue(id, out var entry))
                    _streamBlocks[index] = entry;
            }
            else if (hasIndex && _streamBlocks.TryGetValue(index, out var entry))
            {
                if (type == "content_block_delta" && stream.TryGetProperty("delta", out var delta)
                    && ClaudeCliProtocol.String(delta, "type") == "input_json_delta"
                    && ClaudeCliProtocol.String(delta, "partial_json") is { } fragment)
                    entry.InputFragments.Append(fragment);
                if (type == "content_block_stop") CaptureStreamInput(entry);
            }
        }
        else if (e.Type is "assistant" or "user" && data.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var blocks)
            && blocks.ValueKind == JsonValueKind.Array)
            foreach (var item in blocks.EnumerateArray()) ObserveBlock(item);
    }

    /// <summary>Records the host's handling of a permission request. Denied and canceled requests are final.</summary>
    public void Permission(ClaudeCliPermissionRequest request, string status, bool final = false)
    {
        if (_finished) return;
        // Older CLI versions can omit tool_use_id. Do not guess by tool name.
        var entry = Get(request.ToolUseId is { Length: > 0 } id ? id : "permission:" + request.RequestId, request.ToolName);
        entry.Input = Bound(request.Input.GetRawText());
        entry.InputComplete = true;
        if (entry.Finished) return;
        entry.Status = status;
        entry.Finished = final;
        entry.IsError = final;
        MarkChanged(entry);
    }

    /// <summary>Closes tool calls whose result never arrived, for example after Stop.</summary>
    public void Finish(string status)
    {
        _finished = true;
        foreach (var entry in _entries.Values.Where(e => !e.InputComplete)) CaptureStreamInput(entry);
        foreach (var entry in _entries.Values.Where(e => !e.Finished))
        {
            entry.Status = status;
            entry.Finished = true;
            entry.IsError = true;
            MarkChanged(entry);
        }
    }

    /// <summary>Tool calls changed since the last call, in first-seen order. AskUserQuestion has its own cards.</summary>
    public IReadOnlyList<ClaudeCliToolActivity> TakeChanges()
    {
        var changes = _changed.Where(e => e.Name != "AskUserQuestion").OrderBy(e => e.Order)
            .Select(e => new ClaudeCliToolActivity(e.Id, e.Name, e.Input, e.Result, e.Status, e.Finished, e.IsError))
            .ToArray();
        _changed.Clear();
        return changes;
    }

    private void ObserveBlock(JsonElement block, bool completeInput = true)
    {
        var type = ClaudeCliProtocol.String(block, "type");
        if (type == "tool_use" && ClaudeCliProtocol.String(block, "id") is { Length: > 0 } id
            && ClaudeCliProtocol.String(block, "name") is { Length: > 0 } name)
        {
            var entry = Get(id, name);
            if ((completeInput || !entry.InputComplete) && block.TryGetProperty("input", out var input))
            {
                entry.Input = Bound(input.GetRawText());
                entry.InputComplete = completeInput;
                if (completeInput) entry.InputFragments.Clear();
                MarkChanged(entry);
            }
        }
        else if (type == "tool_result" && ClaudeCliProtocol.String(block, "tool_use_id") is { } toolId
            && _entries.TryGetValue(toolId, out var entry))
        {
            entry.Result = Bound(ResultText(block));
            MarkChanged(entry);
            if (entry.Finished) return; // Keep denial status, but show late evidence.
            // Permission acceptance and content_block_stop are not proof of execution.
            entry.IsError = block.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
            entry.Status = entry.IsError ? Failed : Completed;
            entry.Finished = true;
        }
    }

    private static string ResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return content.GetRawText();
        return string.Join("\n", content.EnumerateArray().Select(item => ClaudeCliProtocol.String(item, "type") switch
        {
            "text" => ClaudeCliProtocol.String(item, "text") ?? "",
            "image" => "[image]",
            _ => item.GetRawText(),
        }));
    }

    private Entry Get(string id, string name)
    {
        if (_entries.TryGetValue(id, out var entry)) return entry;
        entry = new() { Id = id, Name = name, Order = _entries.Count };
        _entries.Add(id, entry);
        MarkChanged(entry);
        return entry;
    }

    private void MarkChanged(Entry entry)
    {
        if (!_changed.Contains(entry)) _changed.Add(entry);
    }

    private void CaptureStreamInput(Entry entry)
    {
        if (entry.InputComplete || entry.InputFragments.Length == 0) return;
        var input = entry.InputFragments.ToString();
        try
        {
            using var json = JsonDocument.Parse(input);
            entry.Input = Bound(json.RootElement.GetRawText());
            entry.InputComplete = true;
            MarkChanged(entry);
        }
        catch (JsonException) { }
    }

    private static string Bound(string text) => text.Length <= MaxChars ? text : text[..MaxChars] + "\n… [truncated]";

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required int Order { get; init; }
        public StringBuilder InputFragments { get; } = new();
        public string? Input { get; set; }
        public bool InputComplete { get; set; }
        public string? Result { get; set; }
        public string Status { get; set; } = Running;
        public bool Finished { get; set; }
        public bool IsError { get; set; }
    }
}
