using System.Text.Json;

namespace SharpTurns.Core.InstanceManagement;

/// <summary>
/// A snapshot of one running SharpTurns process, sent over local IPC so the Instance Manager can show live state and
/// prune unresponsive instances.
/// </summary>
/// <param name="Id">Per-process-run GUID instance identifier (stable across heartbeats).</param>
/// <param name="Pid">Host process id of the running instance.</param>
/// <param name="ProjectId">Selected project id, or null when none is selected.</param>
/// <param name="ProjectTitle">Selected project name, or null when none is selected.</param>
/// <param name="ConversationId">Open conversation id, or null when none is open.</param>
/// <param name="ConversationTitle">Open conversation title, or null when none is open.</param>
/// <param name="ConversationModel">Open conversation's model ID, or null when it uses the CLI's default.</param>
/// <param name="TurnActive">Whether a turn or summary is running in any of this instance's conversations.</param>
/// <param name="Status">Lifecycle status: <c>running</c> or <c>stopped</c>.</param>
/// <param name="TimestampStarted">When the instance registered (preserved across heartbeats).</param>
/// <param name="HeartbeatAt">Last heartbeat time.</param>
/// <param name="TimestampUpdated">Last snapshot update time.</param>
/// <param name="ProjectColor">The selected project's border color.</param>
public sealed record AppInstanceRecord(
    string Id,
    int Pid,
    long? ProjectId,
    string? ProjectTitle,
    long? ConversationId,
    string? ConversationTitle,
    string? ConversationModel,
    bool TurnActive,
    string Status,
    DateTimeOffset TimestampStarted,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset TimestampUpdated,
    string? ProjectColor = null);

/// <summary>The project and conversation a reloaded instance reopens.</summary>
public sealed record AppStartupSession(long? ProjectId, long? ConversationId);

/// <summary>
/// Environment contract used to correlate a manager-launched SharpTurns process with the IPC reports it sends.
/// </summary>
public static class AppInstanceLaunchEnvironment
{
    public const string InstanceIdVariable = "SHARPTURNS_APP_INSTANCE_ID";
    public const string StartupSessionVariable = "SHARPTURNS_APP_STARTUP_SESSION";

    // Consume once so the CLI and other child processes cannot inherit this reload request.
    public static AppStartupSession? ConsumeStartupSession()
    {
        var json = Environment.GetEnvironmentVariable(StartupSessionVariable);
        Environment.SetEnvironmentVariable(StartupSessionVariable, null);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<AppStartupSession>(json); }
        catch (JsonException) { return null; }
    }
}
