using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.ClaudeCli;
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
    public async Task SavingTheClaudePathChecksItsVersion()
    {
        if (OperatingSystem.IsWindows()) return; // The fake CLI is a POSIX shell script.
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var preferences = new ApplicationPreferencesViewModel(store);
        await preferences.LoadAsync();
        var claude = Path.Combine(_directory, "claude");

        async Task<string> SaveAsync(string script)
        {
            await File.WriteAllTextAsync(claude, "#!/bin/sh\n" + script);
            File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            preferences.ClaudePathText = claude;
            await preferences.SaveClaudePathCommand.ExecuteAsync(null);
            return preferences.ClaudeCliMessage;
        }

        Assert.Equal($"Saved. Turns and summaries run {claude}, version 2.1.290.", await SaveAsync("echo '2.1.290 (Claude Code)'\n"));
        Assert.Equal($"Saved. SharpTurns needs Claude Code {ClaudeCliVersion.Minimum} or later, but {claude} is version 2.1.100. "
            + "Run `claude update` to update it.", await SaveAsync("echo '2.1.100 (Claude Code)'\n"));
        Assert.EndsWith("didn't report a version.", await SaveAsync("echo 'unknown'\n"));
        // The path stays saved when the check fails; the startup check returns the problem for the window's banner.
        Assert.Equal(claude, preferences.ClaudePath);
        File.SetUnixFileMode(claude, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Contains("couldn't run", await preferences.CheckClaudeCliAsync());
    }

    [Fact]
    public async Task CliSettingsAreSavedAndReloaded()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var preferences = new ApplicationPreferencesViewModel(store);
        await preferences.LoadAsync();
        Assert.Null(preferences.ClaudePath);
        Assert.Equal(ClaudeCliCodingPolicy.DefaultSystemPrompt, preferences.SystemPrompt);
        Assert.False(preferences.ResetSystemPromptCommand.CanExecute(null));

        // The path must be a full path to a file that exists.
        preferences.ClaudePathText = "bin/claude";
        await preferences.SaveClaudePathCommand.ExecuteAsync(null);
        Assert.StartsWith("Enter the full path", preferences.ClaudeCliMessage);
        preferences.ClaudePathText = Path.Combine(_directory, "missing");
        await preferences.SaveClaudePathCommand.ExecuteAsync(null);
        Assert.StartsWith("There's no file at", preferences.ClaudeCliMessage);
        Assert.Null(preferences.ClaudePath);
        var claude = Path.Combine(_directory, OperatingSystem.IsWindows() ? "claude.exe" : "claude");
        await File.WriteAllTextAsync(claude, "");
        preferences.ClaudePathText = " " + claude + " ";
        await preferences.SaveClaudePathCommand.ExecuteAsync(null);
        Assert.Equal(claude, preferences.ClaudePath);

        preferences.NewConversationModel = "claude-opus-5-5";
        await WaitForSettingAsync(store, "default_model", "claude-opus-5-5");
        preferences.NewConversationEffort = "high";
        await WaitForSettingAsync(store, "default_effort", "high");
        preferences.SummarizerModel = "claude-haiku-4-5-20251001";
        await WaitForSettingAsync(store, TurnSummarizer.ModelSetting, "claude-haiku-4-5-20251001");
        Assert.Equal("claude-haiku-4-5-20251001", await new TurnSummarizer(store).GetModelAsync());
        Assert.Null(await new TurnSummarizer(store).GetEffortAsync());
        preferences.SummarizerEffort = "low";
        await WaitForSettingAsync(store, TurnSummarizer.EffortSetting, "low");
        Assert.Equal("low", await new TurnSummarizer(store).GetEffortAsync());

        // A blank prompt is refused; Cancel brings back the saved one.
        preferences.SystemPromptText = "  \n ";
        await preferences.SaveSystemPromptCommand.ExecuteAsync(null);
        Assert.StartsWith("The system prompt can't be empty.", preferences.SystemPromptMessage);
        preferences.CancelSystemPromptCommand.Execute(null);
        Assert.False(preferences.HasSystemPromptChanges);
        preferences.SystemPromptText = "Be brief.\r\nUse British spelling.\n";
        Assert.True(preferences.SaveSystemPromptCommand.CanExecute(null));
        await preferences.SaveSystemPromptCommand.ExecuteAsync(null);
        Assert.Equal("Be brief.\nUse British spelling.", preferences.SystemPrompt);
        Assert.False(preferences.HasSystemPromptChanges);

        var reloaded = new ApplicationPreferencesViewModel(store);
        await reloaded.LoadAsync();
        Assert.Equal(claude, reloaded.ClaudePath);
        Assert.Equal(claude, reloaded.ClaudePathText);
        Assert.Equal("claude-opus-5-5", reloaded.NewConversationModel);
        Assert.Equal("high", reloaded.NewConversationEffortOrNull);
        Assert.Equal("claude-haiku-4-5-20251001", reloaded.SummarizerModel);
        Assert.Equal("low", reloaded.SummarizerEffortOrNull);
        Assert.Equal("Be brief.\nUse British spelling.", reloaded.SystemPrompt);

        // Reset fills the editor with the built-in prompt; saving it removes the setting.
        reloaded.ResetSystemPromptCommand.Execute(null);
        Assert.Equal(ClaudeCliCodingPolicy.DefaultSystemPrompt, reloaded.SystemPromptText);
        Assert.Equal("Be brief.\nUse British spelling.", reloaded.SystemPrompt);
        await reloaded.SaveSystemPromptCommand.ExecuteAsync(null);
        Assert.Equal(ClaudeCliCodingPolicy.DefaultSystemPrompt, reloaded.SystemPrompt);
        Assert.Null(await store.GetSettingAsync("system_prompt"));
        await reloaded.ResetClaudePathCommand.ExecuteAsync(null);
        Assert.Null(reloaded.ClaudePath);
        Assert.Equal("", reloaded.ClaudePathText);

        var project = await store.CreateProjectAsync("Project", _directory);
        var conversation = await store.CreateConversationAsync(project.Id, "Conversation", reloaded.NewConversationModel,
            reloaded.NewConversationEffortOrNull);
        Assert.Equal(("claude-opus-5-5", "high"), (conversation.Model, conversation.Effort));
        var other = await store.CreateConversationAsync(project.Id, "Other");
        Assert.Equal(((string?)null, (string?)null), (other.Model, other.Effort));
    }

    // The pickers save without being awaited.
    private static async Task WaitForSettingAsync(ConversationStore store, string key, string expected)
    {
        for (var i = 0; i < 100 && await store.GetSettingAsync(key) != expected; i++) await Task.Delay(20);
        Assert.Equal(expected, await store.GetSettingAsync(key));
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
