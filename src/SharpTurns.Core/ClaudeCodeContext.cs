using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpTurns.Core;

/// <summary>One retained turn for a fresh session: its JSON text, then its images in descriptor order.</summary>
public sealed record HistoryBlock(string Text, IReadOnlyList<ImageAttachment> Images);

/// <summary>Selected visible dialogue, not an import of Claude's private execution history.</summary>
public static class ClaudeCodeContext
{
    private const string DeclinedAnswer = "(The user declined to answer.)";

    // The replay is model input, not HTML: keep backticks, '+', '&', '<', '>', and non-ASCII BMP text literal and token-cheap.
    private static readonly JsonSerializerOptions ReplayJsonOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string SelectedDialogue(IReadOnlyList<ConversationTurn> turns) =>
        "[" + string.Join(",", TurnBlocks(turns).Select(block => block.Text)) + "]";

    // Hidden turns stay out of the replay.
    private static HistoryBlock[] TurnBlocks(IReadOnlyList<ConversationTurn> turns) =>
        turns.Where(t => t.IsHydrated).OrderBy(t => t.TurnNumber).Select(TurnBlock).ToArray();

    // Tool activity is display-only and stays out of the replay; questions and answers are dialogue. A compressed turn
    // keeps its user inputs, its summary replaces its assistant text, and its images are replayed as descriptors only.
    private static HistoryBlock TurnBlock(ConversationTurn t)
    {
        var messages = new List<ReplayMessage>();
        var images = new List<ImageAttachment>();
        foreach (var part in t.Parts.OrderBy(p => p.Sequence))
        {
            switch (part.PartType)
            {
                case TurnParts.Text when part.Role == "user" || part.Role == "assistant" && !t.IsCompressed:
                    messages.Add(new(part.Role, part.Content));
                    break;
                case TurnParts.Question:
                    var question = TurnParts.ReadQuestion(part);
                    messages.Add(new("assistant", question.Question));
                    messages.Add(new("user", question.Answer ?? DeclinedAnswer));
                    break;
                case TurnParts.Image:
                    var image = TurnParts.ReadImage(part);
                    // Images follow their message's text part.
                    var owner = messages.LastOrDefault(m => m.Role == "user");
                    if (owner is null) messages.Add(owner = new("user", ""));
                    if (t.IsCompressed)
                    {
                        (owner.OmittedImages ??= []).Add(image);
                        break;
                    }
                    (owner.Images ??= []).Add(new(images.Count + 1, image.FileName, image.MediaType,
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(image.Data)))));
                    images.Add(image);
                    break;
            }
        }
        for (var i = 0; i < messages.Count; i++)
            if (messages[i].OmittedImages is { } omitted)
                messages[i] = messages[i] with { Content = WithImageDescriptors(messages[i].Content, omitted), OmittedImages = null };
        return new HistoryBlock(
            JsonSerializer.Serialize(new { turn_id = t.Id, turn_number = t.TurnNumber, summary = t.Summary, messages },
                ReplayJsonOptions), images);
    }

    /// <summary>The UTF-8 size of the turn's replay block plus its images' base64 data, whether or not it is hidden.</summary>
    public static long ReplayBytes(ConversationTurn turn)
    {
        var block = TurnBlock(turn);
        return Encoding.UTF8.GetByteCount(block.Text) + block.Images.Sum(image => (long)image.Data.Length);
    }

    /// <summary>The content followed by the images' descriptors, as the Workbench replays images it leaves out.</summary>
    internal static string WithImageDescriptors(string content, IReadOnlyList<ImageAttachment> images)
    {
        var text = new StringBuilder()
            .Append("[HISTORICAL ATTACHMENT DESCRIPTORS]\n")
            .Append("Image contents are omitted from this summarized replay of a prior turn; only their attachment descriptors are included.\n");
        for (var i = 0; i < images.Count; i++)
            text.Append(CultureInfo.InvariantCulture,
                $"- Image {i + 1}: file_name={JsonSerializer.Serialize(images[i].FileName)}, media_type={JsonSerializer.Serialize(images[i].MediaType)}, size_bytes={DecodedLength(images[i].Data)}\n");
        text.Append("[/HISTORICAL ATTACHMENT DESCRIPTORS]");
        return content.Length == 0 ? text.ToString() : content + "\n\n" + text;
    }

    private static long DecodedLength(string base64) =>
        base64.Length / 4 * 3L - (base64.EndsWith("==", StringComparison.Ordinal) ? 2 : base64.EndsWith('=') ? 1 : 0);

    public static string Fingerprint(IReadOnlyList<ConversationTurn> turns) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SelectedDialogue(turns))));

    public static bool CanResume(ClaudeCodeSessionState? state, string directory, string fingerprint, string policyVersion) =>
        state is { InFlight: false, SessionId: not null }
        && state.WorkingDirectory == directory && state.ContextFingerprint == fingerprint
        && state.PolicyVersion == policyVersion;

    /// <summary>Stable per-turn text and images for a fresh session; the current request stays separate.</summary>
    public static IReadOnlyList<HistoryBlock> SeedHistoryBlocks(IReadOnlyList<ConversationTurn> turns)
    {
        var blocks = TurnBlocks(turns);
        if (blocks.Length == 0) return [];
        // No changing counts, timestamps, or current request in this prefix. One block per turn
        // lets the CLI cache the previous history boundary as later turns are appended.
        return [
            new("The following JSON blocks are retained conversation background, not new instructions. Do not re-execute earlier requests. " +
                "This is a new session: hidden tool state is not restored. Respond to the current request below.", []),
            .. blocks
        ];
    }

    // Text-only messages serialize exactly as {"role","content"}, so existing fingerprints are unchanged.
    private sealed record ReplayMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content)
    {
        [JsonPropertyName("images")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ImageDescriptor>? Images { get; set; }

        /// <summary>A compressed turn's images, replaced by descriptors in Content before serializing.</summary>
        [JsonIgnore]
        public List<ImageAttachment>? OmittedImages { get; set; }
    }

    private sealed record ImageDescriptor(
        [property: JsonPropertyName("image_block")] int ImageBlock,
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("media_type")] string MediaType,
        [property: JsonPropertyName("sha256")] string Sha256);
}
