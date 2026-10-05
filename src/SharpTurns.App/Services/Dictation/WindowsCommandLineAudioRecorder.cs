using System.Diagnostics;

namespace SharpTurns.App.Services.Dictation;

public sealed class WindowsCommandLineAudioRecorder : IDictationAudioRecorder
{
    private readonly string? _recorderPath;

    private WindowsCommandLineAudioRecorder(string? recorderPath)
    {
        _recorderPath = recorderPath;
    }

    public bool IsSupported => OperatingSystem.IsWindows() && !string.IsNullOrWhiteSpace(_recorderPath);

    public string UnsupportedReason => OperatingSystem.IsWindows()
        ? "Install ffmpeg to enable dictation audio capture, ensure ffmpeg.exe is on PATH, or set SHARPTURNS_AUDIO_RECORDER_PATH to ffmpeg.exe."
        : "Dictation audio capture on this platform uses a different recorder.";

    public static IDictationAudioRecorder CreateDefault()
    {
        var recorder = CreateWindowsRecorder();
        return recorder.IsSupported ? recorder : new UnsupportedDictationAudioRecorder(recorder.UnsupportedReason);
    }

    public async Task<IDictationAudioRecording> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported || _recorderPath is null)
        {
            throw new NotSupportedException(UnsupportedReason);
        }

        var path = RecorderProcess.CreateOutputPath();
        var recorderStartInfo = CreateFfmpegDirectShowStartInfo(_recorderPath, path);
        var process = RecorderProcess.Start(recorderStartInfo.ProcessStartInfo);
        await RecorderProcess.EnsureRecordingStartedAsync(process, path, cancellationToken).ConfigureAwait(false);
        return new CommandLineAudioRecording(process, path, recorderStartInfo.SourceDescription, RequestFfmpegStop);
    }

    public static IReadOnlyList<string> BuildDirectShowArguments(string inputDevice, string outputPath)
    {
        return
        [
            "-hide_banner",
            "-loglevel",
            "error",
            "-f",
            "dshow",
            "-i",
            $"audio={inputDevice}",
            "-ac",
            "1",
            "-ar",
            "16000",
            "-sample_fmt",
            "s16",
            "-y",
            outputPath,
        ];
    }

    public static IReadOnlyList<string> ParseDirectShowAudioDevices(string deviceListOutput)
    {
        return ParseDirectShowAudioDeviceDescriptors(deviceListOutput)
            .Select(device => device.InputName)
            .ToArray();
    }

    public static string? NormalizeConfiguredDirectShowAudioInput(
        string? configuredInputDevice,
        IReadOnlyList<string> availableInputDevices)
    {
        if (string.IsNullOrWhiteSpace(configuredInputDevice))
        {
            return null;
        }

        var normalized = configuredInputDevice.Trim();
        const string directShowAudioPrefix = "audio=";
        if (normalized.StartsWith(directShowAudioPrefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[directShowAudioPrefix.Length..].Trim();
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (availableInputDevices.Count == 0)
        {
            return normalized;
        }

        return availableInputDevices.Any(available => string.Equals(available, normalized, StringComparison.OrdinalIgnoreCase))
            ? normalized
            : null;
    }

    private static IReadOnlyList<DirectShowAudioDevice> ParseDirectShowAudioDeviceDescriptors(string deviceListOutput)
    {
        var devices = new List<DirectShowAudioDevice>();
        foreach (var rawLine in deviceListOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Contains("Alternative name", StringComparison.OrdinalIgnoreCase))
            {
                var alternativeName = ExtractQuotedValue(line);
                if (!string.IsNullOrWhiteSpace(alternativeName) && devices.Count > 0)
                {
                    var previous = devices[^1];
                    devices[^1] = previous with { InputName = alternativeName };
                }

                continue;
            }

            if (!line.EndsWith("(audio)", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var deviceName = ExtractQuotedValue(line);
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                devices.Add(new DirectShowAudioDevice(deviceName, deviceName));
            }
        }

        return devices;
    }

    private static string? ExtractQuotedValue(string line)
    {
        var firstQuote = line.IndexOf('"', StringComparison.Ordinal);
        if (firstQuote < 0)
        {
            return null;
        }

        var secondQuote = line.IndexOf('"', firstQuote + 1);
        return secondQuote <= firstQuote + 1
            ? null
            : line[(firstQuote + 1)..secondQuote];
    }

    private static WindowsCommandLineAudioRecorder CreateWindowsRecorder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new WindowsCommandLineAudioRecorder(null);
        }

        var configuredPath = RecorderProcess.ResolveConfiguredRecorderPath();
        if (configuredPath is not null)
        {
            return new WindowsCommandLineAudioRecorder(configuredPath);
        }

        var ffmpegPath = RecorderProcess.ResolveExecutablePath("ffmpeg.exe", []);
        return new WindowsCommandLineAudioRecorder(ffmpegPath);
    }

    private static RecorderStartInfo CreateFfmpegDirectShowStartInfo(string recorderPath, string outputPath)
    {
        var (inputDevice, sourceDescription) = ResolveDirectShowInputDevice(recorderPath);
        var startInfo = RecorderProcess.CreateBaseStartInfo(recorderPath);
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardError = true;
        foreach (var argument in BuildDirectShowArguments(inputDevice, outputPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        return new RecorderStartInfo(startInfo, sourceDescription);
    }

    private static (string InputDevice, string SourceDescription) ResolveDirectShowInputDevice(string recorderPath)
    {
        var configuredInputDevice = Environment.GetEnvironmentVariable("SHARPTURNS_AUDIO_INPUT_DEVICE")?.Trim();
        var devices = ListDirectShowAudioDevices(recorderPath);
        var availableConfiguredMatches = devices
            .Select(device => device.InputName)
            .Concat(devices.Select(device => device.FriendlyName))
            .ToArray();
        var normalizedConfiguredInputDevice = NormalizeConfiguredDirectShowAudioInput(
            configuredInputDevice,
            availableConfiguredMatches);
        if (!string.IsNullOrWhiteSpace(normalizedConfiguredInputDevice))
        {
            return (normalizedConfiguredInputDevice, $"configured Windows audio input {normalizedConfiguredInputDevice}");
        }

        var selected = devices.FirstOrDefault(device => device.FriendlyName.Contains("microphone", StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault();
        if (selected is not null)
        {
            return (selected.InputName, $"{selected.FriendlyName} (DirectShow audio device)");
        }

        throw new InvalidOperationException("FFmpeg DirectShow did not report any audio input devices. Run `ffmpeg -hide_banner -f dshow -list_devices true -i dummy` and set SHARPTURNS_AUDIO_INPUT_DEVICE to the microphone device name.");
    }

    private static IReadOnlyList<DirectShowAudioDevice> ListDirectShowAudioDevices(string recorderPath)
    {
        try
        {
            var startInfo = RecorderProcess.CreateBaseStartInfo(recorderPath);
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("dshow");
            startInfo.ArgumentList.Add("-list_devices");
            startInfo.ArgumentList.Add("true");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add("dummy");

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var stderr = process.StandardError.ReadToEnd();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return ParseDirectShowAudioDeviceDescriptors(stderr + Environment.NewLine + stdout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ffmpeg finishes the file when it reads "q" on standard input.
    private static void RequestFfmpegStop(Process process)
    {
        try
        {
            process.StandardInput.WriteLine("q");
            process.StandardInput.Flush();
        }
        catch (InvalidOperationException)
        {
        }
        catch (IOException)
        {
        }
    }

    private sealed record DirectShowAudioDevice(string FriendlyName, string InputName);
}
