using System.Text.Json;

namespace SharpTurns.ClaudeCli;

/// <summary>
/// Token counts for one turn. Input includes cache reads and writes, summed over every model request in the turn.
/// ContextTokens is the input size of the latest main-agent request: how much context the conversation now uses.
/// </summary>
public sealed record ClaudeCliUsage(long InputTokens, long CachedInputTokens, long OutputTokens, long? ContextTokens);

/// <summary>Accumulates usage from CLI events. Not thread-safe: callers serialize Observe.</summary>
public sealed class ClaudeCliUsageTracker
{
    private long _input;
    private long _cached;
    private long _output;
    private long? _context;

    public ClaudeCliUsage Current => new(_input, _cached, _output, _context);

    /// <summary>Returns whether the usage changed.</summary>
    public bool Observe(ClaudeCliEvent e)
    {
        var data = e.Data;
        if (data.ValueKind != JsonValueKind.Object
            || data.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind != JsonValueKind.Null)
            return false;
        // result.usage covers one agent cycle; a turn with mid-turn messages can have several.
        if (e.Type == "result" && data.TryGetProperty("usage", out var cycle) && InputTotal(cycle) is { } input)
        {
            _input += input;
            _cached += Count(cycle, "cache_read_input_tokens") ?? 0;
            _output += Count(cycle, "output_tokens") ?? 0;
            return true;
        }
        // Output counts on message_start are placeholders; only its input size is used.
        if (e.Type == "stream_event" && data.TryGetProperty("event", out var stream)
            && ClaudeCliProtocol.String(stream, "type") == "message_start"
            && stream.TryGetProperty("message", out var message) && message.TryGetProperty("usage", out var usage)
            && InputTotal(usage) is { } context)
        {
            _context = context;
            return true;
        }
        return false;
    }

    private static long? InputTotal(JsonElement usage) =>
        Count(usage, "input_tokens") is { } input
            ? input + (Count(usage, "cache_read_input_tokens") ?? 0) + (Count(usage, "cache_creation_input_tokens") ?? 0)
            : null;

    // Missing or invalid telemetry never fails a turn.
    private static long? Count(JsonElement usage, string name) =>
        usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0
            ? count : null;
}
