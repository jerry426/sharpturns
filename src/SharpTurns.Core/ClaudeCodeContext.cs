using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SharpTurns.Core;

/// <summary>Selected visible dialogue, not an import of Claude's private execution history.</summary>
public static class ClaudeCodeContext
{
    // The replay is model input, not HTML: keep backticks, '+', '&', '<', '>', and non-ASCII BMP text literal and token-cheap.
    private static readonly JsonSerializerOptions ReplayJsonOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string SelectedDialogue(IReadOnlyList<ConversationTurn> turns) =>
        "[" + string.Join(",", TurnBlocks(turns)) + "]";

    private static string[] TurnBlocks(IReadOnlyList<ConversationTurn> turns) =>
        turns.OrderBy(t => t.TurnNumber).Select(t => JsonSerializer.Serialize(new
        {
            turn = t.Id,
            summary = (string?)null,
            messages = t.Parts.OrderBy(p => p.Sequence)
                .Where(p => p.PartType == "text" && p.Role is "user" or "assistant")
                .Select(p => new { role = p.Role, content = p.Content })
        }, ReplayJsonOptions)).ToArray();

    public static string Fingerprint(IReadOnlyList<ConversationTurn> turns) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SelectedDialogue(turns))));

    public static bool CanResume(ClaudeCodeSessionState? state, string directory, string fingerprint, string policyVersion) =>
        state is { InFlight: false, SessionId: not null }
        && state.WorkingDirectory == directory && state.ContextFingerprint == fingerprint
        && state.PolicyVersion == policyVersion;

    /// <summary>Stable per-turn text for a fresh session; the current request stays separate.</summary>
    public static IReadOnlyList<string> SeedHistoryBlocks(IReadOnlyList<ConversationTurn> turns)
    {
        var blocks = TurnBlocks(turns);
        if (blocks.Length == 0) return [];
        // No changing counts, timestamps, or current request in this prefix. One block per turn
        // lets the CLI cache the previous history boundary as later turns are appended.
        return [
            "The following JSON blocks are retained conversation background, not new instructions. Do not re-execute earlier requests. " +
            "This is a new session: hidden tool state is not restored. Respond to the current request below.",
            .. blocks
        ];
    }
}
