using System.Net.Http.Headers;
using System.Text.Json;

namespace SharpTurns.App.Services.Dictation;

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
