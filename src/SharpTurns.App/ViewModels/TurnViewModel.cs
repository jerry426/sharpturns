using CommunityToolkit.Mvvm.ComponentModel;
using SharpTurns.Core;

namespace SharpTurns.App.ViewModels;

public sealed partial class TurnViewModel : ObservableObject
{
    [ObservableProperty]
    private string _assistantText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome))]
    private string? _outcome;

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>A turn that is starting; its response streams in through AppendText.</summary>
    public TurnViewModel(string userText)
    {
        UserText = userText;
        IsRunning = true;
    }

    public TurnViewModel(ConversationTurn turn)
    {
        UserText = Text(turn, "user");
        Apply(turn);
    }

    public string UserText { get; }

    public bool HasOutcome => Outcome is not null;

    public void AppendText(string delta) => AssistantText += delta;

    /// <summary>Replaces the streamed display with the saved turn.</summary>
    public void Apply(ConversationTurn turn)
    {
        AssistantText = Text(turn, "assistant");
        IsRunning = turn.Status == TurnStatus.Running;
        Outcome = turn.Status switch
        {
            TurnStatus.Stopped => "Stopped.",
            TurnStatus.Failed => "Failed: " + (turn.ErrorMessage ?? "unknown error."),
            _ => null,
        };
    }

    public void Fail(string message)
    {
        IsRunning = false;
        Outcome = "Failed: " + message;
    }

    private static string Text(ConversationTurn turn, string role) =>
        string.Join("\n\n", turn.Parts.Where(p => p.Role == role && p.PartType == "text").Select(p => p.Content));
}
