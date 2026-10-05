namespace SharpTurns.App.Services.Dictation;

public static class CommandLineDictationAudioRecorder
{
    public static IDictationAudioRecorder CreateDefault()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsCommandLineAudioRecorder.CreateDefault();
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacOsCommandLineAudioRecorder.CreateDefault();
        }

        if (OperatingSystem.IsLinux())
        {
            return LinuxCommandLineAudioRecorder.CreateDefault();
        }

        return new UnsupportedDictationAudioRecorder("Dictation audio recording is not supported on this platform.");
    }
}

public sealed class UnsupportedDictationAudioRecorder : IDictationAudioRecorder
{
    public UnsupportedDictationAudioRecorder(string unsupportedReason)
    {
        UnsupportedReason = string.IsNullOrWhiteSpace(unsupportedReason)
            ? "Dictation audio recording is not supported on this platform."
            : unsupportedReason;
    }

    public bool IsSupported => false;

    public string UnsupportedReason { get; }

    public Task<IDictationAudioRecording> StartAsync(CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(UnsupportedReason);
    }
}
