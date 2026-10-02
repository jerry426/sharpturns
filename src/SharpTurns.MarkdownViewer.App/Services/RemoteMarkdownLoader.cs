using System.Net;
using SharpTurns.Markdown.Rendering;

namespace SharpTurns.MarkdownViewer.App.Services;

internal sealed class RemoteMarkdownLoader(HttpClient client)
{
    internal const int MaxDocumentBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 5;
    internal static RemoteMarkdownLoader Shared { get; } = new(new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    }));

    public async Task<(string Markdown, DateTimeOffset? ModifiedAt, ulong ByteSize)> LoadAsync(
        Uri uri, CancellationToken cancellationToken)
    {
        if (!MarkdownDocumentLink.IsRemoteMarkdown(uri))
        {
            throw new ArgumentException("An HTTP or HTTPS Markdown URL without credentials is required.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var target = uri;
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Accept.ParseAdd("text/markdown, text/plain, application/octet-stream;q=0.5");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (redirects >= MaxRedirects || response.Headers.Location is not { } location)
                {
                    throw new HttpRequestException("The Markdown URL returned too many redirects or a redirect without a destination.");
                }

                var next = new Uri(target, location);
                if ((next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) ||
                    !string.IsNullOrEmpty(next.UserInfo) ||
                    (target.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps))
                {
                    throw new HttpRequestException("The Markdown URL returned an unsafe redirect.");
                }

                target = next;
                continue;
            }

            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (mediaType is not (null or "text/markdown" or "text/x-markdown" or "text/plain" or "application/octet-stream"))
            {
                throw new HttpRequestException("The URL did not return a raw Markdown or plain-text document.");
            }

            // Bound the decompressed body, including chunked responses without Content-Length.
            await response.Content.LoadIntoBufferAsync(MaxDocumentBytes, timeout.Token);
            var markdown = await response.Content.ReadAsStringAsync(timeout.Token);
            if (markdown.TrimStart().StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
                markdown.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("The URL returned an HTML page instead of raw Markdown.");
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            return (markdown, response.Content.Headers.LastModified, (ulong)bytes.Length);
        }
    }
}
