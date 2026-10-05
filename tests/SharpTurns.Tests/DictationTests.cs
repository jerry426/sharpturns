using System.Net;
using System.Text.Json;
using System.Windows.Input;
using SharpTurns.App.Services;
using SharpTurns.App.Services.Dictation;
using SharpTurns.App.ViewModels;
using SharpTurns.Core.Persistence;
using Xunit;

namespace SharpTurns.Tests;

public sealed class DictationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"sharpturns-dictation-{Guid.NewGuid():N}");

    [Fact]
    public void ParseTranscriptReadsTextAndConfidence()
    {
        using var document = JsonDocument.Parse(
            """{"results":{"channels":[{"alternatives":[{"transcript":"Hello from dictation.","confidence":0.93}]}]}}""");

        var result = DeepgramBatchTranscriber.ParseTranscript(document.RootElement);

        Assert.Equal("Hello from dictation.", result.Text);
        Assert.Equal(0.93, result.Confidence, precision: 3);
    }

    [Fact]
    public void RecordersReadDeviceListsAndBuildArguments()
    {
        const string avFoundation = """
            [AVFoundation indev @ 0x123] AVFoundation video devices:
            [AVFoundation indev @ 0x123] [0] Camera
            [AVFoundation indev @ 0x123] AVFoundation audio devices:
            [AVFoundation indev @ 0x123] [0] BoomAudio
            [AVFoundation indev @ 0x123] [1] MacBook Pro Microphone
            """;
        Assert.Equal([new("0", "BoomAudio"), new("1", "MacBook Pro Microphone")],
            MacOsCommandLineAudioRecorder.ParseAvFoundationAudioDevices(avFoundation));
        Assert.Equal(":1", MacOsCommandLineAudioRecorder.NormalizeConfiguredAvFoundationAudioInput("1")?.InputDevice);
        Assert.Null(MacOsCommandLineAudioRecorder.NormalizeConfiguredAvFoundationAudioInput("audio=Microphone Array"));

        const string directShow = """
            [in#0 @ 000001b356040700] "Microphone Array (Intel® Smart Sound Technology)" (audio)
            [in#0 @ 000001b356040700]   Alternative name "@device_cm_{33D9A762}\wave_{9AB0479F}"
            """;
        Assert.Equal(["@device_cm_{33D9A762}\\wave_{9AB0479F}"], WindowsCommandLineAudioRecorder.ParseDirectShowAudioDevices(directShow));
        Assert.Equal(["-hide_banner", "-loglevel", "error", "-f", "dshow", "-i", "audio=Mic", "-ac", "1", "-ar", "16000",
            "-sample_fmt", "s16", "-y", "out.wav"], WindowsCommandLineAudioRecorder.BuildDirectShowArguments("Mic", "out.wav"));
        Assert.Equal(["--target", "alsa_input.test", "--rate", "16000", "--channels", "1", "--format", "s16", "--container", "wav",
            "out.wav"], LinuxCommandLineAudioRecorder.BuildPwRecordArguments("alsa_input.test", "out.wav"));
        Assert.Equal(["-q", "-t", "wav", "-f", "S16_LE", "-r", "16000", "-c", "1", "out.wav"],
            LinuxCommandLineAudioRecorder.BuildArecordArguments(null, "out.wav"));
    }

    [Theory]
    [InlineData("Existing", 8, "Existing dictated message", 25)]
    [InlineData("", 0, "dictated message", 16)]
    [InlineData("Existing text", 9, "Existing dictated message text", 26)]
    [InlineData("Existing", 0, "dictated message Existing", 17)]
    [InlineData("Hello, world.", 12, "Hello, world dictated message.", 29)]
    public void TranscriptsGoInAtTheCaretWithSpacing(string text, int caret, string expectedText, int expectedCaret)
    {
        var history = new DictationComposerHistory(() => text, value => text = value, () => caret, value => caret = value);

        Assert.True(history.ApplyTranscript(" dictated message "));

        Assert.Equal(expectedText, text);
        Assert.Equal(expectedCaret, caret);
        Assert.Equal(1, history.UndoCount);
        Assert.False(history.ApplyTranscript("   "));
        Assert.Equal(1, history.UndoCount);
    }

    [Fact]
    public async Task OneRecordingAtATimeAndCancelStopsTheRecorder()
    {
        var recorder = new FakeRecorder();
        var service = new DeepgramDictationService(recorder, new FakeTranscriber());

        Assert.True(await service.StartBatchRecordingAsync());
        Assert.False(await service.StartBatchRecordingAsync());
        await service.CancelBatchRecordingAsync();

        Assert.False(service.IsBatchRecording);
        Assert.True(recorder.LastRecording!.IsDisposed);
        Assert.True(await service.StartBatchRecordingAsync());
    }

    [Fact]
    public async Task NoSpeechNamesTheMicrophone()
    {
        var recorder = new FakeRecorder { Audio = new(new byte[2000], "audio/wav", "MacBook Pro Microphone (AVFoundation audio device 1)") };
        var service = new DeepgramDictationService(recorder, new FakeTranscriber { Result = new("", 0) });

        await service.StartBatchRecordingAsync();

        Assert.Null(await service.StopBatchRecordingAsync());
        Assert.StartsWith("No speech detected from MacBook Pro Microphone", service.StatusMessage);
        Assert.Contains("SHARPTURNS_AUDIO_INPUT_DEVICE", service.StatusMessage);
    }

    [Fact]
    public async Task TheTranscriberSendsTheKeyAndATimeoutLeavesTheServiceReady()
    {
        string? authorization = null;
        string? contentType = null;
        using var client = new HttpClient(new Handler((request, _) =>
        {
            authorization = request.Headers.Authorization?.ToString();
            contentType = request.Content?.Headers.ContentType?.MediaType;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":{"channels":[{"alternatives":[{"transcript":"ok","confidence":0.9}]}]}}"""),
            });
        }));
        var result = await new DeepgramBatchTranscriber(() => " test-key ", client)
            .TranscribeAsync(new(new byte[2000], "audio/wav"));
        Assert.Equal(("ok", 0.9), (result.Text, result.Confidence));
        Assert.Equal("Token test-key", authorization);
        Assert.Equal("audio/wav", contentType);

        using var slow = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { Timeout = TimeSpan.FromMilliseconds(50) };
        var recorder = new FakeRecorder();
        var service = new DeepgramDictationService(recorder, new DeepgramBatchTranscriber("test-key", slow));
        await service.StartBatchRecordingAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => service.StopBatchRecordingAsync());
        Assert.False(service.IsProcessing);
        Assert.True(recorder.LastRecording!.IsDisposed);
        Assert.True(await service.StartBatchRecordingAsync());
    }

    [Fact]
    public async Task VoiceInsertsAtTheCaretAndUndoRestoresTheComposer()
    {
        var transcriber = new FakeTranscriber { Result = new("dictated message", 0.91) };
        var service = new DeepgramDictationService(new FakeRecorder(), transcriber);
        var (text, caret) = ("Existing text", 9);
        var dictation = new DictationViewModel(service, () => text, value => text = value, () => caret, value => caret = value);
        Assert.Equal(("🎤 Voice", "—", false), (dictation.VoiceButtonLabel, dictation.ConfidenceLabel, dictation.IsStatusVisible));

        await dictation.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.Equal(("🔴 Stop", "Recording…", true), (dictation.VoiceButtonLabel, dictation.StatusLabel, dictation.IsBusy));

        await dictation.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.Equal(("Existing dictated message text", 26), (text, caret));
        Assert.Equal(("🎤 Voice", "91%", "↩ Undo (1)"), (dictation.VoiceButtonLabel, dictation.ConfidenceLabel, dictation.UndoButtonLabel));
        Assert.Equal("Dictation inserted (91% confidence).", dictation.StatusLabel);
        Assert.False(dictation.IsBusy);

        dictation.UndoCommand.Execute(null);
        Assert.Equal(("Existing text", 9), (text, caret));
        Assert.False(dictation.UndoCommand.CanExecute(null));

        // A failed transcription keeps the composer and leaves Voice ready to try again.
        transcriber.Error = new TimeoutException("Deepgram transcription timed out. Please try again.");
        await dictation.ToggleRecordingCommand.ExecuteAsync(null);
        await dictation.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.Equal("Existing text", text);
        Assert.Equal("Dictation failed: Deepgram transcription timed out. Please try again.", dictation.StatusLabel);
        Assert.False(dictation.IsBusy);
        Assert.True(dictation.ToggleRecordingCommand.CanExecute(null));
    }

    [Fact]
    public async Task VoiceFollowsTheKeyAndAnotherComposerFindsTheMicrophoneInUse()
    {
        string? key = null;
        var service = new DeepgramDictationService(new FakeRecorder(), new DeepgramBatchTranscriber(() => key));
        var first = new DictationViewModel(service, () => "", _ => { }, () => 0, _ => { });
        var second = new DictationViewModel(service, () => "", _ => { }, () => 0, _ => { });
        Assert.False(first.ToggleRecordingCommand.CanExecute(null));
        Assert.Equal(DeepgramDictationService.NotConfiguredMessage, first.VoiceToolTip);

        key = "new-key";
        service.NotifyConfigurationChanged();
        Assert.True(first.ToggleRecordingCommand.CanExecute(null));
        await first.ToggleRecordingCommand.ExecuteAsync(null);
        await second.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.False(second.IsRecording);
        Assert.Equal("The microphone is already in use by another conversation.", second.StatusLabel);

        // Stop stays available after the key is cleared; closing the composer stops its recorder.
        key = null;
        service.NotifyConfigurationChanged();
        Assert.True(first.ToggleRecordingCommand.CanExecute(null));
        await first.CancelAsync();
        Assert.False(service.IsBatchRecording);
        Assert.False(first.ToggleRecordingCommand.CanExecute(null));
    }

    [Fact]
    public async Task SendWaitsForDictationAndClearsItsState()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync("Project", _directory);
        await store.CreateConversationAsync(project.Id, "Conversation", "claude-opus-5-5");
        var service = new DeepgramDictationService(new FakeRecorder(), new FakeTranscriber { Result = new("dictated", 0.8) });
        var main = new MainWindowViewModel(store, new ClaudeTurnRunner(store), new TurnSummarizer(store),
            new ApplicationPreferencesViewModel(store), service);
        await main.InitializeAsync();
        await WaitAsync(() => main.CurrentConversation is not null);
        var conversation = main.CurrentConversation!;
        conversation.ComposerText = "Typed";
        conversation.ComposerCaretIndex = 5;

        await conversation.Dictation.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.False(conversation.SendCommand.CanExecute(null));
        await conversation.Dictation.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.Equal("Typed dictated", conversation.ComposerText);
        Assert.True(conversation.SendCommand.CanExecute(null));

        conversation.ClearComposerCommand.Execute(null);
        Assert.False(conversation.Dictation.UndoCommand.CanExecute(null));

        // Closing the app stops a recording, so the recorder doesn't outlive it.
        await conversation.Dictation.ToggleRecordingCommand.ExecuteAsync(null);
        main.CancelRunningTurns();
        await WaitAsync(() => !service.IsBatchRecording);
    }

    [Fact]
    public async Task SwitchingConversationsWaitsForDictation()
    {
        Directory.CreateDirectory(_directory);
        var store = new ConversationStore(Path.Combine(_directory, "test.db"));
        await store.InitializeAsync();
        var project = await store.CreateProjectAsync("Project", _directory);
        var other = await store.CreateConversationAsync(project.Id, "Other", "claude-opus-5-5");
        await store.CreateConversationAsync(project.Id, "Conversation", "claude-opus-5-5");
        var service = new DeepgramDictationService(new FakeRecorder(), new FakeTranscriber { Result = new("dictated", 0.8) });
        var main = new MainWindowViewModel(store, new ClaudeTurnRunner(store), new TurnSummarizer(store),
            new ApplicationPreferencesViewModel(store), service);
        await main.InitializeAsync();
        await WaitAsync(() => main.CurrentConversation is not null);
        var conversation = main.CurrentConversation!;
        ICommand[] switching =
        [
            main.SelectConversationCommand, main.SelectProjectCommand, main.NewProjectCommand, main.DeleteProjectCommand,
            main.NewConversationCommand, main.EditConversationCommand, main.DeleteConversationCommand,
        ];
        Assert.All(switching, command => Assert.True(command.CanExecute(null)));

        await conversation.Dictation.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.True(main.IsDictationBusy);
        Assert.All(switching, command => Assert.False(command.CanExecute(null)));

        await conversation.Dictation.ToggleRecordingCommand.ExecuteAsync(null);
        Assert.False(main.IsDictationBusy);
        Assert.All(switching, command => Assert.True(command.CanExecute(null)));
        main.SelectConversationCommand.Execute(other);
        Assert.NotSame(conversation, main.CurrentConversation);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class FakeRecorder : IDictationAudioRecorder
    {
        public DictationAudioData Audio { get; init; } = new(new byte[2000], "audio/wav");

        public FakeRecording? LastRecording { get; private set; }

        public bool IsSupported => true;

        public string UnsupportedReason => "";

        public Task<IDictationAudioRecording> StartAsync(CancellationToken cancellationToken = default)
        {
            LastRecording = new FakeRecording(Audio);
            return Task.FromResult<IDictationAudioRecording>(LastRecording);
        }
    }

    private sealed class FakeRecording(DictationAudioData audio) : IDictationAudioRecording
    {
        public bool IsDisposed { get; private set; }

        public Task<DictationAudioData> StopAsync(CancellationToken cancellationToken = default) => Task.FromResult(audio);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTranscriber : IDeepgramTranscriber
    {
        public DictationResult Result { get; set; } = new("unused", 0.9);

        public Exception? Error { get; set; }

        public bool IsConfigured => true;

        public Task<DictationResult> TranscribeAsync(DictationAudioData audioData, CancellationToken cancellationToken = default) =>
            Error is null ? Task.FromResult(Result) : Task.FromException<DictationResult>(Error);
    }
}
