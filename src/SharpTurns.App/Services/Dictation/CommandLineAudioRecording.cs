using System.Diagnostics;

namespace SharpTurns.App.Services.Dictation;

/// <summary>
/// A command-line recorder writing a WAV file. Stop asks the recorder to finish the file, then kills it after five
/// seconds; dispose kills the recorder and deletes the file.
/// </summary>
internal sealed class CommandLineAudioRecording : IDictationAudioRecording
{
    private const int WavHeaderLength = 44;
    private readonly Process _process;
    private readonly string _path;
    private readonly string? _sourceDescription;
    private readonly Action<Process> _requestStop;
    private readonly bool _rejectEmptyWav;
    private bool _disposed;

    /// <param name="requestStop">Asks the recorder to stop gracefully so it finishes writing the file.</param>
    /// <param name="rejectEmptyWav">Fails when the file holds only a WAV header, meaning no audio was captured.</param>
    public CommandLineAudioRecording(
        Process process,
        string path,
        string? sourceDescription,
        Action<Process> requestStop,
        bool rejectEmptyWav = false)
    {
        _process = process;
        _path = path;
        _sourceDescription = sourceDescription;
        _requestStop = requestStop;
        _rejectEmptyWav = rejectEmptyWav;
    }

    public async Task<DictationAudioData> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_process.HasExited)
        {
            _requestStop(_process);
            await WaitForExitOrKillAsync(cancellationToken).ConfigureAwait(false);
        }

        var source = string.IsNullOrWhiteSpace(_sourceDescription) ? "the selected microphone" : _sourceDescription;
        if (!File.Exists(_path))
        {
            var error = await RecorderProcess.ReadErrorSnippetAsync(_process).ConfigureAwait(false);
            throw new InvalidOperationException($"Audio recorder did not create an output file for {source}. Check microphone permissions and SHARPTURNS_AUDIO_INPUT_DEVICE. {error}".Trim());
        }

        if (_rejectEmptyWav && new FileInfo(_path).Length <= WavHeaderLength)
        {
            var error = await RecorderProcess.ReadErrorSnippetAsync(_process).ConfigureAwait(false);
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
            RecorderProcess.TryKill(_process);
        }
        finally
        {
            _process.Dispose();
            RecorderProcess.TryDeleteFile(_path);
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
}
