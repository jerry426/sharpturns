using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SharpTurns.App.Services;

namespace SharpTurns.App.ViewModels;

/// <summary>
/// A composer's Voice button, confidence box, Undo button, and dictation status line. Each conversation has one and all
/// share the app's service, so a transcript goes to the composer whose recording it was, and another composer reports
/// the microphone as in use.
/// </summary>
public sealed partial class DictationViewModel : ObservableObject
{
    private readonly DeepgramDictationService _service;
    private readonly DictationComposerHistory _composerHistory;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private Task? _operation;
    private Task? _cancelTask;
    private string _status;
    private bool _showStatus;
    private bool _ownsRecording;
    private bool _isCanceled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceButtonLabel), nameof(StatusLabel), nameof(IsStatusVisible), nameof(IsBusy))]
    private bool _isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceButtonLabel), nameof(StatusLabel), nameof(IsStatusVisible), nameof(IsBusy))]
    private bool _isProcessing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoButtonLabel))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private int _undoCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfidenceLabel))]
    private double _confidence;

    internal DictationViewModel(DeepgramDictationService service, Func<string> getComposerText, Action<string> setComposerText,
        Func<int> getComposerCaretIndex, Action<int> setComposerCaretIndex)
    {
        _service = service;
        _composerHistory = new DictationComposerHistory(getComposerText, setComposerText, getComposerCaretIndex,
            setComposerCaretIndex);
        _status = service.StatusMessage;
        _service.ConfigurationChanged += OnConfigurationChanged;
    }

    public bool IsAvailable => _service.IsAvailable;

    /// <summary>Recording or transcribing; sending waits, so a message can't go out before its transcript is inserted.</summary>
    public bool IsBusy => IsRecording || IsProcessing;

    public bool IsStatusVisible => IsRecording || IsProcessing || _showStatus;

    public string StatusLabel => IsRecording ? "Recording…"
        : IsProcessing ? "Transcribing…"
        : !string.IsNullOrWhiteSpace(_status) ? _status
        : _service.StatusMessage;

    public string VoiceButtonLabel => IsRecording ? "🔴 Stop" : IsProcessing ? "⏳ Voice" : "🎤 Voice";

    public string UndoButtonLabel => UndoCount > 0 ? $"↩ Undo ({UndoCount})" : "↩ Undo";

    public string ConfidenceLabel => Confidence > 0 ? FormatConfidence(Confidence) : "—";

    public string VoiceToolTip => IsAvailable
        ? "Record audio, then transcribe it into the message box (Ctrl+1 or ⌘+1)"
        : _service.StatusMessage;

    public void ClearStateAfterSend()
    {
        _composerHistory.Clear();
        UndoCount = _composerHistory.UndoCount;
        Confidence = 0.0;
        SetStatus(_service.StatusMessage, showStatus: false);
    }

    public void ClearUndoHistory()
    {
        _composerHistory.Clear();
        UndoCount = _composerHistory.UndoCount;
    }

    /// <summary>
    /// Stops for good: when the conversation is deleted or the app closes. A recording this composer owns is discarded
    /// and its recorder stopped. Never throws.
    /// </summary>
    public Task CancelAsync()
    {
        if (_cancelTask is not null) return _cancelTask;
        _service.ConfigurationChanged -= OnConfigurationChanged;
        _isCanceled = true;
        _lifetimeCancellation.Cancel();
        IsRecording = false;
        IsProcessing = false;
        ToggleRecordingCommand.NotifyCanExecuteChanged();
        _cancelTask = CancelCoreAsync();
        return _cancelTask;
    }

    private void OnConfigurationChanged()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(VoiceToolTip));
        ToggleRecordingCommand.NotifyCanExecuteChanged();
    }

    // Stop stays available while recording, even if the key was cleared meanwhile.
    private bool CanToggleRecording() => !_isCanceled && (IsRecording || IsAvailable) && (!IsProcessing || IsRecording);

    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private Task ToggleRecordingAsync()
    {
        var operation = ToggleRecordingCoreAsync();
        _operation = operation;
        return CompleteOperationAsync(operation);
    }

    private async Task ToggleRecordingCoreAsync()
    {
        try
        {
            if (_ownsRecording)
            {
                _ownsRecording = false;
                IsProcessing = true;
                IsRecording = false;
                SetStatus("Transcribing dictation…");
                ToggleRecordingCommand.NotifyCanExecuteChanged();

                var result = await _service.StopBatchRecordingAsync(_lifetimeCancellation.Token);
                IsProcessing = false;
                ToggleRecordingCommand.NotifyCanExecuteChanged();
                if (_isCanceled) return;

                if (result is not null) AppendTranscript(result);
                else SetStatus(_service.StatusMessage);
            }
            else
            {
                SetStatus("Preparing microphone…");
                ToggleRecordingCommand.NotifyCanExecuteChanged();

                if (!await _service.StartBatchRecordingAsync(_lifetimeCancellation.Token))
                {
                    SetStatus("The microphone is already in use by another conversation.");
                    ToggleRecordingCommand.NotifyCanExecuteChanged();
                    return;
                }

                _ownsRecording = true;
                if (_isCanceled) return;

                IsRecording = true;
                SetStatus(_service.StatusMessage, showStatus: false);
                ToggleRecordingCommand.NotifyCanExecuteChanged();
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _ownsRecording = false;
            IsRecording = false;
            IsProcessing = false;
            ToggleRecordingCommand.NotifyCanExecuteChanged();
            SetStatus(ex is OperationCanceledException
                ? "Dictation interrupted. Please try again."
                : $"Dictation failed: {ex.Message}");
        }
        finally
        {
            if (_isCanceled)
            {
                IsRecording = false;
                IsProcessing = false;
                ToggleRecordingCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private async Task CompleteOperationAsync(Task operation)
    {
        try
        {
            await operation;
        }
        finally
        {
            if (ReferenceEquals(_operation, operation)) _operation = null;
        }
    }

    private async Task CancelCoreAsync()
    {
        try
        {
            if (_operation is { } operation) await operation;
            if (_ownsRecording)
            {
                _ownsRecording = false;
                await _service.CancelBatchRecordingAsync();
            }
        }
        catch
        {
            // Closing must stay safe even if the recorder cleanup fails.
        }
        finally
        {
            _lifetimeCancellation.Dispose();
        }
    }

    private bool CanUndo() => UndoCount > 0;

    /// <summary>Restores the composer's text and caret from before the latest transcript.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!_composerHistory.Undo()) return;
        UndoCount = _composerHistory.UndoCount;
        SetStatus(_service.StatusMessage, showStatus: false);
    }

    private void AppendTranscript(DictationResult result)
    {
        if (!_composerHistory.ApplyTranscript(result.Text))
        {
            SetStatus("No speech detected.");
            return;
        }

        UndoCount = _composerHistory.UndoCount;
        Confidence = result.Confidence;
        SetStatus($"Dictation inserted ({FormatConfidence(result.Confidence)} confidence).");
    }

    private static string FormatConfidence(double confidence) =>
        $"{Math.Round(Math.Clamp(confidence, 0.0, 1.0) * 100, MidpointRounding.AwayFromZero):0}%";

    private void SetStatus(string status, bool showStatus = true)
    {
        var normalized = string.IsNullOrWhiteSpace(status) ? _service.StatusMessage : status;
        var changed = _status != normalized;
        _status = normalized;
        if (_showStatus != showStatus)
        {
            _showStatus = showStatus;
            OnPropertyChanged(nameof(IsStatusVisible));
        }
        if (changed) OnPropertyChanged(nameof(StatusLabel));
    }
}
