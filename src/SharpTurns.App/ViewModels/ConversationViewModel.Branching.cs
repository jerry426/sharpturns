using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>The text of a branch confirmation, laid out as in the Workbench's dialog.</summary>
public sealed record BranchConfirmation(string Title, string Message, string DetailTitle, string DetailText, string WarningText);

// Branching, as in the Workbench: Select as Branch End Point on two turn cards, and the Context Management tab's Branch
// Range and Branch Through, copy a run of turns into a new conversation after confirming; the source is unchanged. They
// wait for a running turn or compression, so a copied turn is never still changing.
public sealed partial class ConversationViewModel
{
    // The second endpoint opens the confirmation, and both are cleared after it either way.
    private readonly List<TurnViewModel> _branchEndpoints = [];

    /// <summary>Returns true when the user confirms creating the branch.</summary>
    public Func<BranchConfirmation, Task<bool>>? ConfirmBranchAsync { get; set; }

    private bool CanToggleBranchEndpoint(TurnViewModel? turn) =>
        !IsRunning && !IsCompressing && turn?.Record is { Status: not TurnStatus.Running };

    [RelayCommand(CanExecute = nameof(CanToggleBranchEndpoint))]
    private async Task ToggleBranchEndpointAsync(TurnViewModel turn)
    {
        if (turn.IsBranchEndpoint)
        {
            turn.IsBranchEndpoint = false;
            _branchEndpoints.Remove(turn);
            Status = "Branch endpoint cleared.";
            return;
        }
        turn.IsBranchEndpoint = true;
        _branchEndpoints.Add(turn);
        if (_branchEndpoints.Count < 2)
        {
            Status = "Branch endpoint 1 of 2 selected. Select a second endpoint to define the range.";
            return;
        }
        // As in the Workbench, the range includes the turns between that the Show picker leaves out.
        var first = _branchEndpoints.Min(t => t.Record!.TurnNumber);
        var last = _branchEndpoints.Max(t => t.Record!.TurnNumber);
        var range = SavedTurnsInOrder().Where(t => t.Record!.TurnNumber >= first && t.Record.TurnNumber <= last).ToArray();
        try
        {
            await BranchAsync(range, new("Confirm Branch Range",
                "Create a new branched conversation from the selected endpoint range?",
                "Included turns",
                string.Create(CultureInfo.CurrentCulture, $"Turn {first} → Turn {last} · {range.Length:N0} turn(s) included"),
                "Every turn between the two endpoints (inclusive) is copied into the new conversation. Copied turns are "
                + "renumbered starting at 1. The source conversation is not modified."));
        }
        finally { ClearBranchEndpoints(); }
    }

    private bool CanBranchSelectedRange() => !IsRunning && !IsCompressing && HasSelectedTurns();

    [RelayCommand(CanExecute = nameof(CanBranchSelectedRange))]
    private async Task BranchSelectedRangeAsync()
    {
        var selected = SelectedTurns();
        var first = selected[0].Record!.TurnNumber;
        var last = selected[^1].Record!.TurnNumber;
        // As in the Workbench, the selection must be contiguous, counting turns the Show picker leaves out.
        if (SavedTurnsInOrder().FirstOrDefault(t => t.Record!.TurnNumber > first && t.Record.TurnNumber < last && !t.IsSelected)
            is { } missing)
        {
            Status = $"Branch Range copies every turn from turn {first} to turn {last}, so select turn {missing.Record!.TurnNumber} too."
                + (ShownTurns.Contains(missing) ? "" : " Set Show to All Turns to list it.");
            return;
        }
        if (await BranchAsync(selected, new(
                string.Create(CultureInfo.CurrentCulture, $"Create a branched conversation from {selected.Length:N0} selected turn(s)?"),
                "The selected turns are copied into a new conversation. The source conversation is not modified.",
                "Selected turns",
                TurnsAndIdsLabel(selected),
                "The selected turns must form a contiguous range ordered by turn number. Copied turns are renumbered starting "
                + "at 1 in the new conversation.")))
            ClearContextTurnSelection();
    }

    // The Workbench enables this for any selection and then asks for exactly one turn; here it waits for one.
    private bool CanBranchThroughSelectedTurn() => !IsRunning && !IsCompressing && SelectedTurnCount == 1;

    [RelayCommand(CanExecute = nameof(CanBranchThroughSelectedTurn))]
    private async Task BranchThroughSelectedTurnAsync()
    {
        var end = SelectedTurns()[0];
        var range = SavedTurnsInOrder().Where(t => t.Record!.TurnNumber <= end.Record!.TurnNumber).ToArray();
        if (await BranchAsync(range, new("Create a branched conversation through the selected turn?",
                "Every turn from the start of this conversation up to and including the selected turn is copied into a new "
                + "conversation. The source conversation is not modified.",
                "Branch endpoint",
                TurnsAndIdsLabel([end]),
                "Copied turns are renumbered starting at 1 in the new conversation.")))
            ClearContextTurnSelection();
    }

    // Owns its errors: the outcome goes to the status line. Returns true when the branch was created.
    private async Task<bool> BranchAsync(TurnViewModel[] turns, BranchConfirmation confirmation)
    {
        if (ConfirmBranchAsync is null || !await ConfirmBranchAsync(confirmation))
        {
            Status = "Branch canceled.";
            return false;
        }
        var copied = RangeLabel(turns);
        try
        {
            var branch = await _store.BranchConversationAsync(Conversation.Id, turns.Select(t => t.Record!.Id).ToArray());
            Status = $"Branched {copied} into a new conversation.";
            _conversationBranched(branch, $"Branched from {copied} (now "
                + (turns.Length == 1 ? "turn 1" : $"1–{turns.Length}") + "). The first turn starts a new CLI session.");
            return true;
        }
        catch (Exception e)
        {
            Status = "Couldn't create the branch: " + e.Message;
            return false;
        }
    }

    private void ClearBranchEndpoints()
    {
        foreach (var turn in _branchEndpoints) turn.IsBranchEndpoint = false;
        _branchEndpoints.Clear();
    }

    private IEnumerable<TurnViewModel> SavedTurnsInOrder() => SavedTurns().OrderBy(t => t.Record!.TurnNumber);

    // "turn 4" or "turns 3–5"; the turns are a contiguous run.
    private static string RangeLabel(TurnViewModel[] turns) => turns.Length == 1
        ? $"turn {turns[0].Record!.TurnNumber}"
        : $"turns {turns[0].Record!.TurnNumber}–{turns[^1].Record!.TurnNumber}";

    // "Turn 4 · id 17" or "Turns 3, 4, 5 · ids 12, 13, 14", as in the Workbench.
    private static string TurnsAndIdsLabel(IReadOnlyList<TurnViewModel> turns)
    {
        var numbers = LimitedList(turns.Select(t => t.Record!.TurnNumber.ToString(CultureInfo.InvariantCulture)));
        var ids = LimitedList(turns.Select(t => t.Record!.Id.ToString(CultureInfo.InvariantCulture)));
        return turns.Count == 1 ? $"Turn {numbers} · id {ids}" : $"Turns {numbers} · ids {ids}";
    }
}
