namespace SharpTurns.Markdown.Rendering;

public static class MarkdownDocumentLink
{
    public static bool IsRemoteMarkdown(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.AbsolutePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
         uri.AbsolutePath.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));
}
