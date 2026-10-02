using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpTurns.App.Services;

// Copied from the Workbench. Only the JSON differs: SharpTurns saves the sounds as their own settings value instead of
// inside a shared preferences document.

public enum AppSoundNotificationKind
{
    ConversationTurnFinished,
    /// <summary>A permission or question dialog, as the Workbench's command approval and user input dialogs.</summary>
    CommandApprovalDisplayed,
}

public interface IAppSoundPlayer
{
    IReadOnlyList<string> SystemSoundFileNames { get; }

    string PlaybackDescription { get; }

    void PlaySystemSound(string systemSoundFileName);
}

public static class AppSoundPlayerFactory
{
    public static IAppSoundPlayer CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSystemSoundPlayer();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsSystemSoundPlayer();
        }

        return NullAppSoundPlayer.Instance;
    }
}

public sealed class NullAppSoundPlayer : IAppSoundPlayer
{
    public static NullAppSoundPlayer Instance { get; } = new();

    private NullAppSoundPlayer()
    {
    }

    public IReadOnlyList<string> SystemSoundFileNames => [];

    public string PlaybackDescription => "System notification sound playback is not configured for this platform. Missing files or playback errors are skipped without interrupting the app.";

    public void PlaySystemSound(string systemSoundFileName)
    {
    }
}

public sealed class MacOsSystemSoundPlayer : IAppSoundPlayer
{
    public const string SystemSoundDirectory = "/System/Library/Sounds";
    private const string AfplayPath = "/usr/bin/afplay";

    public static IReadOnlyList<string> AvailableSystemSoundFileNames { get; } =
    [
        "Basso.aiff",
        "Blow.aiff",
        "Bottle.aiff",
        "Frog.aiff",
        "Funk.aiff",
        "Glass.aiff",
        "Hero.aiff",
        "Morse.aiff",
        "Ping.aiff",
        "Pop.aiff",
        "Purr.aiff",
        "Sosumi.aiff",
        "Submarine.aiff",
        "Tink.aiff",
    ];

    public IReadOnlyList<string> SystemSoundFileNames => AvailableSystemSoundFileNames;

    public string PlaybackDescription => "Playback uses /System/Library/Sounds and /usr/bin/afplay on macOS. Missing files or playback errors are skipped without interrupting the app.";

    public void PlaySystemSound(string systemSoundFileName)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var safeFileName = Path.GetFileName(systemSoundFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeFileName)
            || !safeFileName.EndsWith(".aiff", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var soundPath = Path.Combine(SystemSoundDirectory, safeFileName);
        if (!File.Exists(soundPath) || !File.Exists(AfplayPath))
        {
            return;
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = AfplayPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { soundPath },
            });
        }
        catch
        {
            // Notification sounds are best-effort and should never interrupt the app.
        }
    }
}

public sealed class WindowsSystemSoundPlayer : IAppSoundPlayer
{
    public const string DefaultConversationTurnFinishedSound = "Windows Notify System Generic.wav";
    public const string DefaultCommandApprovalDisplayedSound = "Windows Exclamation.wav";

    private const int SoundAsync = 0x0001;
    private const int SoundNoDefault = 0x0002;
    private const int SoundFileName = 0x00020000;

    private static readonly Lazy<IReadOnlyList<string>> AvailableSystemSoundFileNamesLazy = new(LoadSystemSoundFileNames);

    public static string SystemSoundDirectory => Path.Combine(GetWindowsDirectory(), "Media");

    public static IReadOnlyList<string> AvailableSystemSoundFileNames => AvailableSystemSoundFileNamesLazy.Value;

    public IReadOnlyList<string> SystemSoundFileNames => AvailableSystemSoundFileNames;

    public string PlaybackDescription => "Playback uses Windows WAV files from the Windows Media folder. Missing files or playback errors are skipped without interrupting the app.";

    public void PlaySystemSound(string systemSoundFileName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var safeFileName = Path.GetFileName(systemSoundFileName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(safeFileName)
            || !safeFileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var soundPath = Path.Combine(SystemSoundDirectory, safeFileName);
        if (!File.Exists(soundPath))
        {
            return;
        }

        try
        {
            _ = PlaySound(soundPath, IntPtr.Zero, SoundFileName | SoundAsync | SoundNoDefault);
        }
        catch
        {
            // Notification sounds are best-effort and should never interrupt the app.
        }
    }

    private static IReadOnlyList<string> LoadSystemSoundFileNames()
    {
        try
        {
            if (Directory.Exists(SystemSoundDirectory))
            {
                var fileNames = Directory.EnumerateFiles(SystemSoundDirectory, "*.wav", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)
                    .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                    .Select(fileName => fileName!)
                    .OrderBy(fileName => fileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                if (fileNames.Length > 0)
                {
                    return fileNames;
                }
            }
        }
        catch
        {
            // Fall back to the common Windows notification sounds below.
        }

        return
        [
            DefaultConversationTurnFinishedSound,
            DefaultCommandApprovalDisplayedSound,
            "Windows Ding.wav",
            "Windows Notify.wav",
            "Windows Notify Email.wav",
            "Windows Notify Messaging.wav",
        ];
    }

    private static string GetWindowsDirectory()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.IsNullOrWhiteSpace(windowsDirectory) ? @"C:\Windows" : windowsDirectory;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PlaySound(string? pszSound, IntPtr hmod, int fdwSound);
}

public static class AppSoundDefaults
{
    public static SoundNotificationPreference ConversationTurnFinished { get; } = new(true, GetConversationTurnFinishedSystemSound());

    public static SoundNotificationPreference CommandApprovalDisplayed { get; } = new(true, GetCommandApprovalDisplayedSystemSound());

    private static string GetConversationTurnFinishedSystemSound()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsSystemSoundPlayer.DefaultConversationTurnFinishedSound;
        }

        return "Tink.aiff";
    }

    private static string GetCommandApprovalDisplayedSystemSound()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsSystemSoundPlayer.DefaultCommandApprovalDisplayedSound;
        }

        return "Basso.aiff";
    }
}

public sealed record SoundNotificationPreference(bool Enabled, string SystemSound)
{
    public static SoundNotificationPreference ConversationTurnFinishedDefault => AppSoundDefaults.ConversationTurnFinished;

    public static SoundNotificationPreference CommandApprovalDisplayedDefault => AppSoundDefaults.CommandApprovalDisplayed;

    public SoundNotificationPreference Normalize(SoundNotificationPreference fallback) =>
        string.IsNullOrWhiteSpace(SystemSound)
            ? this with { SystemSound = fallback.SystemSound }
            : this with { SystemSound = Path.GetFileName(SystemSound.Trim()) };
}

public sealed record SoundPreferences(
    SoundNotificationPreference ConversationTurnFinished,
    SoundNotificationPreference CommandApprovalDisplayed)
{
    public static SoundPreferences Default { get; } = new(
        SoundNotificationPreference.ConversationTurnFinishedDefault,
        SoundNotificationPreference.CommandApprovalDisplayedDefault);

    public SoundNotificationPreference Get(AppSoundNotificationKind kind) => kind switch
    {
        AppSoundNotificationKind.ConversationTurnFinished => ConversationTurnFinished,
        AppSoundNotificationKind.CommandApprovalDisplayed => CommandApprovalDisplayed,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported sound notification kind."),
    };
}

/// <summary>The sounds' settings value; anything missing or unreadable keeps its default.</summary>
public static class SoundPreferencesJson
{
    public static SoundPreferences Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return SoundPreferences.Default;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject sounds)
            {
                return SoundPreferences.Default;
            }

            return new SoundPreferences(
                ReadPreference(sounds, "conversationTurnFinished", SoundNotificationPreference.ConversationTurnFinishedDefault),
                ReadPreference(sounds, "commandApprovalDisplayed", SoundNotificationPreference.CommandApprovalDisplayedDefault));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return SoundPreferences.Default;
        }
    }

    public static string ToJson(SoundPreferences preferences) => new JsonObject
    {
        ["conversationTurnFinished"] = WritePreference(preferences.ConversationTurnFinished.Normalize(SoundNotificationPreference.ConversationTurnFinishedDefault)),
        ["commandApprovalDisplayed"] = WritePreference(preferences.CommandApprovalDisplayed.Normalize(SoundNotificationPreference.CommandApprovalDisplayedDefault)),
    }.ToJsonString();

    private static SoundNotificationPreference ReadPreference(
        JsonObject sounds,
        string propertyName,
        SoundNotificationPreference fallback)
    {
        var node = sounds[propertyName] as JsonObject;
        if (node is null)
        {
            return fallback;
        }

        var enabled = fallback.Enabled;
        if (node["enabled"] is JsonValue enabledValue && enabledValue.TryGetValue<bool>(out var parsedEnabled))
        {
            enabled = parsedEnabled;
        }

        var systemSound = node["systemSound"]?.GetValue<string>() ?? fallback.SystemSound;
        return new SoundNotificationPreference(enabled, systemSound).Normalize(fallback);
    }

    private static JsonObject WritePreference(SoundNotificationPreference preference) => new()
    {
        ["enabled"] = preference.Enabled,
        ["systemSound"] = preference.SystemSound,
    };
}
