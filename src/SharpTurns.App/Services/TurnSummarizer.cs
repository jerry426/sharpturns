using System.Globalization;
using SharpTurns.ClaudeCli;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.Services;

/// <summary>
/// Compresses turns with a disposable, tool-less one-shot CLI call that never touches the conversation's session.
/// The summarizer model is the "summarizer_model" setting (chosen in Config → Preferences), Sonnet 5.5 by default.
/// executable returns the CLI to launch, as for <see cref="ClaudeTurnRunner"/>.
/// </summary>
internal sealed class TurnSummarizer(ConversationStore store, Func<string?> executable)
{
    public const string ModelSetting = "summarizer_model";
    public const string DefaultModel = "claude-sonnet-5-5";
    private const int OutputTokenCap = 16_384;

    public TurnSummarizer(ConversationStore store, string? executable = null) : this(store, () => executable) { }

    public async Task<string> GetModelAsync(CancellationToken cancellationToken = default) =>
        await store.GetSettingAsync(ModelSetting, cancellationToken) is { Length: > 0 } model ? model : DefaultModel;

    /// <summary>
    /// Saves and returns the compressed turn. Throws InvalidOperationException with a message for the user when the
    /// turn can't be compressed, the summary is unusable, or compressing wouldn't make the turn smaller.
    /// </summary>
    public async Task<ConversationTurn> CompressAsync(ConversationTurn turn, CancellationToken cancellationToken = default)
    {
        var source = TurnCompression.Analyze(turn, out var reason)
            ?? throw new InvalidOperationException(reason);
        string? model = null;
        var workSummary = TurnCompression.NoWorkSummary;
        // A turn with no tool calls or intermediate text has nothing for a model to summarize.
        if (source.HasWork)
        {
            model = await GetModelAsync(cancellationToken).ConfigureAwait(false);
            var generated = await new ClaudeCliClient(executable()).RunOneShotAsync(TurnCompression.SystemPrompt,
                TurnCompression.Instruction(turn.TurnNumber), [new(source.SummarizerSource)], model, null, OutputTokenCap,
                cancellationToken).ConfigureAwait(false);
            workSummary = TurnCompression.NormalizeWorkSummary(generated);
        }
        var compressed = turn with { Summary = TurnCompression.Compose(workSummary, source), SummaryModel = model };
        var full = TurnCompression.FullContentBytes(turn);
        var replay = ClaudeCodeContext.ReplayBytes(compressed);
        if (replay >= full)
            throw new InvalidOperationException(string.Create(CultureInfo.CurrentCulture,
                $"Compressing it wouldn't make it smaller ({full:N0} bytes saved, {replay:N0} bytes compressed)."));
        await store.SetTurnSummaryAsync(turn.Id, compressed.Summary, model, cancellationToken).ConfigureAwait(false);
        return compressed;
    }
}
