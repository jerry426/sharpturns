using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharpTurns.App.Services;

// Batch dictation only, with no streaming and no IDictationService (the composer is the only user). The key comes from
// Config → API Keys. SHARPTURNS_AUDIO_RECORDER_PATH and SHARPTURNS_AUDIO_INPUT_DEVICE override the recorder path and
// input device.

public sealed record DictationResult(string Text, double Confidence);

public sealed record DictationAudioData(byte[] Content, string ContentType, string? SourceDescription = null);

public interface IDictationAudioRecorder
{
    bool IsSupported { get; }

    string UnsupportedReason { get; }

    Task<IDictationAudioRecording> StartAsync(CancellationToken cancellationToken = default);
}

public interface IDictationAudioRecording : IAsyncDisposable
{
    Task<DictationAudioData> StopAsync(CancellationToken cancellationToken = default);
}

public interface IDeepgramTranscriber
{
    bool IsConfigured { get; }

    Task<DictationResult> TranscribeAsync(DictationAudioData audioData, CancellationToken cancellationToken = default);
}

/// <summary>
/// Records from the microphone with a command-line recorder and transcribes the recording with Deepgram. One recording at
/// a time across the app; every conversation's composer shares this service.
/// </summary>
public sealed class DeepgramDictationService
{
    internal const string NotConfiguredMessage = "Save a Deepgram API key in Config → API Keys to enable dictation.";
    private readonly IDictationAudioRecorder _audioRecorder;
    private readonly IDeepgramTranscriber _transcriber;
    private readonly double _minimumConfidence;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IDictationAudioRecording? _batchRecording;
    private string _statusMessage;

    public DeepgramDictationService(
        IDictationAudioRecorder audioRecorder,
        IDeepgramTranscriber transcriber,
        double minimumConfidence = 0.45)
    {
        _audioRecorder = audioRecorder;
        _transcriber = transcriber;
        _minimumConfidence = Math.Clamp(minimumConfidence, 0.0, 1.0);
        _statusMessage = BuildInitialStatusMessage();
    }

    /// <summary>Raised on the UI thread after the Deepgram key is saved or cleared.</summary>
    public event Action? ConfigurationChanged;

    public void NotifyConfigurationChanged()
    {
        if (!IsBatchRecording && !IsProcessing)
        {
            _statusMessage = BuildInitialStatusMessage();
        }
        ConfigurationChanged?.Invoke();
    }

    public bool IsAvailable => IsConfigured && _audioRecorder.IsSupported;

    public bool IsConfigured => _transcriber.IsConfigured;

    public bool IsBatchRecording { get; private set; }

    public bool IsProcessing { get; private set; }

    public string StatusMessage => _statusMessage;

    public static DeepgramDictationService Create(Func<string?> apiKeyProvider)
    {
        var recorder = CommandLineDictationAudioRecorder.CreateDefault();
        var transcriber = new DeepgramBatchTranscriber(apiKeyProvider);
        return new DeepgramDictationService(recorder, transcriber);
    }

    public async Task<bool> StartBatchRecordingAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsAvailable)
            {
                _statusMessage = BuildInitialStatusMessage();
                throw new InvalidOperationException(_statusMessage);
            }

            if (IsBatchRecording || IsProcessing)
            {
                return false;
            }

            _batchRecording = await _audioRecorder.StartAsync(cancellationToken).ConfigureAwait(false);
            IsBatchRecording = true;
            _statusMessage = "Recording dictation… click Stop or press Ctrl+1 when finished.";
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DictationResult?> StopBatchRecordingAsync(CancellationToken cancellationToken = default)
    {
        IDictationAudioRecording? recording;
        // Always enter the short state-transition gate so cancellation cannot strand an owned recording.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsBatchRecording || _batchRecording is null)
            {
                return null;
            }

            recording = _batchRecording;
            _batchRecording = null;
            IsBatchRecording = false;
            IsProcessing = true;
            _statusMessage = "Transcribing dictation…";
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await using (recording.ConfigureAwait(false))
            {
                var audioData = await recording.StopAsync(cancellationToken).ConfigureAwait(false);
                if (audioData.Content.Length < 1000)
                {
                    _statusMessage = "Recording too short.";
                    return null;
                }

                var transcript = await _transcriber.TranscribeAsync(audioData, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(transcript.Text))
                {
                    _statusMessage = BuildNoSpeechStatus(audioData.SourceDescription);
                    return null;
                }

                if (transcript.Confidence < _minimumConfidence)
                {
                    _statusMessage = "No clear speech detected.";
                    return null;
                }

                _statusMessage = $"Dictation ready ({transcript.Confidence:P0} confidence).";
                return transcript;
            }
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public async Task CancelBatchRecordingAsync(CancellationToken cancellationToken = default)
    {
        IDictationAudioRecording? recording;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            recording = _batchRecording;
            _batchRecording = null;
            IsBatchRecording = false;
            _statusMessage = BuildInitialStatusMessage();
        }
        finally
        {
            _gate.Release();
        }

        if (recording is not null)
        {
            await recording.DisposeAsync().ConfigureAwait(false);
        }
    }

    private string BuildInitialStatusMessage()
    {
        if (!IsConfigured)
        {
            return NotConfiguredMessage;
        }

        if (!_audioRecorder.IsSupported)
        {
            return _audioRecorder.UnsupportedReason;
        }

        return "Dictation ready.";
    }

    private static string BuildNoSpeechStatus(string? sourceDescription)
    {
        var source = string.IsNullOrWhiteSpace(sourceDescription)
            ? "the selected microphone"
            : sourceDescription;
        return $"No speech detected from {source}. Check the microphone input, microphone permission, or set SHARPTURNS_AUDIO_INPUT_DEVICE.";
    }
}

public sealed class DeepgramBatchTranscriber : IDeepgramTranscriber
{
    private readonly HttpClient _httpClient;
    private readonly Func<string?> _apiKeyProvider;

    public DeepgramBatchTranscriber(string apiKey, HttpClient? httpClient = null)
        : this(() => apiKey, httpClient)
    {
    }

    public DeepgramBatchTranscriber(Func<string?> apiKeyProvider, HttpClient? httpClient = null)
    {
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ResolveApiKey());

    public async Task<DictationResult> TranscribeAsync(DictationAudioData audioData, CancellationToken cancellationToken = default)
    {
        var apiKey = ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Deepgram API key is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildRequestUri());
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);
        request.Content = new ByteArrayContent(audioData.Content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(audioData.ContentType);

        HttpResponseMessage response;
        try
        {
            // Includes upload, provider processing and buffered response download.
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Deepgram transcription timed out. Please try again.", ex);
        }
        using var responseLifetime = response;
        if (!response.IsSuccessStatusCode)
        {
            var snippet = await ReadResponseSnippetAsync(response, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Deepgram transcription failed: {(int)response.StatusCode} {response.ReasonPhrase}. {snippet}".Trim());
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseTranscript(document.RootElement);
    }

    private string? ResolveApiKey() => _apiKeyProvider()?.Trim();

    public static DictationResult ParseTranscript(JsonElement root)
    {
        try
        {
            var alternatives = root
                .GetProperty("results")
                .GetProperty("channels")[0]
                .GetProperty("alternatives");
            if (alternatives.GetArrayLength() == 0)
            {
                return new DictationResult(string.Empty, 0.0);
            }

            var alternative = alternatives[0];
            var transcript = alternative.TryGetProperty("transcript", out var transcriptElement)
                ? transcriptElement.GetString() ?? string.Empty
                : string.Empty;
            var confidence = alternative.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.TryGetDouble(out var parsedConfidence)
                ? parsedConfidence
                : 0.0;
            return new DictationResult(transcript, Math.Clamp(confidence, 0.0, 1.0));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new InvalidOperationException("Deepgram response did not include a transcript alternative.", ex);
        }
    }

    private static Uri BuildRequestUri() =>
        new("https://api.deepgram.com/v1/listen?model=nova-3&language=en-US&smart_format=true&punctuate=true");

    private static async Task<string> ReadResponseSnippetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        return body.Length <= 500 ? body : body[..500];
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

        var path = Path.Combine(Path.GetTempPath(), $"sharpturns-dictation-{Guid.NewGuid():N}.wav");
        var recorderStartInfo = CreateFfmpegDirectShowStartInfo(_recorderPath, path);
        var process = new Process { StartInfo = recorderStartInfo.ProcessStartInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start audio recorder.");
        }

        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        if (process.HasExited)
        {
            var error = await ReadRecorderErrorSnippetAsync(process).ConfigureAwait(false);
            var exitCode = process.ExitCode;
            process.Dispose();
            TryDeleteFile(path);
            throw new InvalidOperationException($"Audio recorder exited before recording started (exit code {exitCode}). {error}".Trim());
        }

        return new WindowsFfmpegAudioRecording(process, path, recorderStartInfo.SourceDescription);
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

        var configuredPath = Environment.GetEnvironmentVariable("SHARPTURNS_AUDIO_RECORDER_PATH")?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return new WindowsCommandLineAudioRecorder(configuredPath);
        }

        var ffmpegPath = ResolveExecutablePath("ffmpeg.exe");
        return new WindowsCommandLineAudioRecorder(ffmpegPath);
    }

    private static RecorderStartInfo CreateFfmpegDirectShowStartInfo(string recorderPath, string outputPath)
    {
        var (inputDevice, sourceDescription) = ResolveDirectShowInputDevice(recorderPath);
        var startInfo = CreateBaseStartInfo(recorderPath);
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
            var startInfo = CreateBaseStartInfo(recorderPath);
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

    private static ProcessStartInfo CreateBaseStartInfo(string recorderPath)
    {
        return new ProcessStartInfo(recorderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static string? ResolveExecutablePath(string executableName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<string> ReadRecorderErrorSnippetAsync(Process process)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stderr))
            {
                return string.Empty;
            }

            var normalized = stderr.Trim();
            const int maximumLength = 600;
            return normalized.Length <= maximumLength
                ? normalized
                : normalized[^maximumLength..];
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record DirectShowAudioDevice(string FriendlyName, string InputName);

    private readonly record struct RecorderStartInfo(ProcessStartInfo ProcessStartInfo, string? SourceDescription);

    private sealed class WindowsFfmpegAudioRecording : IDictationAudioRecording
    {
        private readonly Process _process;
        private readonly string _path;
        private readonly string? _sourceDescription;
        private bool _disposed;

        public WindowsFfmpegAudioRecording(Process process, string path, string? sourceDescription)
        {
            _process = process;
            _path = path;
            _sourceDescription = sourceDescription;
        }

        public async Task<DictationAudioData> StopAsync(CancellationToken cancellationToken = default)
        {
            if (!_process.HasExited)
            {
                TryRequestGracefulFfmpegStop();
                await WaitForExitOrKillAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!File.Exists(_path))
            {
                var error = await ReadRecorderErrorSnippetAsync(_process).ConfigureAwait(false);
                var source = string.IsNullOrWhiteSpace(_sourceDescription) ? "the selected microphone" : _sourceDescription;
                throw new InvalidOperationException($"Audio recorder did not create an output file for {source}. Check microphone permissions and SHARPTURNS_AUDIO_INPUT_DEVICE. {error}".Trim());
            }

            var content = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            return new DictationAudioData(content, "audio/wav", _sourceDescription);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _process.Dispose();
                TryDeleteFile(_path);
            }

            return ValueTask.CompletedTask;
        }

        private void TryRequestGracefulFfmpegStop()
        {
            try
            {
                _process.StandardInput.WriteLine("q");
                _process.StandardInput.Flush();
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
        }

        private async Task WaitForExitOrKillAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

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

        var path = Path.Combine(Path.GetTempPath(), $"sharpturns-dictation-{Guid.NewGuid():N}.wav");
        var configuredInputDevice = Environment.GetEnvironmentVariable("SHARPTURNS_AUDIO_INPUT_DEVICE")?.Trim();
        var startInfo = CreateStartInfo(_recorderPath, _recorderKind, configuredInputDevice, path);
        var process = new Process { StartInfo = startInfo.ProcessStartInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start audio recorder.");
        }

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            if (process.HasExited)
            {
                var error = await ReadRecorderErrorSnippetAsync(process).ConfigureAwait(false);
                var exitCode = process.ExitCode;
                throw new InvalidOperationException($"Audio recorder exited before recording started (exit code {exitCode}). Check microphone access and the selected input device. {error}".Trim());
            }

            return new LinuxAudioRecording(process, path, startInfo.SourceDescription);
        }
        catch
        {
            TryKillProcess(process);
            process.Dispose();
            TryDeleteFile(path);
            throw;
        }
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

        var configuredPath = Environment.GetEnvironmentVariable("SHARPTURNS_AUDIO_RECORDER_PATH")?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            return new LinuxCommandLineAudioRecorder(configuredPath, IdentifyRecorder(configuredPath));
        }

        var pwRecordPath = ResolveExecutablePath("pw-record", ["/usr/bin/pw-record", "/usr/local/bin/pw-record"]);
        if (pwRecordPath is not null)
        {
            return new LinuxCommandLineAudioRecorder(pwRecordPath, RecorderKind.PwRecord);
        }

        var arecordPath = ResolveExecutablePath("arecord", ["/usr/bin/arecord", "/usr/local/bin/arecord"]);
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
        var processStartInfo = new ProcessStartInfo(recorderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

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

    private static string? ResolveExecutablePath(string executableName, IReadOnlyList<string> fallbackPaths)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return fallbackPaths.FirstOrDefault(File.Exists);
    }

    private static async Task<string> ReadRecorderErrorSnippetAsync(Process process)
    {
        try
        {
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(stderr))
            {
                return string.Empty;
            }

            var normalized = stderr.Trim();
            const int maximumLength = 600;
            return normalized.Length <= maximumLength
                ? normalized
                : normalized[^maximumLength..];
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private enum RecorderKind
    {
        None,
        PwRecord,
        Arecord,
    }

    private readonly record struct RecorderStartInfo(ProcessStartInfo ProcessStartInfo, string? SourceDescription);

    private sealed class LinuxAudioRecording : IDictationAudioRecording
    {
        private const int SigInt = 2;
        private readonly Process _process;
        private readonly string _path;
        private readonly string? _sourceDescription;
        private bool _disposed;

        public LinuxAudioRecording(Process process, string path, string? sourceDescription)
        {
            _process = process;
            _path = path;
            _sourceDescription = sourceDescription;
        }

        public async Task<DictationAudioData> StopAsync(CancellationToken cancellationToken = default)
        {
            if (!_process.HasExited)
            {
                // Only the recorder this app started, by its own process ID, so it finishes writing the file.
                _ = SendSignal(_process.Id, SigInt);
                await WaitForExitOrKillAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!File.Exists(_path) || new FileInfo(_path).Length <= 44)
            {
                var error = await ReadRecorderErrorSnippetAsync(_process).ConfigureAwait(false);
                var source = string.IsNullOrWhiteSpace(_sourceDescription) ? "the selected microphone" : _sourceDescription;
                throw new InvalidOperationException($"Audio recorder did not capture audio from {source}. Check microphone access and SHARPTURNS_AUDIO_INPUT_DEVICE. {error}".Trim());
            }

            var content = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            return new DictationAudioData(content, "audio/wav", _sourceDescription);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _process.Dispose();
                TryDeleteFile(_path);
            }

            return ValueTask.CompletedTask;
        }

        private async Task WaitForExitOrKillAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int SendSignal(int pid, int sig);
    }
}

public sealed class MacOsCommandLineAudioRecorder : IDictationAudioRecorder
{
    private const string AfRecordPath = "/usr/bin/afrecord";
    private readonly string? _recorderPath;
    private readonly RecorderKind _recorderKind;
    private readonly record struct RecorderStartInfo(ProcessStartInfo ProcessStartInfo, string? SourceDescription);
    public readonly record struct AvFoundationAudioDevice(string Index, string Name);

    private MacOsCommandLineAudioRecorder(string? recorderPath, RecorderKind recorderKind)
    {
        _recorderPath = recorderPath;
        _recorderKind = recorderKind;
    }

    public bool IsSupported => OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(_recorderPath);

    public string UnsupportedReason => OperatingSystem.IsMacOS()
        ? "Install ffmpeg to enable dictation audio capture, or set SHARPTURNS_AUDIO_RECORDER_PATH to a recorder executable."
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

        var path = Path.Combine(Path.GetTempPath(), $"sharpturns-dictation-{Guid.NewGuid():N}.wav");
        var recorderStartInfo = _recorderKind switch
        {
            RecorderKind.FfmpegAvFoundation => CreateFfmpegStartInfo(_recorderPath, path),
            RecorderKind.AfRecord => CreateAfRecordStartInfo(_recorderPath, path),
            _ => throw new NotSupportedException(UnsupportedReason),
        };

        var process = new Process { StartInfo = recorderStartInfo.ProcessStartInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start audio recorder.");
        }

        return Task.FromResult<IDictationAudioRecording>(new CommandLineAudioRecording(process, path, recorderStartInfo.SourceDescription));
    }

    private static MacOsCommandLineAudioRecorder CreateMacOsRecorder()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return new MacOsCommandLineAudioRecorder(null, RecorderKind.None);
        }

        var configuredPath = Environment.GetEnvironmentVariable("SHARPTURNS_AUDIO_RECORDER_PATH")?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
        {
            var fileName = Path.GetFileName(configuredPath);
            var kind = fileName.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase)
                ? RecorderKind.FfmpegAvFoundation
                : RecorderKind.AfRecord;
            return new MacOsCommandLineAudioRecorder(configuredPath, kind);
        }

        var ffmpegPath = ResolveExecutablePath("ffmpeg", [
            "/opt/homebrew/bin/ffmpeg",
            "/usr/local/bin/ffmpeg",
            "/usr/bin/ffmpeg",
        ]);
        if (ffmpegPath is not null)
        {
            return new MacOsCommandLineAudioRecorder(ffmpegPath, RecorderKind.FfmpegAvFoundation);
        }

        if (File.Exists(AfRecordPath))
        {
            return new MacOsCommandLineAudioRecorder(AfRecordPath, RecorderKind.AfRecord);
        }

        return new MacOsCommandLineAudioRecorder(null, RecorderKind.None);
    }

    private static RecorderStartInfo CreateFfmpegStartInfo(string recorderPath, string outputPath)
    {
        var configuredInputDevice = Environment.GetEnvironmentVariable("SHARPTURNS_AUDIO_INPUT_DEVICE")?.Trim();
        var normalizedConfiguredInputDevice = NormalizeConfiguredAvFoundationAudioInput(configuredInputDevice);
        var (inputDevice, sourceDescription) = normalizedConfiguredInputDevice ?? ResolveDefaultAvFoundationAudioInput(recorderPath);

        var startInfo = CreateBaseStartInfo(recorderPath);
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

    private static RecorderStartInfo CreateAfRecordStartInfo(string recorderPath, string outputPath)
    {
        var startInfo = CreateBaseStartInfo(recorderPath);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("WAVE");
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add("LEI16@16000");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add(outputPath);
        return new RecorderStartInfo(startInfo, "afrecord default input");
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
            var startInfo = CreateBaseStartInfo(recorderPath);
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

    private static ProcessStartInfo CreateBaseStartInfo(string recorderPath)
    {
        return new ProcessStartInfo(recorderPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static string? ResolveExecutablePath(string executableName, IReadOnlyList<string> fallbackPaths)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return fallbackPaths.FirstOrDefault(File.Exists);
    }

    private enum RecorderKind
    {
        None,
        FfmpegAvFoundation,
        AfRecord,
    }

    private sealed class CommandLineAudioRecording : IDictationAudioRecording
    {
        private const int SigInt = 2;
        private readonly Process _process;
        private readonly string _path;
        private readonly string? _sourceDescription;
        private bool _disposed;

        public CommandLineAudioRecording(Process process, string path, string? sourceDescription)
        {
            _process = process;
            _path = path;
            _sourceDescription = sourceDescription;
        }

        public async Task<DictationAudioData> StopAsync(CancellationToken cancellationToken = default)
        {
            if (!_process.HasExited)
            {
                SendInterrupt(_process.Id);
                await WaitForExitOrKillAsync(cancellationToken).ConfigureAwait(false);
            }

            if (!File.Exists(_path))
            {
                throw new InvalidOperationException("Audio recorder did not create an output file. Check microphone permissions and SHARPTURNS_AUDIO_INPUT_DEVICE.");
            }

            var content = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            return new DictationAudioData(content, "audio/wav", _sourceDescription);
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            finally
            {
                _process.Dispose();
                TryDeleteFile(_path);
            }

            return ValueTask.CompletedTask;
        }

        private async Task WaitForExitOrKillAsync(CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }

                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        // Only the recorder this app started, by its own process ID, so it finishes writing the file.
        private static void SendInterrupt(int processId)
        {
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
            {
                _ = SendSignal(processId, SigInt);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int SendSignal(int pid, int sig);
    }
}
