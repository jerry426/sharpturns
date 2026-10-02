using SharpTurns.App.Services;
using SharpTurns.App.ViewModels;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class SoundPreferencesViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-sounds-{Guid.NewGuid():N}");

    [Fact]
    public async Task SoundsAreSavedReloadedAndPlayedOnlyWhenEnabled()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var player = new RecordingPlayer();
        var preferences = new ApplicationPreferencesViewModel(store, player);
        await preferences.LoadAsync();
        var sounds = preferences.Sounds;
        Assert.Equal("Loaded sound preferences.", sounds.Status);
        Assert.Equal(SoundPreferences.Default, sounds.CurrentPreferences);

        sounds.PlayNotification(AppSoundNotificationKind.ConversationTurnFinished);
        sounds.PreviewCommandApprovalCommand.Execute(null);
        Assert.Equal([SoundNotificationPreference.ConversationTurnFinishedDefault.SystemSound,
            SoundNotificationPreference.CommandApprovalDisplayedDefault.SystemSound], player.Played);

        // A sound the player doesn't offer keeps the current one's default.
        sounds.ConversationTurnFinishedSound = "Nope.aiff";
        Assert.Equal(SoundNotificationPreference.ConversationTurnFinishedDefault.SystemSound, sounds.ConversationTurnFinishedSound);
        sounds.ConversationTurnFinishedSound = "Ping.aiff";
        sounds.CommandApprovalSoundEnabled = false;
        await sounds.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Saved sound preferences.", sounds.Status);

        // Unsaved edits apply at once; Reload brings back the saved choices.
        sounds.CommandApprovalSoundEnabled = true;
        await sounds.ReloadCommand.ExecuteAsync(null);
        Assert.False(sounds.CommandApprovalSoundEnabled);

        player.Played.Clear();
        var reloaded = new ApplicationPreferencesViewModel(store, player);
        await reloaded.LoadAsync();
        reloaded.Sounds.PlayNotification(AppSoundNotificationKind.ConversationTurnFinished);
        reloaded.Sounds.PlayNotification(AppSoundNotificationKind.CommandApprovalDisplayed);
        Assert.Equal(["Ping.aiff"], player.Played);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"conversationTurnFinished":{"enabled":"yes","systemSound":3}}""")]
    public void UnreadableSoundsKeepTheDefaults(string? json) =>
        Assert.Equal(SoundPreferences.Default, SoundPreferencesJson.Parse(json));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class RecordingPlayer : IAppSoundPlayer
    {
        public List<string> Played { get; } = [];

        public IReadOnlyList<string> SystemSoundFileNames => MacOsSystemSoundPlayer.AvailableSystemSoundFileNames;

        public string PlaybackDescription => "Test playback.";

        public void PlaySystemSound(string systemSoundFileName) => Played.Add(systemSoundFileName);
    }
}
