using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.Core;
using SharpTurns.Core.Persistence;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// As in the Workbench's Notes window, without tags or workspace notes: the conversation's notes, each with a title and
/// Markdown source saved as typed. Notes are never sent to Claude. As in the Workbench, choosing another note or New
/// discards unsaved edits.
/// </summary>
public sealed partial class NotesDialogViewModel : ObservableObject
{
    private const string NewNoteTitle = "Untitled Note";
    private readonly ConversationStore _store;
    private readonly Action<int> _countChanged;
    private string _baselineTitle = "";
    private string _baselineContent = "";

    [ObservableProperty]
    private NoteCardViewModel? _selectedNote;

    [ObservableProperty]
    private bool _isNewNote;

    [ObservableProperty]
    private string _titleText = "";

    [ObservableProperty]
    private string _contentText = "";

    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>countChanged receives the number of notes after each load, save, and delete.</summary>
    internal NotesDialogViewModel(ConversationStore store, long conversationId, string conversationTitle, Action<int> countChanged)
    {
        _store = store;
        _countChanged = countChanged;
        ConversationId = conversationId;
        ConversationTitle = conversationTitle;
        Notes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNotes));
    }

    public long ConversationId { get; }

    public string ConversationTitle { get; }

    public ObservableCollection<NoteCardViewModel> Notes { get; } = [];

    public bool HasNotes => Notes.Count > 0;

    public bool IsEditorEnabled => (SelectedNote is not null || IsNewNote) && !IsBusy;

    public bool HasDirtyChanges => (SelectedNote is not null || IsNewNote)
        && (TitleText != _baselineTitle || ContentText != _baselineContent);

    /// <summary>Title and message; returns true when confirmed.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Owns its errors, which replace the status. Selects the note with selectId, or the first note.</summary>
    public async Task LoadAsync(long? selectId = null)
    {
        IsBusy = true;
        try
        {
            var notes = await _store.ListNotesAsync(ConversationId);
            Notes.Clear();
            foreach (var note in notes) Notes.Add(new NoteCardViewModel(note));
            SelectedNote = Notes.FirstOrDefault(n => n.Note.Id == selectId) ?? Notes.FirstOrDefault();
            _countChanged(Notes.Count);
        }
        catch (Exception e) { Status = "Couldn't load the notes: " + e.Message; }
        finally { IsBusy = false; }
    }

    private bool CanCreate() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private void New()
    {
        SelectedNote = null;
        IsNewNote = true;
        SetEditor(NewNoteTitle, "");
        Status = "Creating a new note.";
    }

    // As in the Workbench, a note needs a title and content.
    private bool CanSave() => IsEditorEnabled && (IsNewNote || HasDirtyChanges)
        && !string.IsNullOrWhiteSpace(TitleText) && !string.IsNullOrWhiteSpace(ContentText);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var saved = IsNewNote
                ? await _store.CreateNoteAsync(ConversationId, TitleText.Trim(), ContentText)
                : await _store.UpdateNoteAsync(SelectedNote!.Note.Id, TitleText.Trim(), ContentText);
            IsNewNote = false;
            Status = "Note saved.";
            await LoadAsync(saved.Id);
        }
        catch (Exception e) { Status = "Couldn't save the note: " + e.Message; }
        finally { IsBusy = false; }
    }

    private bool CanCancel() => IsNewNote || HasDirtyChanges;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        if (IsNewNote)
        {
            IsNewNote = false;
            SelectedNote = Notes.FirstOrDefault();
            if (SelectedNote is null) SetEditor("", "");
        }
        else SetEditor(SelectedNote!.Note.Title, SelectedNote.Note.Content);
        Status = "Edits canceled.";
    }

    private bool CanDelete() => SelectedNote is not null && !IsNewNote && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        var note = SelectedNote!.Note;
        if (ConfirmAsync is null || !await ConfirmAsync("Delete Note",
                $"Delete “{note.Title}”? This permanently deletes the note and can't be undone."))
            return;
        IsBusy = true;
        try
        {
            await _store.DeleteNoteAsync(note.Id);
            Status = "Note deleted.";
            await LoadAsync();
        }
        catch (Exception e) { Status = "Couldn't delete the note: " + e.Message; }
        finally { IsBusy = false; }
    }

    partial void OnSelectedNoteChanged(NoteCardViewModel? value)
    {
        if (value is not null)
        {
            IsNewNote = false;
            SetEditor(value.Note.Title, value.Note.Content);
        }
        else if (!IsNewNote) SetEditor("", "");
        RefreshEditorState();
    }

    partial void OnIsNewNoteChanged(bool value) => RefreshEditorState();

    partial void OnTitleTextChanged(string value) => RefreshEditorState();

    partial void OnContentTextChanged(string value) => RefreshEditorState();

    partial void OnIsBusyChanged(bool value) => RefreshEditorState();

    // The text the editor starts from; edits away from it are unsaved changes.
    private void SetEditor(string title, string content)
    {
        _baselineTitle = title;
        _baselineContent = content;
        TitleText = title;
        ContentText = content;
        RefreshEditorState();
    }

    private void RefreshEditorState()
    {
        OnPropertyChanged(nameof(IsEditorEnabled));
        OnPropertyChanged(nameof(HasDirtyChanges));
        NewCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>A note in the Notes window's list, as in the Workbench: its title, the start of its content, and when it changed.</summary>
public sealed class NoteCardViewModel(ConversationNote note)
{
    public ConversationNote Note { get; } = note;

    public string Title => Note.Title;

    public string Preview => Note.Content.Length <= 140 ? Note.Content : string.Concat(Note.Content.AsSpan(0, 140), "…");

    public string TimestampLabel => Note.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
