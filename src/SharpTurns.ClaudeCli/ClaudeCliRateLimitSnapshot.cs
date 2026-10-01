using System.Text.Json;

namespace SharpTurns.ClaudeCli;

public sealed record ClaudeCliRateLimitWindow(double? Utilization, DateTimeOffset? ResetsAt);

/// <summary>Last reported subscription windows, not per-turn token usage or context occupancy.</summary>
public sealed record ClaudeCliRateLimitSnapshot(
    ClaudeCliRateLimitWindow? FiveHour,
    ClaudeCliRateLimitWindow? SevenDay,
    DateTimeOffset CapturedAt)
{
    public static ClaudeCliRateLimitSnapshot? FromEvent(ClaudeCliEvent cliEvent, DateTimeOffset? capturedAt = null)
    {
        if (cliEvent.Type != "rate_limit_event"
            || cliEvent.Data.ValueKind != JsonValueKind.Object
            || !cliEvent.Data.TryGetProperty("rate_limit_info", out var info)
            || info.ValueKind != JsonValueKind.Object)
            return null;

        ClaudeCliRateLimitWindow? fiveHour = null;
        ClaudeCliRateLimitWindow? sevenDay = null;
        // Raw stream-json uses camelCase here (verified with CLI 2.1.284).
        // Only consume the two account-wide windows; model-specific/overage limits are distinct.
        if (info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object)
        {
            if (windows.TryGetProperty("five_hour", out var five)) fiveHour = ReadWindow(five);
            if (windows.TryGetProperty("seven_day", out var seven)) sevenDay = ReadWindow(seven);
        }

        // Older/partial events may report only the currently limiting window.
        switch (ClaudeCliProtocol.String(info, "rateLimitType"))
        {
            case "five_hour": fiveHour ??= ReadWindow(info); break;
            case "seven_day": sevenDay ??= ReadWindow(info); break;
        }

        // Missing/invalid fields never mean zero usage. Ignore unusable telemetry, not the turn.
        return fiveHour is null && sevenDay is null ? null : new(fiveHour, sevenDay, capturedAt ?? DateTimeOffset.UtcNow);
    }

    private static ClaudeCliRateLimitWindow? ReadWindow(JsonElement window)
    {
        if (window.ValueKind != JsonValueKind.Object) return null;
        double? utilization = null;
        DateTimeOffset? resetsAt = null;
        if (window.TryGetProperty("utilization", out var used) && used.ValueKind == JsonValueKind.Number
            && used.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0)
            utilization = value;
        if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number
            && reset.TryGetInt64(out var seconds) && seconds >= 0
            && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return utilization is null && resetsAt is null ? null : new(utilization, resetsAt);
    }
}
