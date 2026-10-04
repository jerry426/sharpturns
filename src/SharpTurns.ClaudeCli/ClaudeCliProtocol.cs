using System.Text.Json;

namespace SharpTurns.ClaudeCli;

public sealed record ClaudeCliEvent(string Type, string? Text, string? SessionId, JsonElement Data)
{
    public string? Thinking { get; init; }
}

public sealed record ClaudeCliPermissionRequest(string RequestId, string ToolName, JsonElement Input,
    string? ToolUseId = null);

public sealed record ClaudeCliPermissionDecision(bool Allow, JsonElement? UpdatedInput = null,
    string Message = "Denied by the host");

public sealed record ClaudeCliResult(string SessionId, bool IsError, JsonElement Data);

public sealed record ClaudeCliImage(string MediaType, string Data);

public sealed record ClaudeCliHistoryBlock(string Text, IReadOnlyList<ClaudeCliImage>? Images = null);

public sealed record ClaudeCliUserMessage(string Uuid, string Text);

/// <summary>A stdio MCP server for a turn to start. Command is the argv; WorkingDirectory null keeps the turn's.</summary>
public sealed record ClaudeCliMcpServer(string Name, IReadOnlyList<string> Command,
    IReadOnlyDictionary<string, string> Environment, string? WorkingDirectory = null);

/// <summary>Host-owned input queue. Take and TryClose are serialized with CLI submission/completion.</summary>
public sealed record ClaudeCliTurnInput(
    Func<CancellationToken, Task> WaitAsync,
    Func<IReadOnlyList<ClaudeCliUserMessage>> Take,
    Func<bool> TryClose);

// Raw control protocol verified against anthropics/claude-agent-sdk-python's
// _internal/query.py. This is a version-sensitive CLI boundary, not an API provider.
public static class ClaudeCliProtocol
{
    public static object UserMessage(string prompt, string? sessionId,
        IReadOnlyList<ClaudeCliHistoryBlock>? retainedHistory = null, IReadOnlyList<ClaudeCliImage>? images = null,
        IReadOnlyList<string>? contextFiles = null, string? uuid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        object content = prompt;
        var hasHistory = retainedHistory is { Count: > 0 };
        var hasContextFiles = contextFiles is { Count: > 0 };
        if (hasHistory && sessionId is not null)
            throw new ArgumentException("Retained history is only valid for a fresh Claude session.", nameof(retainedHistory));
        if (hasContextFiles && sessionId is not null)
            throw new ArgumentException("Context files are only valid for a fresh Claude session.", nameof(contextFiles));
        if (hasHistory || hasContextFiles || images is { Count: > 0 })
        {
            var blocks = new List<Dictionary<string, object>>();
            foreach (var history in retainedHistory ?? [])
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(history.Text, nameof(retainedHistory));
                blocks.Add(new() { ["type"] = "text", ["text"] = history.Text });
                AddImages(blocks, history.Images);
            }
            if (hasHistory)
            {
                // Verified through tool continuations with CLI 2.1.284 only when the client
                // disables experimental betas; otherwise the CLI plus this marker exceeds four.
                // Earlier text/image blocks stay unchanged as the single history marker moves.
                // Include retained images, but never mark the changing current request or images.
                blocks[^1]["cache_control"] = new { type = "ephemeral", ttl = "1h" };
            }
            // Volatile file snapshots follow the history boundary, never adding a cache marker.
            foreach (var file in contextFiles ?? [])
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(file, nameof(contextFiles));
                blocks.Add(new() { ["type"] = "text", ["text"] = file });
            }
            blocks.Add(new() { ["type"] = "text", ["text"] = hasHistory || hasContextFiles ? "Current user request:\n" + prompt : prompt });
            AddImages(blocks, images);
            content = blocks;
        }
        return new { type = "user", session_id = sessionId ?? "", uuid, client_composed = true,
            message = new { role = "user", content }, parent_tool_use_id = (string?)null };
    }

    private static void AddImages(List<Dictionary<string, object>> blocks, IReadOnlyList<ClaudeCliImage>? images)
    {
        foreach (var image in images ?? [])
        {
            if (image.MediaType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp"))
                throw new NotSupportedException("Claude Code image attachments must be PNG, JPEG, GIF, or WebP.");
            if (string.IsNullOrWhiteSpace(image.Data) || !System.Buffers.Text.Base64.IsValid(image.Data))
                throw new ArgumentException("Claude Code image attachments require valid, nonempty base64 data.", nameof(images));
            blocks.Add(new()
            {
                ["type"] = "image",
                ["source"] = new { type = "base64", media_type = image.MediaType, data = image.Data }
            });
        }
    }

    public static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) &&
        field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    public static ClaudeCliEvent ParseEvent(JsonElement data)
    {
        var type = String(data, "type") ?? throw new InvalidDataException("CLI event has no type.");
        string? text = null;
        string? thinking = null;
        // Complete assistant/result messages repeat streamed text/thinking. Never append those again.
        if (type == "stream_event" && data.TryGetProperty("event", out var streamEvent) &&
            String(streamEvent, "type") == "content_block_delta" &&
            streamEvent.TryGetProperty("delta", out var delta) &&
            (!data.TryGetProperty("parent_tool_use_id", out var parent) || parent.ValueKind == JsonValueKind.Null))
        {
            if (String(delta, "type") == "text_delta") text = String(delta, "text");
            if (String(delta, "type") == "thinking_delta") thinking = String(delta, "thinking");
            // Signatures and redacted thinking are opaque protocol data, not display content.
        }

        return new(type, text, String(data, "session_id"), data.Clone()) { Thinking = thinking };
    }

    public static object PermissionResponse(ClaudeCliPermissionRequest request, ClaudeCliPermissionDecision decision)
    {
        object response = decision.Allow
            ? new { behavior = "allow", updatedInput = decision.UpdatedInput ?? request.Input }
            : new { behavior = "deny", message = decision.Message };
        return new { type = "control_response", response = new { subtype = "success", request_id = request.RequestId, response } };
    }
}
