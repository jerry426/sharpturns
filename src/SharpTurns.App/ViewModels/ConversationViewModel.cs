using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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
    private const string DefaultOutputStyle = "Default";
    public const int MaxAttachments = 10;
    private readonly ConversationStore _store;
    private readonly ClaudeTurnRunner _runner;
    private readonly Action<ClaudeCliRateLimitSnapshot> _rateLimitsChanged;
    private readonly Action<Conversation> _conversationUpdated;
    private CancellationTokenSource? _turnLifetime;
    private ClaudeCliInputQueue? _queue;

    [ObservableProperty]
    private string _title;

    private readonly Stopwatch _elapsed = new();
    private DispatcherTimer? _elapsedTimer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearComposerCommand))]
    private string _composerText = "";

    /// <summary>Elapsed time of the latest turn started in this session, as mm:ss; empty until one starts.</summary>
    [ObservableProperty]
    private string _elapsedText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTurnCommand))]
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

    [ObservableProperty]
    private string _selectedOutputStyle;

    /// <summary>conversationUpdated receives the conversation when a turn starts and moves its updated time.</summary>
    internal ConversationViewModel(Conversation conversation, Project project, ConversationStore store, ClaudeTurnRunner runner,
        Action<ClaudeCliRateLimitSnapshot> rateLimitsChanged, Action<Conversation> conversationUpdated)
    {
        Conversation = conversation;
        Project = project;
        _store = store;
        _runner = runner;
        _rateLimitsChanged = rateLimitsChanged;
        _conversationUpdated = conversationUpdated;
        _title = conversation.Title;
        _selectedModel = conversation.Model ?? DefaultModel;
        _selectedEffort = conversation.Effort ?? DefaultEffort;
        _selectedOutputStyle = conversation.OutputStyle ?? DefaultOutputStyle;
        Turns.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TurnCount));
            OnPropertyChanged(nameof(LastTurn));
        };
        Attachments.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(ImageButtonToolTip));
        };
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

    public int TurnCount => Turns.Count;

    public TurnViewModel? LastTurn => Turns.Count > 0 ? Turns[^1] : null;

    /// <summary>Images for the next turn. Messages sent while a turn runs are text only, so these wait for the next turn.</summary>
    public ObservableCollection<ImageAttachmentViewModel> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public string ImageButtonToolTip => HasAttachments
        ? $"Attach more images to the next turn ({Attachments.Count} of {MaxAttachments} attached)"
        : "Attach PNG, JPEG, GIF, WebP, or BMP images to the next turn";

    /// <summary>Sent while a turn runs; each leaves this list when the CLI accepts it.</summary>
    public ObservableCollection<ClaudeCliUserMessage> QueuedMessages { get; } = [];

    public bool HasQueuedMessages => QueuedMessages.Count > 0;

    public string SendLabel => IsRunning ? "Queue" : "Send";

    // Shared by every conversation so the sidebar's pickers keep the same ItemsSource when the conversation changes;
    // a new ItemsSource clears the selection, which would write back to the conversation.
    private static readonly IReadOnlyList<string> SharedModelOptions = [DefaultModel, "opus", "sonnet", "haiku"];
    private static readonly IReadOnlyList<string> SharedEffortOptions = [DefaultEffort, "low", "medium", "high", "xhigh", "max"];
    private static readonly IReadOnlyList<string> SharedOutputStyleOptions = [DefaultOutputStyle, .. ClaudeCliCodingPolicy.OutputStyles];

    /// <summary>CLI model aliases; the default entry uses the CLI's own default.</summary>
    public IReadOnlyList<string> ModelOptions => SharedModelOptions;

    public IReadOnlyList<string> EffortOptions => SharedEffortOptions;

    /// <summary>The CLI's built-in output styles; the default entry leaves the style to the user's CLI settings.</summary>
    public IReadOnlyList<string> OutputStyleOptions => SharedOutputStyleOptions;

    /// <summary>Shows a question non-modally; returns the answer, or null when declined or the token is canceled.</summary>
    public Func<QuestionDialogViewModel, CancellationToken, Task<string?>>? ShowQuestionAsync { get; set; }

    /// <summary>Tool name and raw JSON input; returns true when the user allows it. Closes when the token is canceled.</summary>
    public Func<string, string, CancellationToken, Task<bool>>? ShowPermissionAsync { get; set; }

    /// <summary>Title and message; returns true when confirmed.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public Func<string, Task>? CopyTextAsync { get; set; }

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

    private bool CanClearComposer() => ComposerText.Length > 0;

    [RelayCommand(CanExecute = nameof(CanClearComposer))]
    private void ClearComposer() => ComposerText = "";

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
        StartElapsed();
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
                saved => Dispatcher.UIThread.Post(() =>
                {
                    turn.Started(saved);
                    // Starting the turn moved the conversation's updated time; the sidebar list sorts by it.
                    Conversation = Conversation with { UpdatedAt = saved.CreatedAt };
                    _conversationUpdated(Conversation);
                }),
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
            StopElapsed();
            _turnLifetime.Dispose();
            _turnLifetime = null;
            _queue = null;
            TurnContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void StartElapsed()
    {
        if (_elapsedTimer is null)
        {
            _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _elapsedTimer.Tick += (_, _) => ElapsedText = FormatElapsed(_elapsed.Elapsed);
        }
        _elapsed.Restart();
        ElapsedText = FormatElapsed(TimeSpan.Zero);
        _elapsedTimer.Start();
    }

    // The final time stays shown until the next turn starts.
    private void StopElapsed()
    {
        _elapsed.Stop();
        _elapsedTimer?.Stop();
        ElapsedText = FormatElapsed(_elapsed.Elapsed);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var seconds = Math.Max(0L, (long)elapsed.TotalSeconds);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");
    }

    private async Task<string?> AskAsync(ClaudeCliQuestion question, CancellationToken token) =>
        ShowQuestionAsync is null ? null
            : await ShowQuestionAsync(new QuestionDialogViewModel(question, Project.Name, Title), token);

    private async Task<bool> ApproveAsync(string toolName, string input, CancellationToken token) =>
        ShowPermissionAsync is not null && await ShowPermissionAsync(toolName, input, token);

    [RelayCommand]
    private Task CopyMetricsAsync(TurnViewModel turn) => CopyAsync(turn.FormatMetrics(), "Copied the turn's metrics.");

    [RelayCommand]
    private Task CopyTurnAsync(TurnViewModel turn) => CopyAsync(turn.FormatForClipboard(Conversation.Id), "Copied the turn.");

    private async Task CopyAsync(string text, string copied)
    {
        if (CopyTextAsync is null) return;
        try
        {
            await CopyTextAsync(text);
            Status = copied;
        }
        catch (Exception e) { Status = "Couldn't copy to the clipboard: " + e.Message; }
    }

    // Deleting changes the saved history, so the next turn reseeds a fresh CLI session from what remains.
    private bool CanDeleteTurn(TurnViewModel? turn) => !IsRunning && turn?.Record is not null;

    [RelayCommand(CanExecute = nameof(CanDeleteTurn))]
    private async Task DeleteTurnAsync(TurnViewModel turn)
    {
        var number = turn.Record!.TurnNumber;
        if (ConfirmAsync is null || !await ConfirmAsync($"Delete Turn {number}",
                $"Delete turn {number} and everything saved with it? This can't be undone. "
                + "The next turn starts a new CLI session from the remaining history."))
            return;
        try
        {
            await _store.DeleteTurnAsync(turn.Record.Id);
            Turns.Remove(turn);
            Status = $"Deleted turn {number}.";
        }
        catch (Exception e) { Status = "Couldn't delete the turn: " + e.Message; }
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

    partial void OnSelectedOutputStyleChanged(string value) => _ = SaveOutputStyleAsync();

    // Owns its errors so property-change callers can fire and forget it. A new style reseeds the next turn.
    private async Task SaveOutputStyleAsync()
    {
        var style = SelectedOutputStyle == DefaultOutputStyle ? null : SelectedOutputStyle;
        if (style == Conversation.OutputStyle) return;
        Conversation = Conversation with { OutputStyle = style };
        try { await _store.SetConversationOutputStyleAsync(Conversation.Id, style); }
        catch (Exception e) { Status = "Couldn't save the output style: " + e.Message; }
    }
}
