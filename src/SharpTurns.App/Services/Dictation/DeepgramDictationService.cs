namespace SharpTurns.App.Services.Dictation;

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
