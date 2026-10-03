using System.Globalization;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The User Message History dialog: the user messages in Claude's context (the prompts and messages
/// sent during the turn of every turn that isn't hidden), each with a way back to its turn.
/// </summary>
public sealed class UserMessageHistoryDialogViewModel
{
    public UserMessageHistoryDialogViewModel(string conversationTitle, IEnumerable<TurnViewModel> turns)
    {
        ConversationTitle = conversationTitle;
        Messages = turns.Where(t => t.Record is { IsHydrated: true }).SelectMany(Cards).ToArray();
        for (var i = 0; i < Messages.Count - 1; i++) Messages[i].ShowSeparator = true;
    }

    public string ConversationTitle { get; }

    public IReadOnlyList<UserMessageHistoryCardViewModel> Messages { get; }

    public bool HasMessages => Messages.Count > 0;

    public string CountLabel => string.Create(CultureInfo.CurrentCulture,
        $"{Messages.Count:N0} user message{(Messages.Count == 1 ? "" : "s")} in Claude's context");

    // A user text part is the prompt or a message sent during the turn.
    private static IEnumerable<UserMessageHistoryCardViewModel> Cards(TurnViewModel turn) => turn.Record!.Parts
        .Where(p => p is { PartType: TurnParts.Text, Role: "user" } && !string.IsNullOrWhiteSpace(p.Content))
        .OrderBy(p => p.Sequence)
        .Select(p => new UserMessageHistoryCardViewModel(turn, p.Content.Trim()));
}

public sealed class UserMessageHistoryCardViewModel(TurnViewModel turn, string message)
{
    public TurnViewModel Turn { get; } = turn;

    public string Message { get; } = message;

    public bool ShowSeparator { get; internal set; }

    public string Label => string.Create(CultureInfo.CurrentCulture,
        $"Turn #{Turn.Record!.TurnNumber}  ·  ID {Turn.Record.Id}  ·  {Turn.Record.CreatedAt.ToLocalTime():MMM d, yyyy · h:mm tt}");
}
