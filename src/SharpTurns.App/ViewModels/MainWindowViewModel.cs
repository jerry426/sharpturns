using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.App.Services;
using SharpTurns.ClaudeCli;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const string LastProjectSetting = "last_project_id";
    private const string LastConversationSetting = "last_conversation_id";
    private readonly ConversationStore _store;
    private readonly ClaudeTurnRunner _runner;
    // Kept while the app runs, so a turn keeps streaming when another conversation is shown.
    private readonly Dictionary<long, ConversationViewModel> _openConversations = [];
    private long? _conversationToRestore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyCanExecuteChangedFor(nameof(EditProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(NewConversationCommand))]
    private Project? _selectedProject;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    [NotifyCanExecuteChangedFor(nameof(RenameConversationCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteConversationCommand))]
    private Conversation? _selectedConversation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoConversation))]
    private ConversationViewModel? _currentConversation;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlanUsageText))]
    [NotifyPropertyChangedFor(nameof(PlanUsageToolTip))]
    [NotifyPropertyChangedFor(nameof(HasPlanUsage))]
    private ClaudeCliRateLimitSnapshot? _planUsage;

    internal MainWindowViewModel(ConversationStore store, ClaudeTurnRunner runner)
    {
        _store = store;
        _runner = runner;
    }

    public ObservableCollection<Project> Projects { get; } = [];

    public ObservableCollection<Conversation> Conversations { get; } = [];

    public string WindowTitle =>
        string.Join(" — ", new[] { "SharpTurns", SelectedProject?.Name, SelectedConversation?.Title }.Where(s => s is not null));

    public bool HasNoConversation => CurrentConversation is null;

    public bool HasError => ErrorMessage is not null;

    public bool HasPlanUsage => PlanUsage is not null;

    public string PlanUsageText => PlanUsage is { } usage
        ? $"5-hour: {FormatWindow(usage.FiveHour)}\n7-day: {FormatWindow(usage.SevenDay)}" : "";

    public string PlanUsageToolTip => PlanUsage is { } usage
        ? "Your subscription's shared usage limits, as last reported by the CLI during a turn. Not this conversation's tokens.\n"
          + $"Reported at {usage.CapturedAt.ToLocalTime():t}."
        : "";

    public Func<ProjectDialogViewModel, Task<bool>>? ShowProjectDialogAsync { get; set; }

    /// <summary>Title, message, and initial text; returns the entered text, or null when canceled.</summary>
    public Func<string, string, string, Task<string?>>? PromptAsync { get; set; }

    /// <summary>Title and message; returns true when confirmed.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public async Task InitializeAsync()
    {
        try
        {
            await _store.InitializeAsync();
            foreach (var project in await _store.ListProjectsAsync()) Projects.Add(project);
            var lastProject = ParseId(await _store.GetSettingAsync(LastProjectSetting));
            _conversationToRestore = ParseId(await _store.GetSettingAsync(LastConversationSetting));
            SelectedProject = Projects.FirstOrDefault(p => p.Id == lastProject) ?? Projects.FirstOrDefault();
        }
        catch (Exception e) { ErrorMessage = "Couldn't open the database: " + e.Message; }
    }

    public void CancelRunningTurns()
    {
        foreach (var conversation in _openConversations.Values) conversation.CancelTurn();
    }

    [RelayCommand]
    private void DismissError() => ErrorMessage = null;

    partial void OnSelectedProjectChanged(Project? value) => _ = LoadConversationsAsync(value);

    partial void OnSelectedConversationChanged(Conversation? value)
    {
        if (value is null || SelectedProject is null)
        {
            CurrentConversation = null;
            return;
        }
        if (!_openConversations.TryGetValue(value.Id, out var conversation))
        {
            conversation = new ConversationViewModel(value, SelectedProject, _store, _runner, limits => PlanUsage = limits);
            _openConversations[value.Id] = conversation;
            _ = conversation.LoadAsync();
        }
        CurrentConversation = conversation;
        _ = SaveSettingAsync(LastConversationSetting, value.Id);
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

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        var dialog = new ProjectDialogViewModel("New Project");
        if (ShowProjectDialogAsync is null || !await ShowProjectDialogAsync(dialog)) return;
        await RunAsync("create the project", async () =>
        {
            var project = await _store.CreateProjectAsync(dialog.Name.Trim(), dialog.WorkingDirectory.Trim());
            Projects.Insert(SortedIndex(project), project);
            SelectedProject = project;
        });
    }

    private bool HasSelectedProject() => SelectedProject is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task EditProjectAsync()
    {
        var current = SelectedProject!;
        var dialog = new ProjectDialogViewModel("Edit Project", current);
        if (ShowProjectDialogAsync is null || !await ShowProjectDialogAsync(dialog)) return;
        await RunAsync("save the project", async () =>
        {
            var project = current with { Name = dialog.Name.Trim(), WorkingDirectory = dialog.WorkingDirectory.Trim() };
            await _store.UpdateProjectAsync(project);
            foreach (var conversation in _openConversations.Values.Where(c => c.Project.Id == project.Id))
                conversation.Project = project;
            _conversationToRestore = SelectedConversation?.Id;
            Projects.Remove(current);
            Projects.Insert(SortedIndex(project), project);
            SelectedProject = project;
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task DeleteProjectAsync()
    {
        var project = SelectedProject!;
        var open = _openConversations.Values.Where(c => c.Project.Id == project.Id).ToArray();
        if (open.Any(c => c.IsRunning))
        {
            ErrorMessage = "Stop the running turn in this project before deleting it.";
            return;
        }
        if (ConfirmAsync is null || !await ConfirmAsync("Delete Project",
                $"Delete \"{project.Name}\" and all of its conversations? This can't be undone. Files in its working directory are not affected."))
            return;
        await RunAsync("delete the project", async () =>
        {
            await _store.DeleteProjectAsync(project.Id);
            foreach (var conversation in open) _openConversations.Remove(conversation.Conversation.Id);
            Projects.Remove(project);
            SelectedProject = Projects.FirstOrDefault();
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedProject))]
    private async Task NewConversationAsync()
    {
        var project = SelectedProject!;
        if (PromptAsync is null || await PromptAsync("New Conversation", "Title", "New conversation") is not { } title
            || string.IsNullOrWhiteSpace(title))
            return;
        await RunAsync("create the conversation", async () =>
        {
            var conversation = await _store.CreateConversationAsync(project.Id, title.Trim());
            if (SelectedProject != project) return;
            Conversations.Insert(0, conversation);
            SelectedConversation = conversation;
        });
    }

    private bool HasSelectedConversation() => SelectedConversation is not null;

    [RelayCommand(CanExecute = nameof(HasSelectedConversation))]
    private async Task RenameConversationAsync()
    {
        var current = SelectedConversation!;
        if (PromptAsync is null || await PromptAsync("Rename Conversation", "Title", current.Title) is not { } title
            || string.IsNullOrWhiteSpace(title))
            return;
        await RunAsync("rename the conversation", async () =>
        {
            var conversation = current with { Title = title.Trim() };
            await _store.RenameConversationAsync(conversation.Id, conversation.Title);
            if (_openConversations.TryGetValue(conversation.Id, out var open)) open.ApplyRename(conversation);
            var index = Conversations.IndexOf(current);
            if (index < 0) return;
            Conversations[index] = conversation;
            SelectedConversation = conversation;
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedConversation))]
    private async Task DeleteConversationAsync()
    {
        var conversation = SelectedConversation!;
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
            _openConversations.Remove(conversation.Id);
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

    // Absolute reset times stay true when the window sits idle past a reset.
    private static string FormatWindow(ClaudeCliRateLimitWindow? window)
    {
        var used = window?.Utilization is { } fraction ? $"{Math.Round(Math.Clamp(fraction, 0, 1) * 100)}% used" : "not reported";
        return window?.ResetsAt is { } reset ? $"{used}, resets {reset.ToLocalTime():g}" : used;
    }

    private static long? ParseId(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
}
