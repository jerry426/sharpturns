using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SharpTurns.App.ViewModels;

// The Context Management tab: Smart Cleanup, the turn list's selection, and the
// bulk Show, Hide, and Delete. Like the turn card actions, each changes the replayed history, so they wait for a running
// turn or compression, and the next turn starts a new CLI session.
public sealed partial class ConversationViewModel
{
    private static readonly IReadOnlyList<string> SharedSmartCleanupOptions =
        ["Keep Recent 10 Turns", "Keep Recent 15 Turns", "Keep Recent 20 Turns", "Keep Recent 30 Turns"];
    private static readonly int[] SmartCleanupTurnCounts = [10, 15, 20, 30];
    private const int MaxListedTurns = 12;

    // The turn a Shift-click selects the range from.
    private TurnViewModel? _selectionAnchor;

    /// <summary>Keep Recent 15 Turns by default.</summary>
    [ObservableProperty]
    private int _smartCleanupIndex = 1;

    public IReadOnlyList<string> SmartCleanupOptions => SharedSmartCleanupOptions;

    public string ContextStatusLabel => string.Create(CultureInfo.CurrentCulture,
        $"{SavedTurns().Count():N0} stored turns · {SavedTurns().Count(t => t.IsHydrated):N0} replayed when a new CLI session starts");

    public string ContextTurnsHeaderLabel => string.Create(CultureInfo.CurrentCulture, $"Turns ({Turns.Count:N0} total)");

    private int SelectedTurnCount => Turns.Count(t => t.IsSelected);

    public string SelectedTurnsLabel => string.Create(CultureInfo.CurrentCulture, $"{SelectedTurnCount:N0} selected");

    public string ShowSelectedTurnsLabel => string.Create(CultureInfo.CurrentCulture, $"Show ({SelectedTurnCount:N0})");

    public string HideSelectedTurnsLabel => string.Create(CultureInfo.CurrentCulture, $"Hide ({SelectedTurnCount:N0})");

    public string DeleteSelectedTurnsLabel => string.Create(CultureInfo.CurrentCulture, $"Delete ({SelectedTurnCount:N0})");

    public string BranchRangeLabel => string.Create(CultureInfo.CurrentCulture, $"Branch Range ({SelectedTurnCount:N0})");

    public string BranchThroughLabel => string.Create(CultureInfo.CurrentCulture, $"Branch Through ({SelectedTurnCount:N0})");

    private IEnumerable<TurnViewModel> SavedTurns() => Turns.Where(t => t.Record is not null);

    private TurnViewModel[] SelectedTurns() =>
        Turns.Where(t => t.IsSelected && t.Record is not null).OrderBy(t => t.Record!.TurnNumber).ToArray();

    /// <summary>
    /// A click on a row's check box. Shift selects every listed turn between the last one clicked and this one;
    /// otherwise it toggles this turn.
    /// </summary>
    public void SelectContextTurn(TurnViewModel turn, bool range)
    {
        if (turn.Record is null) return;
        var anchor = _selectionAnchor is { } a ? ShownTurns.IndexOf(a) : -1;
        var target = ShownTurns.IndexOf(turn);
        if (!range || anchor < 0 || target < 0)
        {
            turn.IsSelected = !turn.IsSelected;
            _selectionAnchor = turn.IsSelected ? turn : null;
            return;
        }
        for (var i = Math.Min(anchor, target); i <= Math.Max(anchor, target); i++)
        {
            if (ShownTurns[i].Record is not null) ShownTurns[i].IsSelected = true;
        }
    }

    [RelayCommand]
    private void SelectAllContextTurns()
    {
        var listed = ShownTurns.Where(t => t.Record is not null).ToArray();
        foreach (var turn in listed) turn.IsSelected = true;
        _selectionAnchor = listed.FirstOrDefault();
    }

    private bool HasSelectedTurns() => SelectedTurnCount > 0;

    [RelayCommand(CanExecute = nameof(HasSelectedTurns))]
    private void ClearContextTurnSelection()
    {
        foreach (var turn in Turns) turn.IsSelected = false;
        _selectionAnchor = null;
    }

    private bool CanChangeSelectedTurns() => !IsRunning && !IsCompressing && HasSelectedTurns();

    [RelayCommand(CanExecute = nameof(CanChangeSelectedTurns))]
    private Task ShowSelectedTurnsAsync() => SetSelectedTurnsHydratedAsync(true);

    [RelayCommand(CanExecute = nameof(CanChangeSelectedTurns))]
    private Task HideSelectedTurnsAsync() => SetSelectedTurnsHydratedAsync(false);

    private async Task SetSelectedTurnsHydratedAsync(bool isHydrated)
    {
        var changed = SelectedTurns().Where(t => t.IsHydrated != isHydrated).ToArray();
        if (changed.Length == 0)
        {
            Status = isHydrated ? "The selected turns are already in Claude's context." : "The selected turns are already hidden.";
            return;
        }
        try
        {
            await _store.SetTurnsHydratedAsync(changed.Select(t => (t.Record!.Id, isHydrated)).ToArray());
            foreach (var turn in changed) turn.ApplyRecord(turn.Record! with { IsHydrated = isHydrated });
            NotifyContextChanged();
            Status = (isHydrated ? $"Put {TurnsLabel(changed)} back in Claude's context." : $"Hid {TurnsLabel(changed)} from Claude's context.")
                + " The next turn starts a new CLI session.";
        }
        catch (Exception e) { Status = $"Couldn't change the selected turns: {e.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanChangeSelectedTurns))]
    private async Task DeleteSelectedTurnsAsync()
    {
        var selected = SelectedTurns();
        var count = selected.Length.ToString("N0", CultureInfo.CurrentCulture);
        if (ConfirmAsync is null || !await ConfirmAsync(selected.Length == 1 ? "Delete 1 Turn" : $"Delete {count} Turns",
                $"Delete {TurnsLabel(selected)} and everything saved with {(selected.Length == 1 ? "it" : "them")}? "
                + "This can't be undone. The next turn starts a new CLI session from the remaining history."))
            return;
        try
        {
            await _store.DeleteTurnsAsync(selected.Select(t => t.Record!.Id).ToArray());
            foreach (var turn in selected) Turns.Remove(turn);
            Status = $"Deleted {TurnsLabel(selected)}.";
        }
        catch (Exception e) { Status = "Couldn't delete the selected turns: " + e.Message; }
    }

    private bool CanKeepRecentTurns() => !IsRunning && !IsCompressing && SavedTurns().Any();

    /// <summary>Shows the most recent turns and hides every older one.</summary>
    [RelayCommand(CanExecute = nameof(CanKeepRecentTurns))]
    private async Task KeepRecentTurnsAsync()
    {
        var count = SmartCleanupTurnCounts[Math.Clamp(SmartCleanupIndex, 0, SmartCleanupTurnCounts.Length - 1)];
        var recent = SavedTurns().OrderByDescending(t => t.Record!.TurnNumber).ThenByDescending(t => t.Record!.Id).Take(count).ToHashSet();
        var changed = SavedTurns().Where(t => t.IsHydrated != recent.Contains(t)).ToArray();
        if (changed.Length == 0)
        {
            Status = $"Nothing to change: Claude's context already holds only the {count} most recent turns.";
            return;
        }
        try
        {
            await _store.SetTurnsHydratedAsync(changed.Select(t => (t.Record!.Id, recent.Contains(t))).ToArray());
            foreach (var turn in changed) turn.ApplyRecord(turn.Record! with { IsHydrated = recent.Contains(turn) });
            NotifyContextChanged();
            var hidden = changed.Count(t => !t.IsHydrated);
            Status = string.Create(CultureInfo.CurrentCulture,
                $"Kept the {count} most recent turns: hid {hidden:N0}, showed {changed.Length - hidden:N0}. The next turn starts a new CLI session.");
        }
        catch (Exception e) { Status = "Couldn't apply Smart Cleanup: " + e.Message; }
    }

    // "turn 4" or "turns 3, 4, 5", listing at most a dozen numbers.
    private static string TurnsLabel(IReadOnlyList<TurnViewModel> turns) =>
        (turns.Count == 1 ? "turn " : "turns ") + LimitedList(turns.Select(t => t.Record!.TurnNumber.ToString(CultureInfo.InvariantCulture)));

    private static string LimitedList(IEnumerable<string> values)
    {
        var items = values.ToArray();
        var listed = string.Join(", ", items.Take(MaxListedTurns));
        return items.Length <= MaxListedTurns
            ? listed
            : string.Create(CultureInfo.CurrentCulture, $"{listed}, … +{items.Length - MaxListedTurns:N0} more");
    }

    private void OnTurnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TurnViewModel.IsSelected)) NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTurnsLabel));
        OnPropertyChanged(nameof(ShowSelectedTurnsLabel));
        OnPropertyChanged(nameof(HideSelectedTurnsLabel));
        OnPropertyChanged(nameof(DeleteSelectedTurnsLabel));
        OnPropertyChanged(nameof(BranchRangeLabel));
        OnPropertyChanged(nameof(BranchThroughLabel));
        ClearContextTurnSelectionCommand.NotifyCanExecuteChanged();
        ShowSelectedTurnsCommand.NotifyCanExecuteChanged();
        HideSelectedTurnsCommand.NotifyCanExecuteChanged();
        DeleteSelectedTurnsCommand.NotifyCanExecuteChanged();
        BranchSelectedRangeCommand.NotifyCanExecuteChanged();
        BranchThroughSelectedTurnCommand.NotifyCanExecuteChanged();
    }

    // After the turns or their context state change. A turn the Show picker leaves out is deselected, so a bulk action
    // never touches a turn the list doesn't show.
    private void RefreshShownTurns()
    {
        SyncShownTurns(ShownTurns, Turns.Where(Display.Shows));
        foreach (var turn in Turns.Where(t => t.IsSelected && !ShownTurns.Contains(t))) turn.IsSelected = false;
        if (_selectionAnchor is { } anchor && !ShownTurns.Contains(anchor)) _selectionAnchor = null;
        OnPropertyChanged(nameof(ContextStatusLabel));
        OnPropertyChanged(nameof(ContextTurnsHeaderLabel));
        KeepRecentTurnsCommand.NotifyCanExecuteChanged();
        NotifySelectionChanged();
    }
}
