using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The conversation's images by turn: each image's replay choice,
/// whether the replay sends it now, and a way back to its turn.
/// </summary>
public sealed partial class ImagesBeingReplayedDialogViewModel : ObservableObject
{
    public const string ActiveColor = "#365C48";
    public const string InactiveColor = "#633C40";
    private readonly Func<TurnViewModel, int, bool, Task> _save;

    [ObservableProperty]
    private string _status = "";

    /// <param name="save">Saves an image's choice (turn, part sequence, include); throws when it can't.</param>
    public ImagesBeingReplayedDialogViewModel(string conversationTitle, IEnumerable<TurnViewModel> turns,
        Func<TurnViewModel, int, bool, Task> save)
    {
        ConversationTitle = conversationTitle;
        _save = save;
        Turns = turns.Select(turn => new ReplayImageTurnViewModel(turn)).ToArray();
    }

    public string ConversationTitle { get; }

    public IReadOnlyList<ReplayImageTurnViewModel> Turns { get; }

    public string CountLabel
    {
        get
        {
            var images = Turns.Sum(turn => turn.Images.Count);
            var replayed = Turns.Sum(turn => turn.Images.Count(image => image.IsReplayed));
            return string.Create(CultureInfo.CurrentCulture,
                $"{images:N0} image{(images == 1 ? "" : "s")} across {Turns.Count:N0} turn{(Turns.Count == 1 ? "" : "s")} ({replayed:N0} will be replayed)");
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(ReplayImageViewModel image)
    {
        Status = "";
        try { await _save(image.Owner.Turn, image.Sequence, !image.IncludeInFutureReplay); }
        catch (Exception e) { Status = "Couldn't save the image's replay choice: " + e.Message; }
        finally
        {
            // Also puts a check box back when the save failed.
            image.Owner.Refresh();
            OnPropertyChanged(nameof(CountLabel));
        }
    }
}

/// <summary>A saved turn with images: its number, prompt, and images, green when any is replayed.</summary>
public sealed partial class ReplayImageTurnViewModel : ObservableObject
{
    [ObservableProperty]
    private string _background = ImagesBeingReplayedDialogViewModel.InactiveColor;

    [ObservableProperty]
    private string _note = "";

    public ReplayImageTurnViewModel(TurnViewModel turn)
    {
        Turn = turn;
        Images = turn.Record!.Parts.Where(p => p.PartType == TurnParts.Image).OrderBy(p => p.Sequence)
            .Select(p => new ReplayImageViewModel(this, p.Sequence, new ImageAttachmentViewModel(TurnParts.ReadImage(p))))
            .ToArray();
        Refresh();
    }

    public TurnViewModel Turn { get; }

    public string Label => string.Create(CultureInfo.CurrentCulture, $"Turn #{Turn.Record!.TurnNumber} · {Turn.TimeLabel}");

    public string Message => Turn.UserText;

    public IReadOnlyList<ReplayImageViewModel> Images { get; }

    public void Refresh()
    {
        var record = Turn.Record!;
        foreach (var image in Images) image.Refresh(record);
        Background = Images.Any(image => image.IsReplayed)
            ? ImagesBeingReplayedDialogViewModel.ActiveColor
            : ImagesBeingReplayedDialogViewModel.InactiveColor;
        Note = record switch
        {
            { IsHydrated: false } => "Not replayed: this turn is hidden from Claude's context. Choosing an image doesn't show the turn again.",
            { IsCompressed: false } => "Replayed in full, with all its images. Checked images stay in the replay when the turn is compressed.",
            _ => "Compressed: only checked images are replayed; the others are described by name, type, and size.",
        };
    }
}

public sealed class ReplayImageViewModel(ReplayImageTurnViewModel owner, int sequence, ImageAttachmentViewModel preview)
    : ObservableObject
{
    public ReplayImageTurnViewModel Owner { get; } = owner;

    /// <summary>The image's part sequence in its turn.</summary>
    public int Sequence { get; } = sequence;

    public ImageAttachmentViewModel Preview { get; } = preview;

    public bool IncludeInFutureReplay { get; private set; }

    public bool IsReplayed { get; private set; }

    public string ChoiceLabel { get; private set; } = "";

    public string ChoiceToolTip { get; private set; } = "";

    // Always notifies, so a check box the user toggled shows the saved choice again.
    internal void Refresh(ConversationTurn turn)
    {
        var image = TurnParts.ReadImage(turn.Parts.First(p => p.Sequence == Sequence));
        IncludeInFutureReplay = image.IncludeInFutureReplay;
        IsReplayed = ClaudeCodeContext.IsImageReplayed(turn, image);
        ChoiceLabel = TurnImageViewModel.ChoiceLabel(turn.IsCompressed);
        ChoiceToolTip = TurnImageViewModel.ChoiceToolTip(turn.IsCompressed);
        OnPropertyChanged(nameof(IncludeInFutureReplay));
        OnPropertyChanged(nameof(IsReplayed));
        OnPropertyChanged(nameof(ChoiceLabel));
        OnPropertyChanged(nameof(ChoiceToolTip));
    }
}
