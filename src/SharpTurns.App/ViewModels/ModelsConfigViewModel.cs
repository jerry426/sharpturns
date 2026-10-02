using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.App.Services;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// The Config tab's Models: the model IDs every model picker offers, in the user's order. Renaming or deleting an ID
/// moves the conversations and settings that use it, so no picker is left on an ID the list doesn't have.
/// </summary>
public sealed partial class ModelsConfigViewModel : ObservableObject
{
    private const int MaxIdLength = 200;
    // The settings that hold a model ID; the store moves them with the conversations.
    private static readonly string[] ModelSettings = [ApplicationPreferencesViewModel.NewConversationModelSetting, TurnSummarizer.ModelSetting];
    private readonly ConversationStore _store;

    [ObservableProperty]
    private string? _selectedModel;

    /// <summary>The ID to add, or the selected ID as edited for Rename.</summary>
    [ObservableProperty]
    private string _modelIdText = "";

    /// <summary>The outcome of the last change.</summary>
    [ObservableProperty]
    private string _message = "";

    internal ModelsConfigViewModel(ConversationStore store) => _store = store;

    /// <summary>
    /// The listed IDs; also every model picker's list. Shared so the conversation pickers keep the same ItemsSource when
    /// the conversation changes.
    /// </summary>
    public ObservableCollection<string> Models { get; } = [];

    /// <summary>
    /// Raised after the list changes. A list change can clear a picker's selection, so subscribers restore it. When an ID
    /// was renamed or deleted in favor of another, replaced and replacement name them; the store has already moved its uses.
    /// </summary>
    public event Action<string?, string?>? ModelsChanged;

    /// <summary>Title and message; returns true when confirmed.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Title, message, and the IDs to choose from; returns the chosen ID, or null when canceled.</summary>
    public Func<string, string, IReadOnlyList<string>, Task<string?>>? ChooseReplacementAsync { get; set; }

    internal async Task LoadAsync()
    {
        var models = await _store.ListModelsAsync();
        Models.Clear();
        foreach (var model in models) Models.Add(model);
        NotifyCommands();
    }

    partial void OnSelectedModelChanged(string? value)
    {
        // Null while a rename replaces the selected item; the new ID is selected right after.
        if (value is not null) ModelIdText = value;
        NotifyCommands();
    }

    [RelayCommand]
    private async Task AddModelAsync()
    {
        if (ValidateId(null) is not { } id) return;
        try
        {
            await _store.AddModelAsync(id);
            Models.Add(id);
            SelectedModel = id;
            Message = $"Added {id}.";
            ModelsChanged?.Invoke(null, null);
        }
        catch (Exception e) { Message = "Couldn't add the model: " + e.Message; }
        NotifyCommands();
    }

    private bool HasSelection() => SelectedModel is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RenameModelAsync()
    {
        if (SelectedModel is not { } old || ValidateId(old) is not { } id) return;
        if (id == old)
        {
            Message = "Edit the ID in the box, then click Rename.";
            return;
        }
        try
        {
            await PinSummarizerDefaultAsync(old);
            await _store.RenameModelAsync(old, id, ModelSettings);
            var index = Models.IndexOf(old);
            Models[index] = id;
            SelectedModel = id;
            Message = $"Renamed {old} to {id}. Everything that used {old} now uses {id}.";
            ModelsChanged?.Invoke(old, id);
        }
        catch (Exception e) { Message = "Couldn't rename the model: " + e.Message; }
    }

    // The summarizer always needs a model, so the last ID stays.
    private bool CanDelete() => SelectedModel is not null && Models.Count > 1;

    /// <summary>An ID in use is replaced by one the user chooses; an unused one is deleted after confirming.</summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteModelAsync()
    {
        if (SelectedModel is not { } id) return;
        try
        {
            string? replacement = null;
            if (await DescribeUsesAsync(id) is { Length: > 0 } uses)
            {
                if (ChooseReplacementAsync is null
                    || await ChooseReplacementAsync("Delete Model",
                        $"{id} is used by {uses}. Choose the model that replaces it there.",
                        Models.Where(m => m != id).ToArray()) is not { } chosen)
                    return;
                replacement = chosen;
                await PinSummarizerDefaultAsync(id);
            }
            else if (ConfirmAsync is null || !await ConfirmAsync("Delete Model", $"Delete {id} from the model list?"))
                return;
            await _store.DeleteModelAsync(id, replacement, ModelSettings);
            var index = Models.IndexOf(id);
            Models.RemoveAt(index);
            SelectedModel = Models[Math.Min(index, Models.Count - 1)];
            Message = replacement is null ? $"Deleted {id}." : $"Deleted {id}. Everything that used it now uses {replacement}.";
            ModelsChanged?.Invoke(id, replacement);
        }
        catch (Exception e) { Message = "Couldn't delete the model: " + e.Message; }
        NotifyCommands();
    }

    private bool CanMoveUp() => SelectedModel is { } id && Models.IndexOf(id) > 0;

    private bool CanMoveDown() => SelectedModel is { } id && Models.IndexOf(id) is >= 0 and var index && index < Models.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private Task MoveModelUpAsync() => MoveAsync(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private Task MoveModelDownAsync() => MoveAsync(1);

    private async Task MoveAsync(int offset)
    {
        if (SelectedModel is not { } id || Models.IndexOf(id) is var index && (uint)(index + offset) >= (uint)Models.Count) return;
        var order = Models.ToList();
        order.RemoveAt(index);
        order.Insert(index + offset, id);
        try
        {
            await _store.SetModelOrderAsync(order);
            Models.Move(index, index + offset);
            SelectedModel = id;
            Message = "";
            ModelsChanged?.Invoke(null, null);
        }
        catch (Exception e) { Message = "Couldn't move the model: " + e.Message; }
        NotifyCommands();
    }

    // Returns the trimmed ID, or null with the reason in Message. except is the ID being renamed.
    private string? ValidateId(string? except)
    {
        var id = ModelIdText.Trim();
        if (id.Length == 0)
            Message = "Enter a model ID, such as claude-sonnet-5-5.";
        else if (id.Length > MaxIdLength || id.Any(char.IsWhiteSpace))
            Message = $"A model ID can't contain spaces or be longer than {MaxIdLength} characters.";
        else if (Models.FirstOrDefault(m => string.Equals(m, id, StringComparison.OrdinalIgnoreCase)) is { } existing && existing != except)
            Message = $"{existing} is already in the list.";
        else
            return id;
        return null;
    }

    // What uses the ID, for the delete dialog; empty when nothing does.
    private async Task<string> DescribeUsesAsync(string id)
    {
        var uses = new List<string>();
        if (await _store.CountConversationsUsingModelAsync(id) is > 0 and var count)
            uses.Add(string.Create(CultureInfo.CurrentCulture, $"{count:N0} conversation{(count == 1 ? "" : "s")}"));
        if (await _store.GetSettingAsync(ApplicationPreferencesViewModel.NewConversationModelSetting) == id)
            uses.Add("the new conversation model");
        if ((await _store.GetSettingAsync(TurnSummarizer.ModelSetting) ?? TurnSummarizer.DefaultModel) == id)
            uses.Add("the summarizer");
        return uses.Count switch
        {
            0 => "",
            1 => uses[0],
            _ => string.Join(", ", uses[..^1]) + " and " + uses[^1],
        };
    }

    // The summarizer's built-in default isn't saved, so save it before its ID moves; the store then moves it with the rest.
    private async Task PinSummarizerDefaultAsync(string id)
    {
        if (id == TurnSummarizer.DefaultModel && await _store.GetSettingAsync(TurnSummarizer.ModelSetting) is null)
            await _store.SetSettingAsync(TurnSummarizer.ModelSetting, id);
    }

    private void NotifyCommands()
    {
        RenameModelCommand.NotifyCanExecuteChanged();
        DeleteModelCommand.NotifyCanExecuteChanged();
        MoveModelUpCommand.NotifyCanExecuteChanged();
        MoveModelDownCommand.NotifyCanExecuteChanged();
    }
}
