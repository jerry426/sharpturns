using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.App.Services;
using SharpTurns.App.Services.Dictation;
using SharpTurns.ClaudeCli;
using SharpTurns.Core;
using SharpTurns.Core.InstanceManagement;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const string LastProjectSetting = "last_project_id";
    private const string LastConversationSetting = "last_conversation_id";
    private readonly ConversationStore _store;
    private readonly ClaudeTurnRunner _runner;
    private readonly TurnSummarizer _summarizer;
    // One microphone, so one recording at a time across every conversation's composer.
    private readonly DeepgramDictationService _dictation;
    // Kept while the app runs, so a turn keeps streaming when another conversation is shown.
    private readonly Dictionary<long, ConversationViewModel> _openConversations = [];
    // Several instances can run. Each holds the lock of the conversation it shows and of any running a turn or summary;
    // switching away from an idle one releases it for the others.
    private readonly ConversationLocks _locks;
    private DispatcherTimer? _lockWaitTimer;
    private long? _conversationToRestore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(ProjectBorderColor), nameof(OtherProjects), nameof(OtherProjectsLabel),
        nameof(NoConversationText))]
    [NotifyCanExecuteChangedFor(nameof(EditProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewConversationCommand))]
    private Project? _selectedProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(OtherConversations), nameof(OtherConversationsLabel))]
    [NotifyCanExecuteChangedFor(nameof(EditConversationCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteConversationCommand))]
    private Conversation? _selectedConversation;

    /// <summary>0 is Projects, 1 is Conversations, 2 is Config.</summary>
    [ObservableProperty]
    private int _selectedTabIndex = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConversationSidebarClosed))]
    private bool _isConversationSidebarOpen = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoConversation), nameof(IsDictationBusy))]
    private ConversationViewModel? _currentConversation;

    /// <summary>The selected conversation while another instance holds it; it opens here once that instance lets it go.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoConversationText))]
    private Conversation? _lockedConversation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FiveHourLimitLabel))]
    [NotifyPropertyChangedFor(nameof(WeeklyLimitLabel))]
    [NotifyPropertyChangedFor(nameof(PlanUsageToolTip))]
    [NotifyPropertyChangedFor(nameof(HasPlanUsage))]
    [NotifyPropertyChangedFor(nameof(IsWaitingForPlanUsage))]
    private ClaudeCliRateLimitSnapshot? _planUsage;

    /// <summary>
    /// Without a dictation service, one is made with this platform's recorder and the preferences' Deepgram key. Without
    /// conversation locks, they go in a folder beside the database.
    /// </summary>
    internal MainWindowViewModel(ConversationStore store, ClaudeTurnRunner runner, TurnSummarizer summarizer,
        ApplicationPreferencesViewModel preferences, DeepgramDictationService? dictation = null, ConversationLocks? locks = null)
    {
        _store = store;
        _runner = runner;
        _summarizer = summarizer;
        _locks = locks ?? ConversationLocks.For(store);
        Preferences = preferences;
        _dictation = dictation ?? DeepgramDictationService.Create(() => preferences.ApiKeys.DictationApiKey);
        preferences.ApiKeys.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ApiKeysConfigViewModel.IsConfigured)) _dictation.NotifyConfigurationChanged();
        };
        preferences.Models.ModelsChanged += OnModelsChanged;
        preferences.McpServers.ServersChanged += () =>
        {
            foreach (var conversation in _openConversations.Values) _ = conversation.ReloadMcpServersAsync();
        };
        // Open conversations without their own summarizer show the default's.
        preferences.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ApplicationPreferencesViewModel.SummarizerModel)
                or nameof(ApplicationPreferencesViewModel.SummarizerEffort))
                foreach (var conversation in _openConversations.Values) conversation.NotifySummarizerChanged();
        };
        Projects.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OtherProjects));
            OnPropertyChanged(nameof(OtherProjectsLabel));
        };
        Conversations.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OtherConversations));
            OnPropertyChanged(nameof(OtherConversationsLabel));
        };
    }

    public ObservableCollection<Project> Projects { get; } = [];

    public ObservableCollection<Conversation> Conversations { get; } = [];

    /// <summary>The display controls; every conversation shares them.</summary>
    public ConversationDisplayViewModel Display { get; } = new();

    /// <summary>The Config tab's Preferences; loaded by <see cref="InitializeAsync"/>.</summary>
    public ApplicationPreferencesViewModel Preferences { get; }

    /// <summary>The selected project is pinned above these.</summary>
    public IReadOnlyList<Project> OtherProjects => Projects.Where(p => p != SelectedProject).ToArray();

    public string OtherProjectsLabel => CountLabel(OtherProjects.Count, SelectedProject is null ? "project" : "other project");

    /// <summary>The selected conversation is pinned above these.</summary>
    public IReadOnlyList<Conversation> OtherConversations => Conversations.Where(c => c != SelectedConversation).ToArray();

    public string OtherConversationsLabel =>
        CountLabel(OtherConversations.Count, SelectedConversation is null ? "conversation" : "other conversation");

    /// <summary>The selected project's border color, for its cards and the conversations sidebar.</summary>
    public string ProjectBorderColor => SelectedProject?.Color ?? ProjectColor.Default;

    public bool IsConversationSidebarClosed => !IsConversationSidebarOpen;

    public string WindowTitle =>
        string.Join(" — ", new[] { "SharpTurns", SelectedProject?.Name, SelectedConversation?.Title }.Where(s => s is not null));

    public bool HasNoConversation => CurrentConversation is null;

    public string NoConversationText => LockedConversation is { } locked
        ? $"\"{locked.Title}\" is open in another SharpTurns instance. It opens here once that instance switches to another conversation or closes."
        : SelectedProject is null
            ? "Create a project on the Projects tab to get started."
            : "Create a conversation to get started.";

    /// <summary>A turn or summary is running in one of this instance's conversations.</summary>
    public bool IsAnyTurnActive => _openConversations.Values.Any(c => !c.IsIdle);

    /// <summary>Raised when something the Instance Manager shows for this instance changes.</summary>
    internal event Action? InstanceStateChanged;

    internal InstanceSnapshot CaptureInstanceSnapshot() => new(SelectedProject?.Id, SelectedProject?.Name, SelectedProject?.Color,
        CurrentConversation?.Conversation.Id, CurrentConversation?.Title, CurrentConversation?.SelectedModel, IsAnyTurnActive);

    /// <summary>
    /// Recording or transcribing in the shown conversation. Switching conversations or projects waits for it, so the
    /// transcript lands in the composer on screen.
    /// </summary>
    public bool IsDictationBusy => CurrentConversation?.Dictation.IsBusy == true;

    public bool HasError => ErrorMessage is not null;

    public bool HasPlanUsage => PlanUsage is not null;

    public bool IsWaitingForPlanUsage => PlanUsage is null;

    public string FiveHourLimitLabel => FormatWindow(PlanUsage?.FiveHour);

    public string WeeklyLimitLabel => FormatWindow(PlanUsage?.SevenDay);

    public string PlanUsageToolTip =>
        "Shared Claude subscription limits, not this conversation's token usage. Updated when the CLI reports usage during normal turns; no background polling. Reset times are local."
        + (PlanUsage is { } usage ? $"\nLast reported: {usage.CapturedAt.ToLocalTime():g}" : "");

    public Func<ProjectDialogViewModel, Task<bool>>? ShowProjectDialogAsync { get; set; }

    public Func<EditConversationDialogViewModel, Task<bool>>? ShowEditConversationDialogAsync { get; set; }

    /// <summary>Title, message, and initial text; returns the entered text, or null when canceled.</summary>
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }

    /// <summary>Title and message; returns true when confirmed.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>A startup session, from the Instance Manager's Reload, opens its project and conversation either way.</summary>
    public async Task InitializeAsync(AppStartupSession? startupSession = null)
    {
        try
        {
            await _store.InitializeAsync();
            await FailAbandonedTurnsAsync();
            await Preferences.LoadAsync();
            foreach (var project in await _store.ListProjectsAsync()) Projects.Add(project);
            // The selection is saved either way, so turning restore back on picks up the latest one.
            long? lastProject = null;
            if (startupSession is not null)
            {
                lastProject = startupSession.ProjectId;
                _conversationToRestore = startupSession.ConversationId;
            }
            else if (Preferences.RestoreLastSession)
            {
                lastProject = ParseId(await _store.GetSettingAsync(LastProjectSetting));
                _conversationToRestore = ParseId(await _store.GetSettingAsync(LastConversationSetting));
            }
            SelectedProject = Projects.FirstOrDefault(p => p.Id == lastProject) ?? Projects.FirstOrDefault();
            if (SelectedProject is null) SelectedTabIndex = 0;
            _ = CheckClaudeCliAsync();
        }
        catch (Exception e) { ErrorMessage = "Couldn't open the database: " + e.Message; }
    }

    // A turn still marked running belongs to an instance that exited if its conversation's lock is free; a live
    // instance running it holds the lock. Taking the lock while closing the turns keeps another instance from starting one.
    private async Task FailAbandonedTurnsAsync()
    {
        foreach (var conversationId in await _store.ListConversationsWithRunningTurnsAsync())
        {
            if (!_locks.TryAcquire(conversationId)) continue;
            try { await _store.FailRunningTurnsAsync(conversationId); }
            finally { _locks.Release(conversationId); }
        }
    }

    // The window opens without waiting for it; the preferences' check owns its errors.
    private async Task CheckClaudeCliAsync()
    {
        if (await Preferences.CheckClaudeCliAsync() is { } problem) ErrorMessage ??= problem;
    }

    /// <summary>Also stops a dictation recorder, which would otherwise outlive the app.</summary>
    public void CancelRunningTurns()
    {
        foreach (var conversation in _openConversations.Values)
        {
            conversation.CancelTurn();
            _ = conversation.Dictation.CancelAsync();
        }
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    private bool CanSwitch() => !IsDictationBusy;

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private void SelectProject(Project project) => SelectedProject = project;

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private void SelectConversation(Conversation conversation) => SelectedConversation = conversation;

    [RelayCommand]
    private void ToggleConversationSidebar() => IsConversationSidebarOpen = !IsConversationSidebarOpen;

    partial void OnSelectedProjectChanged(Project? value)
    {
        _ = LoadConversationsAsync(value);
        InstanceStateChanged?.Invoke();
    }

    partial void OnSelectedConversationChanged(Conversation? value) => OpenSelectedConversation();

    // Shows the selected conversation if no other instance holds it; otherwise waits for that instance to let it go.
    private void OpenSelectedConversation()
    {
        var value = SelectedConversation;
        var previous = CurrentConversation;
        if (value is null || SelectedProject is null)
        {
            StopLockWait();
            LockedConversation = null;
            CurrentConversation = null;
            ReleaseIfIdle(previous);
            return;
        }
        if (!_locks.TryAcquire(value.Id, out var heldElsewhereSince))
        {
            LockedConversation = value;
            CurrentConversation = null;
            ReleaseIfIdle(previous);
            StartLockWait();
            return;
        }
        StopLockWait();
        LockedConversation = null;
        if (!_openConversations.TryGetValue(value.Id, out var conversation))
        {
            conversation = new ConversationViewModel(value, SelectedProject, _store, _runner, _summarizer, Display, Preferences,
                _dictation, limits => PlanUsage = limits, OnConversationUpdated, OnConversationBranched);
            conversation.PropertyChanged += OnOpenConversationPropertyChanged;
            _openConversations[value.Id] = conversation;
            _ = ReloadConversationAsync(conversation);
        }
        // One this instance let go of, and another instance opened since, may have changed there.
        else if (heldElsewhereSince) _ = ReloadConversationAsync(conversation);
        CurrentConversation = conversation;
        ReleaseIfIdle(previous);
        _ = SaveSettingAsync(LastConversationSetting, value.Id);
    }

    // Lets another instance open a conversation that isn't shown here and has no turn or summary running.
    private void ReleaseIfIdle(ConversationViewModel? conversation)
    {
        if (conversation is null || conversation == CurrentConversation || !conversation.IsIdle) return;
        _locks.Release(conversation.Conversation.Id);
    }

    // Owns its errors. The list was read when the project was selected, so the conversation may have changed, or been
    // deleted, in another instance since.
    private async Task ReloadConversationAsync(ConversationViewModel conversation)
    {
        var id = conversation.Conversation.Id;
        try
        {
            if (await _store.GetConversationAsync(id) is not { } saved)
            {
                ForgetConversation(conversation);
                if (Conversations.FirstOrDefault(c => c.Id == id) is { } listed) Conversations.Remove(listed);
                if (SelectedConversation?.Id == id) SelectedConversation = Conversations.FirstOrDefault();
                ErrorMessage = $"\"{conversation.Title}\" was deleted in another SharpTurns instance.";
                return;
            }
            await conversation.ReloadAsync(saved);
            var index = Conversations.Select(c => c.Id).ToList().IndexOf(id);
            if (index < 0 || Conversations[index] == saved) return;
            var selected = SelectedConversation?.Id == id;
            Conversations[index] = saved;
            if (selected) SelectedConversation = saved;
        }
        catch (Exception e) { conversation.Status = "Couldn't load this conversation: " + e.Message; }
    }

    // A deleted conversation: stops its recorder and lets its lock go.
    private void ForgetConversation(ConversationViewModel conversation)
    {
        var id = conversation.Conversation.Id;
        if (_openConversations.Remove(id))
        {
            conversation.PropertyChanged -= OnOpenConversationPropertyChanged;
            _ = conversation.Dictation.CancelAsync();
        }
        _locks.Release(id);
    }

    // Checks every second whether the instance holding the selected conversation has let it go.
    private void StartLockWait()
    {
        if (_lockWaitTimer is null)
        {
            _lockWaitTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _lockWaitTimer.Tick += (_, _) => OpenSelectedConversation();
        }
        _lockWaitTimer.Start();
    }

    private void StopLockWait() => _lockWaitTimer?.Stop();

    private void OnOpenConversationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ConversationViewModel conversation) return;
        if (e.PropertyName is nameof(ConversationViewModel.IsRunning) or nameof(ConversationViewModel.IsCompressing))
        {
            OnPropertyChanged(nameof(IsAnyTurnActive));
            ReleaseIfIdle(conversation);
        }
        if (e.PropertyName is nameof(ConversationViewModel.IsRunning) or nameof(ConversationViewModel.IsCompressing)
            or nameof(ConversationViewModel.Title) or nameof(ConversationViewModel.SelectedModel))
            InstanceStateChanged?.Invoke();
    }

    partial void OnCurrentConversationChanged(ConversationViewModel? oldValue, ConversationViewModel? newValue)
    {
        if (oldValue is not null) oldValue.Dictation.PropertyChanged -= OnDictationPropertyChanged;
        if (newValue is not null) newValue.Dictation.PropertyChanged += OnDictationPropertyChanged;
        NotifySwitchingChanged();
        InstanceStateChanged?.Invoke();
    }

    private void OnDictationPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DictationViewModel.IsBusy)) return;
        OnPropertyChanged(nameof(IsDictationBusy));
        NotifySwitchingChanged();
    }

    // Everything that would show another conversation: picking one, a project change, creating or deleting either, and
    // moving one to another project.
    private void NotifySwitchingChanged()
    {
        SelectProjectCommand.NotifyCanExecuteChanged();
        SelectConversationCommand.NotifyCanExecuteChanged();
        NewProjectCommand.NotifyCanExecuteChanged();
        DeleteProjectCommand.NotifyCanExecuteChanged();
        NewConversationCommand.NotifyCanExecuteChanged();
        EditConversationCommand.NotifyCanExecuteChanged();
        DeleteConversationCommand.NotifyCanExecuteChanged();
    }

    // A turn started, so the conversation moves to the top with its new time, matching the store's order.
    private void OnConversationUpdated(Conversation conversation)
    {
        // Missing when another project's list is shown; that list reloads from the store when its project is selected.
        if (Conversations.FirstOrDefault(c => c.Id == conversation.Id) is not { } listed) return;
        var selected = SelectedConversation?.Id == conversation.Id;
        Conversations.Remove(listed);
        Conversations.Insert(0, conversation);
        if (selected) SelectedConversation = conversation;
    }

    // A new branch opens at once. It's the newest conversation, so it goes first, matching the
    // store's order.
    private void OnConversationBranched(Conversation branch, string status)
    {
        // Another project's list reloads from the store when its project is selected.
        if (SelectedProject?.Id != branch.ProjectId) return;
        Conversations.Insert(0, branch);
        SelectedConversation = branch;
        if (CurrentConversation?.Conversation.Id == branch.Id) CurrentConversation.Status = status;
    }

    // The store has already moved a replaced model ID's conversations; keep the listed and open ones in step.
    private void OnModelsChanged(string? replaced, string? replacement)
    {
        if (replacement is not null)
            for (var i = 0; i < Conversations.Count; i++)
            {
                var listed = Conversations[i];
                if (listed.Model != replaced && listed.SummarizerModel != replaced) continue;
                var updated = listed with
                {
                    Model = listed.Model == replaced ? replacement : listed.Model,
                    SummarizerModel = listed.SummarizerModel == replaced ? replacement : listed.SummarizerModel,
                };
                var selected = SelectedConversation?.Id == updated.Id;
                Conversations[i] = updated;
                if (selected) SelectedConversation = updated;
            }
        foreach (var conversation in _openConversations.Values) conversation.ApplyModelsChanged(replaced, replacement);
    }

    // Owns its errors so property-change callers can fire and forget it.
    private async Task LoadConversationsAsync(Project? project)
    {
        if (project is null)
        {
            // Also passed through while a list edit replaces the selected project; keep any pending restore.
            Conversations.Clear();
            return;
        }
        try
        {
            var conversations = await _store.ListConversationsAsync(project.Id);
            if (SelectedProject != project) return; // A later selection replaced this one.
            Conversations.Clear();
            foreach (var conversation in conversations) Conversations.Add(conversation);
            var restore = _conversationToRestore;
            _conversationToRestore = null;
            SelectedConversation = Conversations.FirstOrDefault(c => c.Id == restore) ?? Conversations.FirstOrDefault();
            await SaveSettingAsync(LastProjectSetting, project.Id);
        }
        catch (Exception e) { ErrorMessage = "Couldn't load conversations: " + e.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private async Task NewProjectAsync()
    {
        var dialog = new ProjectDialogViewModel("New Project");
        if (ShowProjectDialogAsync is null || !await ShowProjectDialogAsync(dialog)) return;
        await RunAsync("create the project", async () =>
        {
            var project = await _store.CreateProjectAsync(dialog.Name.Trim(), dialog.WorkingDirectory.Trim(), dialog.ColorHex);
            Projects.Insert(SortedIndex(project), project);
            SelectedProject = project;
        });
    }

    private bool HasSelectedProject() => SelectedProject is not null;

    private bool CanSwitchFromSelectedProject() => HasSelectedProject() && CanSwitch();

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task EditProjectAsync()
    {
        var current = SelectedProject!;
        var dialog = new ProjectDialogViewModel("Edit Project", current);
        if (ShowProjectDialogAsync is null || !await ShowProjectDialogAsync(dialog)) return;
        await RunAsync("save the project", async () =>
        {
            var project = current with
            {
                Name = dialog.Name.Trim(), WorkingDirectory = dialog.WorkingDirectory.Trim(), Color = dialog.ColorHex,
            };
            await _store.UpdateProjectAsync(project);
            foreach (var conversation in _openConversations.Values.Where(c => c.Project.Id == project.Id))
                conversation.Project = project;
            _conversationToRestore = SelectedConversation?.Id;
            Projects.Remove(current);
            Projects.Insert(SortedIndex(project), project);
            SelectedProject = project;
        });
    }

    [RelayCommand(CanExecute = nameof(CanSwitchFromSelectedProject))]
    private async Task DeleteProjectAsync()
    {
        var project = SelectedProject!;
        var open = _openConversations.Values.Where(c => c.Project.Id == project.Id).ToArray();
        if (open.Any(c => c.IsRunning))
        {
            ErrorMessage = "Stop the running turn in this project before deleting it.";
            return;
        }
        // Held through the confirmation, so no other instance opens one of the project's conversations meanwhile.
        var taken = new List<long>();
        try
        {
            var conversations = await _store.ListConversationsAsync(project.Id);
            if (conversations.FirstOrDefault(c => c.IsProtected) is { } protectedConversation)
            {
                ErrorMessage = $"\"{protectedConversation.Title}\" is protected, so \"{project.Name}\" can't be deleted. " +
                    "Clear its protection or move it to another project in Edit Conversation first.";
                return;
            }
            foreach (var conversation in conversations)
            {
                if (_locks.IsHeld(conversation.Id)) continue;
                if (!_locks.TryAcquire(conversation.Id))
                {
                    ErrorMessage = $"\"{conversation.Title}\" is open in another SharpTurns instance, so this project can't be deleted.";
                    return;
                }
                taken.Add(conversation.Id);
            }
            if (ConfirmAsync is null || !await ConfirmAsync("Delete Project",
                    $"Delete \"{project.Name}\" and all of its conversations? This can't be undone. Files in its working directory are not affected."))
                return;
            await RunAsync("delete the project", async () =>
            {
                await _store.DeleteProjectAsync(project.Id);
                foreach (var conversation in open) ForgetConversation(conversation);
                Projects.Remove(project);
                SelectedProject = Projects.FirstOrDefault();
            });
        }
        catch (Exception e) { ErrorMessage = "Couldn't delete the project: " + e.Message; }
        finally
        {
            foreach (var id in taken) _locks.Release(id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSwitchFromSelectedProject))]
    private async Task NewConversationAsync()
    {
        var project = SelectedProject!;
        if (PromptAsync is null || await PromptAsync("New Conversation", "Title", "New conversation") is not { } title
            || string.IsNullOrWhiteSpace(title))
            return;
        await RunAsync("create the conversation", async () =>
        {
            var conversation = await _store.CreateConversationAsync(project.Id, title.Trim(), Preferences.NewConversationModel,
                Preferences.NewConversationEffortOrNull);
            if (SelectedProject != project) return;
            Conversations.Insert(0, conversation);
            SelectedConversation = conversation;
        });
    }

    private bool HasSelectedConversation() => SelectedConversation is not null;

    private bool CanSwitchFromSelectedConversation() => HasSelectedConversation() && CanSwitch();

    // Moving the shown conversation to another project shows that project, with the conversation still open.
    [RelayCommand(CanExecute = nameof(CanSwitchFromSelectedConversation))]
    private async Task EditConversationAsync()
    {
        var current = SelectedConversation!;
        if (ShowEditConversationDialogAsync is null) return;
        EditConversationDialogViewModel dialog;
        try
        {
            dialog = new(current, Projects, await _store.ListMcpServersAsync(),
                (await _store.ListConversationMcpServersAsync(current.Id)).Select(s => s.Id).ToArray(),
                await _store.ListContextFilesAsync(current.Id), Preferences.ModelOptions, Preferences.SummarizerModel,
                Preferences.SummarizerEffort);
        }
        catch (Exception e)
        {
            ErrorMessage = "Couldn't open the conversation's settings: " + e.Message;
            return;
        }
        if (!await ShowEditConversationDialogAsync(dialog) || dialog.SelectedProject is not { } project) return;
        await RunAsync("save the conversation", async () =>
        {
            var saved = await _store.UpdateConversationAsync(current.Id, dialog.Title.Trim(), project.Id,
                dialog.WorkingDirectoryOrNull, dialog.IsProtected, dialog.SummarizerModelOrNull, dialog.SummarizerEffortOrNull,
                dialog.SelectedServerIds, dialog.ContextFileSettings, dialog.ReadOnlyFolders, dialog.ReadWriteFolders,
                dialog.BlockedPathPatterns);
            if (_openConversations.TryGetValue(saved.Id, out var open))
                open.ApplyEdit(saved, project, dialog.ContextFileSettings, dialog.SelectedServers);
            // A turn may have replaced the listed record meanwhile.
            if (Conversations.FirstOrDefault(c => c.Id == saved.Id) is not { } listed) return;
            var selected = SelectedConversation?.Id == saved.Id;
            if (saved.ProjectId == SelectedProject?.Id)
            {
                Conversations[Conversations.IndexOf(listed)] = saved;
                if (selected) SelectedConversation = saved;
                return;
            }
            Conversations.Remove(listed);
            if (!selected) return;
            _conversationToRestore = saved.Id;
            SelectedProject = Projects.FirstOrDefault(p => p.Id == project.Id);
        });
    }

    [RelayCommand(CanExecute = nameof(CanSwitchFromSelectedConversation))]
    private async Task DeleteConversationAsync()
    {
        var conversation = SelectedConversation!;
        if (!_locks.IsHeld(conversation.Id))
        {
            ErrorMessage = $"\"{conversation.Title}\" is open in another SharpTurns instance, so it can't be deleted here.";
            return;
        }
        if (conversation.IsProtected)
        {
            ErrorMessage = $"\"{conversation.Title}\" is protected. Clear Protect Conversation in Edit Conversation to delete it.";
            return;
        }
        if (_openConversations.TryGetValue(conversation.Id, out var open) && open.IsRunning)
        {
            ErrorMessage = "Stop the running turn before deleting this conversation.";
            return;
        }
        if (ConfirmAsync is null || !await ConfirmAsync("Delete Conversation",
                $"Delete \"{conversation.Title}\"? This can't be undone."))
            return;
        await RunAsync("delete the conversation", async () =>
        {
            await _store.DeleteConversationAsync(conversation.Id);
            if (_openConversations.TryGetValue(conversation.Id, out var removed)) ForgetConversation(removed);
            Conversations.Remove(conversation);
            SelectedConversation = Conversations.FirstOrDefault();
        });
    }

    private async Task RunAsync(string action, Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception e) { ErrorMessage = $"Couldn't {action}: {e.Message}"; }
    }

    private async Task SaveSettingAsync(string key, long id)
    {
        try { await _store.SetSettingAsync(key, id.ToString(CultureInfo.InvariantCulture)); }
        catch (Exception e) { ErrorMessage = "Couldn't save the current selection: " + e.Message; }
    }

    private int SortedIndex(Project project)
    {
        var index = 0;
        while (index < Projects.Count && string.Compare(Projects[index].Name, project.Name, StringComparison.OrdinalIgnoreCase) <= 0)
            index++;
        return index;
    }

    // Shows the share remaining. Absolute reset times stay true when the window sits idle past a reset.
    private static string FormatWindow(ClaudeCliRateLimitWindow? window)
    {
        var remaining = window?.Utilization is { } fraction
            ? $"{Math.Max(0, (int)Math.Round(100 - Math.Clamp(fraction, 0, 1) * 100, MidpointRounding.AwayFromZero))}%"
            : "not reported";
        return window?.ResetsAt is { } reset ? $"{remaining} (resets at {reset.ToLocalTime():g})" : remaining;
    }

    private static string CountLabel(int count, string noun) =>
        $"{count.ToString(CultureInfo.CurrentCulture)} {noun}{(count == 1 ? "" : "s")}";

    private static long? ParseId(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
}
