using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.App.Services;
using SharpTurns.ClaudeCli;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.ViewModels;

public sealed partial class ConversationViewModel : ObservableObject
{
    private const string DefaultModel = "Default model";
    private const string DefaultEffort = "Default effort";
    private readonly ConversationStore _store;
    private readonly ClaudeTurnRunner _runner;
    private CancellationTokenSource? _turnLifetime;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _composerText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string _selectedModel;

    [ObservableProperty]
    private string _selectedEffort;

    internal ConversationViewModel(Conversation conversation, Project project, ConversationStore store, ClaudeTurnRunner runner)
    {
        Conversation = conversation;
        Project = project;
        _store = store;
        _runner = runner;
        _title = conversation.Title;
        _selectedModel = conversation.Model ?? DefaultModel;
        _selectedEffort = conversation.Effort ?? DefaultEffort;
    }

    public Conversation Conversation { get; private set; }

    /// <summary>Kept current when the project is edited; a running turn keeps the project it started with.</summary>
    public Project Project { get; set; }

    public ObservableCollection<TurnViewModel> Turns { get; } = [];

    /// <summary>CLI model aliases; the default entry uses the CLI's own default.</summary>
    public IReadOnlyList<string> ModelOptions { get; } = [DefaultModel, "opus", "sonnet", "haiku"];

    public IReadOnlyList<string> EffortOptions { get; } = [DefaultEffort, "low", "medium", "high", "xhigh", "max"];

    /// <summary>Raised on the UI thread after each turn's text changes, for scrolling.</summary>
    public event EventHandler? TurnContentChanged;

    public async Task LoadAsync()
    {
        try
        {
            var turns = await _store.LoadTurnsAsync(Conversation.Id);
            Turns.Clear();
            foreach (var turn in turns) Turns.Add(new TurnViewModel(turn));
            TurnContentChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) { Status = "Couldn't load this conversation: " + e.Message; }
    }

    public void ApplyRename(Conversation conversation)
    {
        Conversation = Conversation with { Title = conversation.Title };
        Title = conversation.Title;
    }

    public void CancelTurn() => _turnLifetime?.Cancel();

    private bool CanSend() => !IsRunning && !string.IsNullOrWhiteSpace(ComposerText);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var prompt = ComposerText.Trim();
        ComposerText = "";
        var turn = new TurnViewModel(prompt);
        Turns.Add(turn);
        TurnContentChanged?.Invoke(this, EventArgs.Empty);
        IsRunning = true;
        Status = "Starting…";
        _turnLifetime = new CancellationTokenSource();
        try
        {
            var callbacks = new TurnCallbacks(
                delta => Dispatcher.UIThread.Post(() =>
                {
                    turn.AppendText(delta);
                    TurnContentChanged?.Invoke(this, EventArgs.Empty);
                }),
                status => Dispatcher.UIThread.Post(() =>
                {
                    if (IsRunning) Status = status;
                }));
            var saved = await _runner.RunAsync(Project, Conversation, prompt, ClaudeCliCodingPolicy.DefaultSystemPrompt,
                callbacks, _turnLifetime.Token);
            IsRunning = false;
            turn.Apply(saved);
            Status = saved.Status switch
            {
                TurnStatus.Stopped => "Stopped.",
                TurnStatus.Failed => "The turn failed.",
                _ => null,
            };
        }
        catch (Exception e)
        {
            IsRunning = false;
            turn.Fail(e.Message);
            Status = "The turn couldn't be saved.";
        }
        finally
        {
            _turnLifetime.Dispose();
            _turnLifetime = null;
            TurnContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        Status = "Stopping…";
        CancelTurn();
    }

    partial void OnSelectedModelChanged(string value) => _ = SaveModelAsync();

    partial void OnSelectedEffortChanged(string value) => _ = SaveModelAsync();

    // Owns its errors so property-change callers can fire and forget it.
    private async Task SaveModelAsync()
    {
        var model = SelectedModel == DefaultModel ? null : SelectedModel;
        var effort = SelectedEffort == DefaultEffort ? null : SelectedEffort;
        if (model == Conversation.Model && effort == Conversation.Effort) return;
        Conversation = Conversation with { Model = model, Effort = effort };
        try { await _store.SetConversationModelAsync(Conversation.Id, model, effort); }
        catch (Exception e) { Status = "Couldn't save the model choice: " + e.Message; }
    }
}
