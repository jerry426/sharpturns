namespace SharpTurns.App.Services.Dictation;

public sealed class LinuxCommandLineAudioRecorder : IDictationAudioRecorder
{
    private readonly string? _recorderPath;
    private readonly RecorderKind _recorderKind;

    private LinuxCommandLineAudioRecorder(string? recorderPath, RecorderKind recorderKind)
    {
        _recorderPath = recorderPath;
        _recorderKind = recorderKind;
    }

    public bool IsSupported => OperatingSystem.IsLinux()
        && !string.IsNullOrWhiteSpace(_recorderPath)
        && _recorderKind != RecorderKind.None;

    public string UnsupportedReason => OperatingSystem.IsLinux()
        ? "Install PipeWire pw-record or ALSA arecord to enable dictation audio capture, or set SHARPTURNS_AUDIO_RECORDER_PATH to one of those executables."
        : "Dictation audio capture on this platform uses a different recorder.";

    public static IDictationAudioRecorder CreateDefault()
    {
        var recorder = CreateLinuxRecorder();
        return recorder.IsSupported ? recorder : new UnsupportedDictationAudioRecorder(recorder.UnsupportedReason);
    }

    public async Task<IDictationAudioRecording> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported || _recorderPath is null)
        {
            throw new NotSupportedException(UnsupportedReason);
        }

        var path = RecorderProcess.CreateOutputPath();
        var configuredInputDevice = Environment.GetEnvironmentVariable("SHARPTURNS_AUDIO_INPUT_DEVICE")?.Trim();
        var startInfo = CreateStartInfo(_recorderPath, _recorderKind, configuredInputDevice, path);
        var process = RecorderProcess.Start(startInfo.ProcessStartInfo);
        await RecorderProcess.EnsureRecordingStartedAsync(process, path, cancellationToken).ConfigureAwait(false);
        return new CommandLineAudioRecording(process, path, startInfo.SourceDescription, RecorderProcess.SendInterrupt,
            rejectEmptyWav: true);
    }

    public static IReadOnlyList<string> BuildPwRecordArguments(string? inputDevice, string outputPath)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(inputDevice))
        {
            arguments.Add("--target");
            arguments.Add(inputDevice.Trim());
        }

        arguments.Add("--rate");
        arguments.Add("16000");
        arguments.Add("--channels");
        arguments.Add("1");
        arguments.Add("--format");
        arguments.Add("s16");
        arguments.Add("--container");
        arguments.Add("wav");
        arguments.Add(outputPath);
        return arguments;
    }

    public static IReadOnlyList<string> BuildArecordArguments(string? inputDevice, string outputPath)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(inputDevice))
        {
            arguments.Add("-D");
            arguments.Add(inputDevice.Trim());
        }

        arguments.Add("-q");
        arguments.Add("-t");
        arguments.Add("wav");
        arguments.Add("-f");
        arguments.Add("S16_LE");
        arguments.Add("-r");
        arguments.Add("16000");
        arguments.Add("-c");
        arguments.Add("1");
        arguments.Add(outputPath);
        return arguments;
    }

    private static LinuxCommandLineAudioRecorder CreateLinuxRecorder()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new LinuxCommandLineAudioRecorder(null, RecorderKind.None);
        }

        var configuredPath = RecorderProcess.ResolveConfiguredRecorderPath();
        if (configuredPath is not null)
        {
            return new LinuxCommandLineAudioRecorder(configuredPath, IdentifyRecorder(configuredPath));
        }

        var pwRecordPath = RecorderProcess.ResolveExecutablePath("pw-record", ["/usr/bin/pw-record", "/usr/local/bin/pw-record"]);
        if (pwRecordPath is not null)
        {
            return new LinuxCommandLineAudioRecorder(pwRecordPath, RecorderKind.PwRecord);
        }

        var arecordPath = RecorderProcess.ResolveExecutablePath("arecord", ["/usr/bin/arecord", "/usr/local/bin/arecord"]);
        return arecordPath is null
            ? new LinuxCommandLineAudioRecorder(null, RecorderKind.None)
            : new LinuxCommandLineAudioRecorder(arecordPath, RecorderKind.Arecord);
    }

    private static RecorderKind IdentifyRecorder(string recorderPath)
    {
        var fileName = Path.GetFileName(recorderPath);
        if (fileName.StartsWith("pw-record", StringComparison.OrdinalIgnoreCase))
        {
            return RecorderKind.PwRecord;
        }

        return fileName.StartsWith("arecord", StringComparison.OrdinalIgnoreCase)
            ? RecorderKind.Arecord
            : RecorderKind.None;
    }

    private static RecorderStartInfo CreateStartInfo(
        string recorderPath,
        RecorderKind recorderKind,
        string? inputDevice,
        string outputPath)
    {
        var processStartInfo = RecorderProcess.CreateBaseStartInfo(recorderPath);
        processStartInfo.RedirectStandardError = true;

        var arguments = recorderKind switch
        {
            RecorderKind.PwRecord => BuildPwRecordArguments(inputDevice, outputPath),
            RecorderKind.Arecord => BuildArecordArguments(inputDevice, outputPath),
            _ => throw new NotSupportedException("The configured Linux audio recorder must be pw-record or arecord."),
        };
        foreach (var argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        var sourceDescription = recorderKind switch
        {
            RecorderKind.PwRecord when !string.IsNullOrWhiteSpace(inputDevice) => $"configured PipeWire input {inputDevice}",
            RecorderKind.PwRecord => "PipeWire default input",
            RecorderKind.Arecord when !string.IsNullOrWhiteSpace(inputDevice) => $"configured ALSA input {inputDevice}",
            RecorderKind.Arecord => "ALSA default input",
            _ => null,
        };
        return new RecorderStartInfo(processStartInfo, sourceDescription);
    }

    private enum RecorderKind
    {
        None,
        PwRecord,
        Arecord,
    }
}
