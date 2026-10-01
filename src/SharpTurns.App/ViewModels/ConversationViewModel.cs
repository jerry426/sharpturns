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
    private const int MaxAttachments = 10;
    private readonly ConversationStore _store;
    private readonly ClaudeTurnRunner _runner;
    private readonly Action<ClaudeCliRateLimitSnapshot> _rateLimitsChanged;
    private CancellationTokenSource? _turnLifetime;
    private ClaudeCliInputQueue? _queue;

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _composerText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(SendLabel))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isAddingImage;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string _selectedModel;

    [ObservableProperty]
    private string _selectedEffort;

    internal ConversationViewModel(Conversation conversation, Project project, ConversationStore store, ClaudeTurnRunner runner,
        Action<ClaudeCliRateLimitSnapshot> rateLimitsChanged)
    {
        Conversation = conversation;
        Project = project;
        _store = store;
        _runner = runner;
        _rateLimitsChanged = rateLimitsChanged;
        _title = conversation.Title;
        _selectedModel = conversation.Model ?? DefaultModel;
        _selectedEffort = conversation.Effort ?? DefaultEffort;
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasAttachments));
        QueuedMessages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasQueuedMessages));
            SendCommand.NotifyCanExecuteChanged();
        };
    }

    public Conversation Conversation { get; private set; }

    /// <summary>Kept current when the project is edited; a running turn keeps the project it started with.</summary>
    public Project Project { get; set; }

    public ObservableCollection<TurnViewModel> Turns { get; } = [];

    /// <summary>Images for the next turn. Messages sent while a turn runs are text only, so these wait for the next turn.</summary>
    public ObservableCollection<ImageAttachmentViewModel> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>Sent while a turn runs; each leaves this list when the CLI accepts it.</summary>
    public ObservableCollection<ClaudeCliUserMessage> QueuedMessages { get; } = [];

    public bool HasQueuedMessages => QueuedMessages.Count > 0;

    public string SendLabel => IsRunning ? "Queue" : "Send";

    /// <summary>CLI model aliases; the default entry uses the CLI's own default.</summary>
    public IReadOnlyList<string> ModelOptions { get; } = [DefaultModel, "opus", "sonnet", "haiku"];

    public IReadOnlyList<string> EffortOptions { get; } = [DefaultEffort, "low", "medium", "high", "xhigh", "max"];

    /// <summary>Shows a question non-modally; returns the answer, or null when declined or the token is canceled.</summary>
    public Func<QuestionDialogViewModel, CancellationToken, Task<string?>>? ShowQuestionAsync { get; set; }

    /// <summary>Tool name and raw JSON input; returns true when the user allows it. Closes when the token is canceled.</summary>
    public Func<string, string, CancellationToken, Task<bool>>? ShowPermissionAsync { get; set; }

    /// <summary>Raised on the UI thread after each turn's content changes, for scrolling.</summary>
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

    /// <summary>Owns its errors: reports a rejected image in the status line.</summary>
    public async Task AddImageAsync(string fileName, Stream source)
    {
        if (Attachments.Count >= MaxAttachments)
        {
            Status = $"A turn can include up to {MaxAttachments} images.";
            return;
        }
        IsAddingImage = true;
        try { Attachments.Add(new ImageAttachmentViewModel(await ImageAttachmentProcessor.ProcessAsync(fileName, source))); }
        catch (Exception e) { Status = $"Couldn't attach {fileName}: {e.Message}"; }
        finally { IsAddingImage = false; }
    }

    [RelayCommand]
    private void RemoveAttachment(ImageAttachmentViewModel attachment) => Attachments.Remove(attachment);

    private bool CanSend() => !string.IsNullOrWhiteSpace(ComposerText) && !IsAddingImage
        && (!IsRunning || QueuedMessages.Count < ClaudeCliInputQueue.Capacity);

    // Concurrent: Send stays available during a turn to queue messages for it.
    [RelayCommand(CanExecute = nameof(CanSend), AllowConcurrentExecutions = true)]
    private async Task SendAsync()
    {
        var prompt = ComposerText.Trim();
        if (IsRunning)
        {
            if (_queue?.TryEnqueue(prompt, out var queued) == true)
            {
                QueuedMessages.Add(queued!);
                ComposerText = "";
            }
            else Status = "The turn is finishing; send this message as the next turn.";
            return;
        }

        var images = Attachments.Select(a => a.Attachment).ToArray();
        ComposerText = "";
        Attachments.Clear();
        var turn = new TurnViewModel(prompt, images);
        Turns.Add(turn);
        TurnContentChanged?.Invoke(this, EventArgs.Empty);
        IsRunning = true;
        Status = "Starting…";
        _turnLifetime = new CancellationTokenSource();
        _queue = new ClaudeCliInputQueue();
        try
        {
            void Show(Action update) => Dispatcher.UIThread.Post(() =>
            {
                update();
                TurnContentChanged?.Invoke(this, EventArgs.Empty);
            });
            var callbacks = new TurnCallbacks(
                delta => Show(() => turn.AppendText(delta)),
                status => Dispatcher.UIThread.Post(() =>
                {
                    if (IsRunning) Status = status;
                }),
                tool => Show(() => turn.UpdateTool(tool)),
                (key, question, waiting) => Show(() => turn.UpdateQuestion(key, question, waiting)),
                message => Show(() =>
                {
                    if (QueuedMessages.FirstOrDefault(m => m.Uuid == message.Uuid) is { } queued) QueuedMessages.Remove(queued);
                    turn.AddUserMessage(message.Text);
                }),
                usage => Dispatcher.UIThread.Post(() => turn.UpdateUsage(usage)),
                limits => Dispatcher.UIThread.Post(() => _rateLimitsChanged(limits)),
                (question, token) => Dispatcher.UIThread.InvokeAsync(() => AskAsync(question, token)),
                (tool, input, token) => Dispatcher.UIThread.InvokeAsync(() => ApproveAsync(tool, input, token)));
            var result = await _runner.RunAsync(Project, Conversation, prompt, images, ClaudeCliCodingPolicy.DefaultSystemPrompt,
                _queue, callbacks, _turnLifetime.Token);
            IsRunning = false;
            turn.Finish(result.Turn);
            QueuedMessages.Clear();
            Status = result.Turn.Status switch
            {
                TurnStatus.Stopped => "Stopped.",
                TurnStatus.Failed => "The turn failed.",
                _ => null,
            };
            if (result.UndeliveredMessages.Count > 0)
            {
                // Nothing the user typed is lost: messages the CLI never accepted go back to the composer.
                ComposerText = string.Join("\n\n", result.UndeliveredMessages.Append(ComposerText).Where(t => !string.IsNullOrWhiteSpace(t)));
                Status = (Status is null ? "" : Status + " ") + "Messages that weren't delivered are back in the composer.";
            }
        }
        catch (Exception e)
        {
            IsRunning = false;
            QueuedMessages.Clear();
            turn.Fail(e.Message);
            Status = "The turn couldn't be saved.";
        }
        finally
        {
            _turnLifetime.Dispose();
            _turnLifetime = null;
            _queue = null;
            TurnContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<string?> AskAsync(ClaudeCliQuestion question, CancellationToken token) =>
        ShowQuestionAsync is null ? null
            : await ShowQuestionAsync(new QuestionDialogViewModel(question, Project.Name, Title), token);

    private async Task<bool> ApproveAsync(string toolName, string input, CancellationToken token) =>
        ShowPermissionAsync is not null && await ShowPermissionAsync(toolName, input, token);

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
