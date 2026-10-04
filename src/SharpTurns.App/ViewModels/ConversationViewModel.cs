using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
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
    internal const string DefaultEffort = "Default effort";
    private const string DefaultOutputStyle = "Default";
    public const int MaxAttachments = 10;
    private readonly ConversationStore _store;
    private readonly ClaudeTurnRunner _runner;
    private readonly TurnSummarizer _summarizer;
    private readonly Action<ClaudeCliRateLimitSnapshot> _rateLimitsChanged;
    private readonly Action<Conversation> _conversationUpdated;
    private readonly Action<Conversation, string> _conversationBranched;
    private CancellationTokenSource? _turnLifetime;
    private ClaudeCliInputQueue? _queue;
    private Project _project;

    [ObservableProperty]
    private string _title;

    private readonly Stopwatch _elapsed = new();
    private DispatcherTimer? _elapsedTimer;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearComposerCommand))]
    private string _composerText = "";

    /// <summary>Where dictation inserts its transcript.</summary>
    [ObservableProperty]
    private int _composerCaretIndex;

    /// <summary>Elapsed time of the latest turn started in this session, as mm:ss; empty until one starts.</summary>
    [ObservableProperty]
    private string _elapsedText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTurnCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleHydrationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCompressionCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenReplayImagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleImageReplayCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenUserMessageHistoryCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportConversationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExportTurnCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSelectedTurnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(HideSelectedTurnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedTurnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(KeepRecentTurnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleBranchEndpointCommand))]
    [NotifyCanExecuteChangedFor(nameof(BranchSelectedRangeCommand))]
    [NotifyCanExecuteChangedFor(nameof(BranchThroughSelectedTurnCommand))]
    [NotifyPropertyChangedFor(nameof(SendLabel))]
    private bool _isRunning;

    /// <summary>A turn's summary is being generated; one at a time. Sending waits, since the summary changes the history.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTurnCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleHydrationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleCompressionCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenReplayImagesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleImageReplayCommand))]
    [NotifyCanExecuteChangedFor(nameof(ShowSelectedTurnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(HideSelectedTurnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedTurnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(KeepRecentTurnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleBranchEndpointCommand))]
    [NotifyCanExecuteChangedFor(nameof(BranchSelectedRangeCommand))]
    [NotifyCanExecuteChangedFor(nameof(BranchThroughSelectedTurnCommand))]
    private bool _isCompressing;

    // A completed turn's Auto-Summarize is about to start.
    private bool _summarizeNext;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isAddingImage;

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private string? _selectedModel;

    [ObservableProperty]
    private string _selectedEffort;

    [ObservableProperty]
    private string _selectedOutputStyle;

    /// <summary>Compresses each turn that completes, before the next can be sent.</summary>
    [ObservableProperty]
    private bool _autoSummarize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotesLabel))]
    [NotifyPropertyChangedFor(nameof(NotesToolTip))]
    private int _notesCount;

    /// <summary>
    /// conversationUpdated receives the conversation when a turn starts and moves its updated time. conversationBranched
    /// receives a new branch of this conversation and the status line to show in it. Every conversation shares dictation's
    /// service.
    /// </summary>
    internal ConversationViewModel(Conversation conversation, Project project, ConversationStore store, ClaudeTurnRunner runner,
        TurnSummarizer summarizer, ConversationDisplayViewModel display, ApplicationPreferencesViewModel preferences,
        DeepgramDictationService dictationService, Action<ClaudeCliRateLimitSnapshot> rateLimitsChanged,
        Action<Conversation> conversationUpdated, Action<Conversation, string> conversationBranched)
    {
        Conversation = conversation;
        _project = project;
        _store = store;
        _runner = runner;
        _summarizer = summarizer;
        Display = display;
        Preferences = preferences;
        _rateLimitsChanged = rateLimitsChanged;
        _conversationUpdated = conversationUpdated;
        _conversationBranched = conversationBranched;
        _title = conversation.Title;
        _selectedModel = conversation.Model;
        _selectedEffort = conversation.Effort ?? DefaultEffort;
        _selectedOutputStyle = conversation.OutputStyle ?? DefaultOutputStyle;
        _autoSummarize = conversation.AutoSummarize;
        Dictation = new DictationViewModel(dictationService, () => ComposerText, value => ComposerText = value,
            () => ComposerCaretIndex, value => ComposerCaretIndex = value);
        Dictation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(DictationViewModel.IsBusy)) return;
            SendCommand.NotifyCanExecuteChanged();
            ToggleBranchEndpointCommand.NotifyCanExecuteChanged();
            BranchSelectedRangeCommand.NotifyCanExecuteChanged();
            BranchThroughSelectedTurnCommand.NotifyCanExecuteChanged();
        };
        Turns.CollectionChanged += (_, e) =>
        {
            // Removed turns are gone for good, so only new ones need the selection hook; a deleted endpoint is dropped.
            foreach (var turn in e.NewItems?.OfType<TurnViewModel>() ?? []) turn.PropertyChanged += OnTurnPropertyChanged;
            foreach (var turn in e.OldItems?.OfType<TurnViewModel>() ?? []) _branchEndpoints.Remove(turn);
            OnPropertyChanged(nameof(TurnCount));
            OnPropertyChanged(nameof(LastTurn));
            NotifyContextChanged();
        };
        // The display outlives every conversation, so this subscription needs no removal.
        display.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationDisplayViewModel.TurnFilterIndex)) RefreshShownTurns();
            else if (e.PropertyName == nameof(ConversationDisplayViewModel.IsWorkSummaryExpanded))
            {
                foreach (var turn in Turns) turn.IsWorkSummaryExpanded = display.IsWorkSummaryExpanded;
            }
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

    /// <summary>
    /// Kept current when the project is edited or the conversation moves to another; a running turn keeps the project it
    /// started with.
    /// </summary>
    public Project Project
    {
        get => _project;
        set
        {
            if (SetProperty(ref _project, value)) OnPropertyChanged(nameof(Workspace));
        }
    }

    /// <summary>The folder turns run in: the conversation's workspace, or else the project's working directory.</summary>
    public string Workspace => Conversation.WorkspaceFor(Project);

    public bool IsProtected => Conversation.IsProtected;

    /// <summary>The sidebar's Conversation Configuration card: the enabled context files, in the order they're sent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextFiles), nameof(ContextFilesHeaderLabel))]
    private IReadOnlyList<ContextFileSummaryViewModel> _contextFiles = [];

    public bool HasContextFiles => ContextFiles.Count > 0;

    public string ContextFilesHeaderLabel => ContextFiles.Count == 1
        ? "Additional Context File:"
        : string.Create(CultureInfo.CurrentCulture, $"Additional Context Files ({ContextFiles.Count:N0}):");

    /// <summary>The selected MCP servers' names; a disabled one is marked, since it isn't started.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMcpServers), nameof(McpServersHeaderLabel), nameof(McpServersLabel))]
    private IReadOnlyList<string> _mcpServers = [];

    public bool HasMcpServers => McpServers.Count > 0;

    public string McpServersHeaderLabel => McpServers.Count <= 1
        ? "MCP Servers:"
        : string.Create(CultureInfo.CurrentCulture, $"MCP Servers ({McpServers.Count:N0}):");

    public string McpServersLabel => string.Join(", ", McpServers);

    public ConversationDisplayViewModel Display { get; }

    /// <summary>The composer's Voice, confidence, and Undo.</summary>
    public DictationViewModel Dictation { get; }

    /// <summary>
    /// Shared by every conversation: the system prompt, the Notes and Markdown Viewer window sizes, and the DOCX export
    /// defaults.
    /// </summary>
    public ApplicationPreferencesViewModel Preferences { get; }

    public ObservableCollection<TurnViewModel> Turns { get; } = [];

    /// <summary>The turns the Show picker includes, in order.</summary>
    public ObservableCollection<TurnViewModel> ShownTurns { get; } = [];

    public int TurnCount => Turns.Count;

    public TurnViewModel? LastTurn => Turns.Count > 0 ? Turns[^1] : null;

    // The metrics row. A live turn counts as visible and uncompressed.
    public string LastReportedInputLabel => LastTurn?.Usage?.ContextTokens is { } tokens ? Count(tokens) : "—";

    public string TurnsTotalLabel => Count(Turns.Count);

    public string TurnsVisibleLabel => Count(Turns.Count(t => t.IsHydrated));

    public string TurnsCompressedLabel => Count(Turns.Count(t => t.IsCompressed));

    public string TurnsHiddenLabel => Count(Turns.Count(t => !t.IsHydrated));

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

    /// <summary>
    /// No turn, Auto-Summarize, or compression is running, so the saved conversation won't change until the user acts.
    /// Read when IsRunning or IsCompressing changes.
    /// </summary>
    public bool IsIdle => !IsRunning && !IsCompressing && !_summarizeNext;

    // The header's Notes button.
    public string NotesLabel => NotesCount > 0 ? string.Create(CultureInfo.CurrentCulture, $"📝 Notes ({NotesCount:N0})") : "📝 Notes";

    public string NotesToolTip => NotesCount > 0
        ? string.Create(CultureInfo.CurrentCulture, $"View this conversation's notes ({NotesCount:N0} note{(NotesCount == 1 ? "" : "s")})")
        : "View this conversation's notes";

    // The composer's replay image button. A live turn's images count as replayed.
    private int ReplayedImageCount => Turns.Sum(t => t.Record is { } turn ? ClaudeCodeContext.ReplayedImageCount(turn) : t.Images.Count);

    public bool HasConversationImages => Turns.Any(t => t.HasImages);

    public bool HasImagesBeingReplayed => ReplayedImageCount > 0;

    public string ReplayImagesLabel => ReplayedImageCount is > 0 and var count
        ? string.Create(CultureInfo.CurrentCulture, $"Images Being Replayed ({count:N0})")
        : "Images Available for Replay";

    // Shared by every conversation so the sidebar's pickers keep the same ItemsSource when the conversation changes;
    // a new ItemsSource clears the selection, which would write back to the conversation.
    // Preferences' defaults for new conversations use the same lists; the model list is the Config tab's Models.
    internal static readonly IReadOnlyList<string> SharedEffortOptions = [DefaultEffort, "low", "medium", "high", "xhigh", "max"];
    private static readonly IReadOnlyList<string> SharedOutputStyleOptions = [DefaultOutputStyle, .. ClaudeCliCodingPolicy.OutputStyles];

    /// <summary>The Config tab's model IDs.</summary>
    public IReadOnlyList<string> ModelOptions => Preferences.ModelOptions;

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

    /// <summary>Shows the conversation's images modally.</summary>
    public Func<ImagesBeingReplayedDialogViewModel, Task>? ShowReplayImagesAsync { get; set; }

    /// <summary>Shows the user message history modally.</summary>
    public Func<UserMessageHistoryDialogViewModel, Task>? ShowUserMessageHistoryAsync { get; set; }

    /// <summary>Shows the notes non-modally, or brings this conversation's open Notes window forward.</summary>
    public Action<NotesDialogViewModel>? ShowNotes { get; set; }

    /// <summary>Shows the export options modally.</summary>
    public Func<TurnExportDialogViewModel, Task>? ShowExportAsync { get; set; }

    /// <summary>The center of the screen showing the conversation, so the Markdown Viewer opens on that monitor.</summary>
    public Func<PixelPoint?>? GetScreenPoint { get; set; }

    /// <summary>Raised on the UI thread after each turn's content changes, for scrolling.</summary>
    public event EventHandler? TurnContentChanged;

    public async Task LoadAsync()
    {
        try
        {
            var turns = await _store.LoadTurnsAsync(Conversation.Id);
            Turns.Clear();
            foreach (var turn in turns) Turns.Add(new TurnViewModel(turn) { IsWorkSummaryExpanded = Display.IsWorkSummaryExpanded });
            TurnContentChanged?.Invoke(this, EventArgs.Empty);
            NotesCount = await _store.CountNotesAsync(Conversation.Id);
            ShowConfiguration(await _store.ListContextFilesAsync(Conversation.Id),
                await _store.ListConversationMcpServersAsync(Conversation.Id));
        }
        catch (Exception e) { Status = "Couldn't load this conversation: " + e.Message; }
    }

    /// <summary>Owns its errors so the Config tab's event can fire and forget it: a renamed, toggled or deleted server.</summary>
    internal async Task ReloadMcpServersAsync()
    {
        try
        {
            ShowConfiguration(await _store.ListContextFilesAsync(Conversation.Id),
                await _store.ListConversationMcpServersAsync(Conversation.Id));
        }
        catch (Exception e) { Status = "Couldn't load this conversation's MCP servers: " + e.Message; }
    }

    private void ShowConfiguration(IReadOnlyList<ContextFile> contextFiles, IReadOnlyList<McpServer> mcpServers)
    {
        ContextFiles = contextFiles.Where(f => f.Enabled).Select(ContextFileSummaryViewModel.From).ToArray();
        McpServers = mcpServers.Select(s => s.Enabled ? s.Name : $"{s.Name} (disabled)").ToArray();
    }

    /// <summary>
    /// Takes the saved settings and turns afresh, since another SharpTurns instance may have changed them while this one
    /// didn't hold the conversation. The composer's draft and images stay.
    /// </summary>
    public async Task ReloadAsync(Conversation conversation)
    {
        // The pickers' saves compare with Conversation, so setting it first keeps them from writing back.
        Conversation = conversation;
        Title = conversation.Title;
        SelectedModel = conversation.Model;
        SelectedEffort = conversation.Effort ?? DefaultEffort;
        SelectedOutputStyle = conversation.OutputStyle ?? DefaultOutputStyle;
        AutoSummarize = conversation.AutoSummarize;
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(IsProtected));
        _branchEndpoints.Clear();
        _selectionAnchor = null;
        await LoadAsync();
    }

    /// <summary>The Edit Conversation dialog's saved changes; project is the conversation's project after them.</summary>
    public void ApplyEdit(Conversation conversation, Project project, IReadOnlyList<ContextFile> contextFiles,
        IReadOnlyList<McpServer> mcpServers)
    {
        ShowConfiguration(contextFiles, mcpServers);
        Conversation = Conversation with
        {
            Title = conversation.Title, ProjectId = conversation.ProjectId, WorkingDirectory = conversation.WorkingDirectory,
            IsProtected = conversation.IsProtected,
        };
        Title = conversation.Title;
        Project = project;
        OnPropertyChanged(nameof(Workspace));
        OnPropertyChanged(nameof(IsProtected));
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

    // Clearing also drops dictation's undo history, whose snapshots no longer match the composer.
    [RelayCommand(CanExecute = nameof(CanClearComposer))]
    private void ClearComposer()
    {
        ComposerText = "";
        Dictation.ClearUndoHistory();
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(ComposerText) && !IsAddingImage && !IsCompressing && !Dictation.IsBusy
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
                Dictation.ClearStateAfterSend();
            }
            else Status = "The turn is finishing; send this message as the next turn.";
            return;
        }

        var images = Attachments.Select(a => a.Attachment).ToArray();
        ComposerText = "";
        Dictation.ClearStateAfterSend();
        Attachments.Clear();
        var turn = new TurnViewModel(prompt, images) { IsWorkSummaryExpanded = Display.IsWorkSummaryExpanded };
        Turns.Add(turn);
        TurnContentChanged?.Invoke(this, EventArgs.Empty);
        IsRunning = true;
        StartElapsed();
        Status = "Starting…";
        _turnLifetime = new CancellationTokenSource();
        _queue = new ClaudeCliInputQueue();
        var completed = false;
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
                usage => Dispatcher.UIThread.Post(() =>
                {
                    turn.UpdateUsage(usage);
                    OnPropertyChanged(nameof(LastReportedInputLabel));
                }),
                limits => Dispatcher.UIThread.Post(() => _rateLimitsChanged(limits)),
                (question, token) => Dispatcher.UIThread.InvokeAsync(() => AskAsync(question, token)),
                (tool, input, token) => Dispatcher.UIThread.InvokeAsync(() => ApproveAsync(tool, input, token)));
            // A changed system prompt changes the session policy version, so this turn starts a new CLI session.
            var result = await _runner.RunAsync(Project, Conversation, prompt, images, Preferences.SystemPrompt,
                _queue, callbacks, _turnLifetime.Token);
            // Finish first: clearing IsRunning re-checks the card commands, which need the finished record.
            turn.Finish(result.Turn);
            OnPropertyChanged(nameof(LastReportedInputLabel));
            completed = result.Turn.Status == TurnStatus.Completed;
            // Set before IsRunning clears, so the conversation never looks idle between the turn and its summary.
            _summarizeNext = completed && AutoSummarize;
            IsRunning = false;
            // Only a turn that completes plays the sound.
            if (completed) Preferences.Sounds.PlayNotification(AppSoundNotificationKind.ConversationTurnFinished);
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
            if (result.OmittedContextFiles.Count > 0)
                Status = (Status is null ? "" : Status + " ") + "Left out optional context files: "
                    + string.Join("; ", result.OmittedContextFiles);
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
        // Stopped and failed turns stay as they are, so the user can see what happened and retry.
        if (_summarizeNext) await CompressAsync(turn, automatic: true);
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

    // Both dialogs play the approval sound as they open.
    private async Task<string?> AskAsync(ClaudeCliQuestion question, CancellationToken token)
    {
        if (ShowQuestionAsync is null) return null;
        Preferences.Sounds.PlayNotification(AppSoundNotificationKind.CommandApprovalDisplayed);
        return await ShowQuestionAsync(new QuestionDialogViewModel(question, Project.Name, Title), token);
    }

    private async Task<bool> ApproveAsync(string toolName, string input, CancellationToken token)
    {
        if (ShowPermissionAsync is null) return false;
        Preferences.Sounds.PlayNotification(AppSoundNotificationKind.CommandApprovalDisplayed);
        return await ShowPermissionAsync(toolName, input, token);
    }

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
    private bool CanDeleteTurn(TurnViewModel? turn) => !IsRunning && !IsCompressing && turn?.Record is not null;

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

    // Hiding, showing, compressing, and expanding change the replayed history, so the next turn reseeds a fresh CLI
    // session. They wait for the running turn, whose session would otherwise be checked against a changed history.
    private bool CanChangeContext(TurnViewModel? turn) =>
        !IsRunning && !IsCompressing && turn?.Record is { Status: not TurnStatus.Running };

    [RelayCommand(CanExecute = nameof(CanChangeContext))]
    private async Task ToggleHydrationAsync(TurnViewModel turn)
    {
        var record = turn.Record!;
        try
        {
            await _store.SetTurnHydratedAsync(record.Id, !record.IsHydrated);
            turn.ApplyRecord(record with { IsHydrated = !record.IsHydrated });
            NotifyContextChanged();
            Status = (record.IsHydrated ? $"Hid turn {record.TurnNumber} from Claude's context."
                : $"Turn {record.TurnNumber} is back in Claude's context.") + " The next turn starts a new CLI session.";
        }
        catch (Exception e) { Status = $"Couldn't change turn {record.TurnNumber}: {e.Message}"; }
    }

    [RelayCommand(CanExecute = nameof(CanChangeContext))]
    private async Task ToggleCompressionAsync(TurnViewModel turn)
    {
        var record = turn.Record!;
        if (record.IsCompressed)
        {
            try
            {
                await _store.SetTurnSummaryAsync(record.Id, null, null);
                turn.ApplyRecord(record with { Summary = null, SummaryModel = null });
                NotifyContextChanged();
                Status = $"Expanded turn {record.TurnNumber}. The next turn starts a new CLI session.";
            }
            catch (Exception e) { Status = $"Couldn't expand turn {record.TurnNumber}: {e.Message}"; }
            return;
        }
        await CompressAsync(turn, automatic: false);
    }

    // Owns its errors: the outcome goes to the status line, and a turn that isn't compressed stays as it was.
    private async Task CompressAsync(TurnViewModel turn, bool automatic)
    {
        var record = turn.Record!;
        IsCompressing = true;
        _summarizeNext = false;
        turn.IsCompressing = true;
        Status = automatic ? $"Auto-summarizing turn {record.TurnNumber}…" : $"Compressing turn {record.TurnNumber}…";
        try
        {
            var compressed = await _summarizer.CompressAsync(record);
            turn.ApplyRecord(compressed);
            NotifyContextChanged();
            Status = (automatic ? "Auto-summarized" : "Compressed") + $" turn {record.TurnNumber}"
                + (TurnCompression.Reduction(compressed) is { } reduction
                    ? string.Create(CultureInfo.CurrentCulture, $" ({reduction:0.0}x replay reduction)") : "")
                + ". The next turn starts a new CLI session.";
        }
        catch (Win32Exception)
        {
            Status = ClaudeTurnRunner.ClaudeCliNotStarted;
        }
        catch (Exception e)
        {
            Status = automatic
                ? $"Auto-Summarize left turn {record.TurnNumber} uncompressed: {e.Message}"
                : $"Couldn't compress turn {record.TurnNumber}: {e.Message}";
        }
        finally
        {
            turn.IsCompressing = false;
            IsCompressing = false;
        }
    }

    // Like the turn card actions, image choices change the replay, so they wait for a running turn or compression.
    private bool CanOpenReplayImages() => HasConversationImages && !IsRunning && !IsCompressing;

    [RelayCommand(CanExecute = nameof(CanOpenReplayImages))]
    private async Task OpenReplayImagesAsync()
    {
        if (ShowReplayImagesAsync is null) return;
        await ShowReplayImagesAsync(new ImagesBeingReplayedDialogViewModel(Title,
            Turns.Where(t => t.HasImages && t.Record is not null).ToArray(), SetImageReplayAsync));
    }

    /// <summary>
    /// Chooses whether an image stays in the replay once its turn is compressed. Throws when it can't be saved. A change
    /// to a shown, compressed turn changes the replay, so the next turn starts a new CLI session.
    /// </summary>
    private async Task SetImageReplayAsync(TurnViewModel turn, int sequence, bool include)
    {
        var record = turn.Record!;
        await _store.SetImageReplayAsync(record.Id, sequence, include);
        turn.ApplyRecord(record with
        {
            Parts = record.Parts.Select(p => p.Sequence == sequence ? TurnParts.WithImageReplay(p, include) : p).ToArray(),
        });
        NotifyContextChanged();
        Status = record is { IsHydrated: true, IsCompressed: true }
            ? $"Turn {record.TurnNumber}'s image is {(include ? "now" : "no longer")} replayed. The next turn starts a new CLI session."
            : $"Turn {record.TurnNumber}'s image {(include ? "will stay" : "won't stay")} in the replay when the turn is compressed.";
    }

    private bool CanToggleImageReplay(TurnImageViewModel? image) => CanChangeContext(image?.Owner);

    /// <summary>The turn card's check box under each image; the same choice as in the Conversation Images dialog.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleImageReplay))]
    private async Task ToggleImageReplayAsync(TurnImageViewModel image)
    {
        var turn = image.Owner;
        try { await SetImageReplayAsync(turn, image.Part!.Sequence, !image.IncludeInFutureReplay); }
        catch (Exception e) { Status = $"Couldn't save turn {turn.Record!.TurnNumber}'s image choice: {e.Message}"; }
        finally { image.Refresh(); } // Also puts the check box back when the save failed.
    }

    // The history waits for a running turn, whose messages aren't saved until it ends.
    private bool CanOpenUserMessageHistory() => !IsRunning && Turns.Any(t => t.IsHydrated);

    [RelayCommand(CanExecute = nameof(CanOpenUserMessageHistory))]
    private async Task OpenUserMessageHistoryAsync()
    {
        if (ShowUserMessageHistoryAsync is null) return;
        await ShowUserMessageHistoryAsync(new UserMessageHistoryDialogViewModel(Title, Turns));
    }

    // Notes are never sent to Claude, so they stay available while a turn or compression runs.
    [RelayCommand]
    private async Task OpenNotesAsync()
    {
        if (ShowNotes is null) return;
        var notes = new NotesDialogViewModel(_store, Conversation.Id, Title, count => NotesCount = count);
        await notes.LoadAsync();
        ShowNotes(notes);
    }

    // Exports read the saved turns, so they don't change anything and need only a saved turn. A running turn exports
    // what's saved so far.
    private bool CanExportConversation() => Turns.Any(t => t.Record is not null);

    [RelayCommand(CanExecute = nameof(CanExportConversation))]
    private async Task ExportConversationAsync()
    {
        if (ShowExportAsync is null) return;
        await ShowExportAsync(TurnExportDialogViewModel.ForConversation(
            Turns.Select(t => t.Record).OfType<ConversationTurn>().ToArray(), Conversation, Project, Preferences.DocxExportSettings));
    }

    // The viewer is a separate app that only reads the project's files, so it's always available.
    [RelayCommand]
    private void OpenMarkdownViewer()
    {
        try
        {
            MarkdownViewerLauncher.Launch(Workspace, Preferences.MarkdownViewerWindow.Size, GetScreenPoint?.Invoke());
            Status = "Opened the Markdown Viewer.";
        }
        catch (Exception e)
        {
            Status = "Couldn't open the Markdown Viewer: " + e.Message;
        }
    }

    private static bool CanExportTurn(TurnViewModel? turn) => turn?.Record is { Status: not TurnStatus.Running };

    [RelayCommand(CanExecute = nameof(CanExportTurn))]
    private async Task ExportTurnAsync(TurnViewModel turn)
    {
        if (ShowExportAsync is null) return;
        await ShowExportAsync(TurnExportDialogViewModel.ForTurn(turn.Record!, Conversation, Project, Preferences.DocxExportSettings,
            turn.IsViewingFullCompressedContent));
    }

    // After the turns or their context state change: the header buttons, the metrics row, the Show picker's turns, and
    // the Context Management tab.
    private void NotifyContextChanged()
    {
        OnPropertyChanged(nameof(HasConversationImages));
        OnPropertyChanged(nameof(HasImagesBeingReplayed));
        OnPropertyChanged(nameof(ReplayImagesLabel));
        OpenReplayImagesCommand.NotifyCanExecuteChanged();
        OpenUserMessageHistoryCommand.NotifyCanExecuteChanged();
        ExportConversationCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(LastReportedInputLabel));
        OnPropertyChanged(nameof(TurnsTotalLabel));
        OnPropertyChanged(nameof(TurnsVisibleLabel));
        OnPropertyChanged(nameof(TurnsCompressedLabel));
        OnPropertyChanged(nameof(TurnsHiddenLabel));
        RefreshShownTurns();
    }

    /// <summary>
    /// Brings shown up to date with target, an ordered subset of the same turns, by removing and inserting only what
    /// changed, so the cards that stay keep their rendered content.
    /// </summary>
    internal static void SyncShownTurns<T>(ObservableCollection<T> shown, IEnumerable<T> target) where T : class
    {
        var wanted = target.ToList();
        var keep = wanted.ToHashSet();
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(shown[i])) shown.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= shown.Count || shown[i] != wanted[i]) shown.Insert(i, wanted[i]);
        }
    }

    private static string Count(long count) => count.ToString("N0", CultureInfo.CurrentCulture);

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        Status = "Stopping…";
        CancelTurn();
    }

    partial void OnSelectedModelChanged(string? value) => _ = SaveModelAsync();

    partial void OnSelectedEffortChanged(string value) => _ = SaveModelAsync();

    // Owns its errors so property-change callers can fire and forget it.
    private async Task SaveModelAsync()
    {
        // A picker whose list changed passes null until ApplyModelsChanged restores it.
        if (SelectedModel is null || SelectedEffort is null) return;
        var model = SelectedModel;
        var effort = SelectedEffort == DefaultEffort ? null : SelectedEffort;
        if (model == Conversation.Model && effort == Conversation.Effort) return;
        Conversation = Conversation with { Model = model, Effort = effort };
        try { await _store.SetConversationModelAsync(Conversation.Id, model, effort); }
        catch (Exception e) { Status = "Couldn't save the model choice: " + e.Message; }
    }

    /// <summary>
    /// The model list changed. The store has already moved a replaced ID's conversations to its replacement; this keeps
    /// the loaded conversation in step and restores a picker selection the list change cleared.
    /// </summary>
    internal void ApplyModelsChanged(string? replaced, string? replacement)
    {
        if (replacement is not null && Conversation.Model == replaced) Conversation = Conversation with { Model = replacement };
        SelectedModel = Conversation.Model;
        OnPropertyChanged(nameof(SelectedModel));
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

    partial void OnAutoSummarizeChanged(bool value) => _ = SaveAutoSummarizeAsync();

    // Owns its errors so property-change callers can fire and forget it. Earlier turns are left as they are.
    private async Task SaveAutoSummarizeAsync()
    {
        if (AutoSummarize == Conversation.AutoSummarize) return;
        Conversation = Conversation with { AutoSummarize = AutoSummarize };
        try { await _store.SetConversationAutoSummarizeAsync(Conversation.Id, AutoSummarize); }
        catch (Exception e) { Status = "Couldn't save the Auto-Summarize setting: " + e.Message; }
    }
}
