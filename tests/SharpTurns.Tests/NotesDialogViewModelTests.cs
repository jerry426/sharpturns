using SharpTurns.App.ViewModels;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class NotesDialogViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-notes-{Guid.NewGuid():N}");

    [Fact]
    public async Task NotesAreCreatedEditedCanceledAndDeleted()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync("Project", "/work");
        var conversation = await store.CreateConversationAsync(project.Id, "Conversation");
        var count = -1;
        var notes = new NotesDialogViewModel(store, conversation.Id, conversation.Title, c => count = c);

        await notes.LoadAsync();
        Assert.Equal((0, false, false), (count, notes.HasNotes, notes.IsEditorEnabled));

        notes.NewCommand.Execute(null);
        Assert.Equal(("Untitled Note", true), (notes.TitleText, notes.IsEditorEnabled));
        // A note needs content.
        Assert.False(notes.SaveCommand.CanExecute(null));
        notes.ContentText = "# Plan\n- first";
        await notes.SaveCommand.ExecuteAsync(null);
        Assert.Equal((1, "Untitled Note", false), (count, notes.SelectedNote?.Title, notes.HasDirtyChanges));

        notes.TitleText = "Plan";
        Assert.True(notes.HasDirtyChanges);
        notes.CancelCommand.Execute(null);
        Assert.Equal(("Untitled Note", false), (notes.TitleText, notes.HasDirtyChanges));
        notes.TitleText = "Plan";
        await notes.SaveCommand.ExecuteAsync(null);

        notes.NewCommand.Execute(null);
        notes.TitleText = "Second";
        notes.ContentText = "text";
        await notes.SaveCommand.ExecuteAsync(null);
        Assert.Equal((2, "Second"), (count, notes.SelectedNote?.Title));
        Assert.Equal(["Plan", "Second"], notes.Notes.Select(n => n.Title));

        notes.ConfirmAsync = (_, _) => Task.FromResult(false);
        await notes.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(2, notes.Notes.Count);
        notes.ConfirmAsync = (_, _) => Task.FromResult(true);
        await notes.DeleteCommand.ExecuteAsync(null);
        Assert.Equal((1, "Plan"), (count, notes.SelectedNote?.Title));
        Assert.Equal("# Plan\n- first", Assert.Single(await store.ListNotesAsync(conversation.Id)).Content);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
