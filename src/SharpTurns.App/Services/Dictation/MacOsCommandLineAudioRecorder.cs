using System.Diagnostics;

namespace SharpTurns.App.Services.Dictation;

public sealed class MacOsCommandLineAudioRecorder : IDictationAudioRecorder
{
    private readonly string? _recorderPath;
    public readonly record struct AvFoundationAudioDevice(string Index, string Name);

    private MacOsCommandLineAudioRecorder(string? recorderPath)
    {
        _recorderPath = recorderPath;
    }

    public bool IsSupported => OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(_recorderPath);

    public string UnsupportedReason => OperatingSystem.IsMacOS()
        ? "Install ffmpeg to enable dictation audio capture, or set SHARPTURNS_AUDIO_RECORDER_PATH to ffmpeg."
        : "Dictation audio capture currently supports macOS command-line recorders only.";

    public static IDictationAudioRecorder CreateDefault()
    {
        var recorder = CreateMacOsRecorder();
        return recorder.IsSupported ? recorder : new UnsupportedDictationAudioRecorder(recorder.UnsupportedReason);
    }

    public Task<IDictationAudioRecording> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported || _recorderPath is null)
        {
            throw new NotSupportedException(UnsupportedReason);
        }

        var path = RecorderProcess.CreateOutputPath();
        var recorderStartInfo = CreateFfmpegStartInfo(_recorderPath, path);
        var process = RecorderProcess.Start(recorderStartInfo.ProcessStartInfo);
        return Task.FromResult<IDictationAudioRecording>(
            new CommandLineAudioRecording(process, path, recorderStartInfo.SourceDescription, RecorderProcess.SendInterrupt));
    }

    private static MacOsCommandLineAudioRecorder CreateMacOsRecorder()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new MacOsCommandLineAudioRecorder(null);
        }

        var configuredPath = RecorderProcess.ResolveConfiguredRecorderPath();
        if (configuredPath is not null)
        {
            return new MacOsCommandLineAudioRecorder(configuredPath);
        }

        var ffmpegPath = RecorderProcess.ResolveExecutablePath("ffmpeg", [
            "/opt/homebrew/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
        ]);
        return new MacOsCommandLineAudioRecorder(ffmpegPath);
    }

    private static RecorderStartInfo CreateFfmpegStartInfo(string recorderPath, string outputPath)
    {
        var configuredInputDevice = Environment.GetEnvironmentVariable("SHARPTURNS_AUDIO_INPUT_DEVICE")?.Trim();
        var normalizedConfiguredInputDevice = NormalizeConfiguredAvFoundationAudioInput(configuredInputDevice);
        var (inputDevice, sourceDescription) = normalizedConfiguredInputDevice ?? ResolveDefaultAvFoundationAudioInput(recorderPath);

        var startInfo = RecorderProcess.CreateBaseStartInfo(recorderPath);
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("avfoundation");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(inputDevice);
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("16000");
        startInfo.ArgumentList.Add("-sample_fmt");
        startInfo.ArgumentList.Add("s16");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(outputPath);
        return new RecorderStartInfo(startInfo, sourceDescription);
    }

    private static (string InputDevice, string SourceDescription) ResolveDefaultAvFoundationAudioInput(string recorderPath)
    {
        var devices = ListAvFoundationAudioDevices(recorderPath);
        var selected = devices.FirstOrDefault(device => device.Name.Contains("microphone", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(selected.Index))
        {
            selected = devices.FirstOrDefault(device => !IsLikelyVirtualAudioDevice(device.Name));
        }

        if (!string.IsNullOrWhiteSpace(selected.Index))
        {
            return ($":{selected.Index}", $"{selected.Name} (AVFoundation audio device {selected.Index})");
        }

        return (":0", "AVFoundation audio device 0");
    }

    public static (string InputDevice, string SourceDescription)? NormalizeConfiguredAvFoundationAudioInput(string? configuredInputDevice)
    {
        if (string.IsNullOrWhiteSpace(configuredInputDevice))
        {
            return null;
        }

        configuredInputDevice = configuredInputDevice.Trim();
        if (IsDirectShowAudioInput(configuredInputDevice))
        {
            return null;
        }

        var inputDevice = configuredInputDevice.Contains(':', StringComparison.Ordinal)
            ? configuredInputDevice
            : $":{configuredInputDevice}";
        return (inputDevice, $"configured AVFoundation input {inputDevice}");
    }

    private static bool IsDirectShowAudioInput(string configuredInputDevice)
    {
        return configuredInputDevice.StartsWith("audio=", StringComparison.OrdinalIgnoreCase)
            || configuredInputDevice.StartsWith("@device_cm_", StringComparison.OrdinalIgnoreCase)
            || configuredInputDevice.Contains("\\wave_", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<AvFoundationAudioDevice> ListAvFoundationAudioDevices(string recorderPath)
    {
        try
        {
            var startInfo = RecorderProcess.CreateBaseStartInfo(recorderPath);
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("avfoundation");
            startInfo.ArgumentList.Add("-list_devices");
            startInfo.ArgumentList.Add("true");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(string.Empty);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return [];
            }

            var stderr = process.StandardError.ReadToEnd();
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return ParseAvFoundationAudioDevices(stderr + Environment.NewLine + stdout);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static IReadOnlyList<AvFoundationAudioDevice> ParseAvFoundationAudioDevices(string deviceListOutput)
    {
        var devices = new List<AvFoundationAudioDevice>();
        var inAudioSection = false;
        foreach (var rawLine in deviceListOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Contains("AVFoundation audio devices:", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = true;
                continue;
            }

            if (line.Contains("AVFoundation video devices:", StringComparison.OrdinalIgnoreCase))
            {
                inAudioSection = false;
                continue;
            }

            if (!inAudioSection)
            {
                continue;
            }

            var closePrefix = line.LastIndexOf("] [", StringComparison.Ordinal);
            if (closePrefix < 0)
            {
                continue;
            }

            var indexStart = closePrefix + 3;
            var indexEnd = line.IndexOf(']', indexStart);
            if (indexEnd <= indexStart || indexEnd + 1 >= line.Length)
            {
                continue;
            }

            var index = line[indexStart..indexEnd].Trim();
            var name = line[(indexEnd + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(index) && !string.IsNullOrWhiteSpace(name))
            {
                devices.Add(new AvFoundationAudioDevice(index, name));
            }
        }

        return devices;
    }

    private static bool IsLikelyVirtualAudioDevice(string name)
    {
        return name.Contains("boom", StringComparison.OrdinalIgnoreCase)
            || name.Contains("teams", StringComparison.OrdinalIgnoreCase)
            || name.Contains("blackhole", StringComparison.OrdinalIgnoreCase)
            || name.Contains("soundflower", StringComparison.OrdinalIgnoreCase);
    }
}
