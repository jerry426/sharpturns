using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class ApplicationPreferencesViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-preferences-{Guid.NewGuid():N}");

    [Fact]
    public async Task PreferencesAreSavedAndReloaded()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var preferences = new ApplicationPreferencesViewModel(store);
        await preferences.LoadAsync();
        Assert.True(preferences.RestoreLastSession);
        Assert.Equal(new WindowSize(1400, 860), preferences.StartupWindow.Size);
        Assert.Equal(DocxExportSettings.Default, preferences.DocxExportSettings);
        preferences.RestoreLastSession = false;

        // A size below the minimum is refused and nothing changes.
        preferences.NotesWindow.WidthText = "500";
        await preferences.NotesWindow.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Width must be a number from 820 to 10000.", preferences.NotesWindow.Message);
        Assert.Null(await store.GetSettingAsync("notes_window_size"));

        preferences.NotesWindow.WidthText = "1000";
        preferences.NotesWindow.HeightText = "900";
        await preferences.NotesWindow.SaveCommand.ExecuteAsync(null);
        preferences.StartupWindow.WidthText = " 1800.4 ";
        await preferences.StartupWindow.SaveCommand.ExecuteAsync(null);
        preferences.MarkdownViewerWindow.WidthText = "1600";
        await preferences.MarkdownViewerWindow.SaveCommand.ExecuteAsync(null);
        await preferences.MarkdownViewerWindow.ResetCommand.ExecuteAsync(null);
        var docx = DocxExportSettings.Default with { NormalFontFamily = "Arial", MarginTopInches = 0.5 };
        preferences.ShowDocxExportDefaultsDialogAsync = dialog =>
        {
            Assert.Equal("Aptos", dialog.NormalFontFamily);
            return Task.FromResult<DocxExportSettings?>(docx);
        };
        await preferences.ConfigureDocxExportCommand.ExecuteAsync(null);
        Assert.Equal("Saved the DOCX export defaults.", preferences.Status);

        var reloaded = new ApplicationPreferencesViewModel(store);
        await reloaded.LoadAsync();
        Assert.False(reloaded.RestoreLastSession);
        Assert.Equal(new WindowSize(1000, 900), reloaded.NotesWindow.Size);
        Assert.Equal(new WindowSize(1800, 860), reloaded.StartupWindow.Size);
        Assert.Equal("Current startup window size: 1800 × 860.", reloaded.StartupWindow.Message);
        Assert.Equal(reloaded.MarkdownViewerWindow.Default, reloaded.MarkdownViewerWindow.Size);
        Assert.Null(await store.GetSettingAsync("markdown_viewer_window_size"));
        Assert.Equal(docx, reloaded.DocxExportSettings);

        // A canceled dialog keeps the settings.
        reloaded.ShowDocxExportDefaultsDialogAsync = _ => Task.FromResult<DocxExportSettings?>(null);
        await reloaded.ConfigureDocxExportCommand.ExecuteAsync(null);
        Assert.Equal(docx, reloaded.DocxExportSettings);
    }

    [Fact]
    public void AnUnreadableSavedSizeUsesTheDefault()
    {
        WindowSize defaultSize = new(980, 660), minimum = new(820, 520);
        Assert.Equal(new WindowSize(1000, 900), WindowSize.Parse("1000,900", defaultSize, minimum));
        Assert.Equal(defaultSize, WindowSize.Parse(null, defaultSize, minimum));
        Assert.Equal(defaultSize, WindowSize.Parse("1000", defaultSize, minimum));
        Assert.Equal(defaultSize, WindowSize.Parse("1000,100", defaultSize, minimum));
        Assert.Equal(defaultSize, WindowSize.Parse("20000,900", defaultSize, minimum));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
