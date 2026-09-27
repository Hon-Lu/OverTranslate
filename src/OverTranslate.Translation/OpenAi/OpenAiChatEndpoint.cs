namespace OverTranslate.Translation.OpenAi;

/// <summary>Where the chat completions of a server are.</summary>
public static class OpenAiChatEndpoint
{
    /// <summary>
    /// The chat-completions address for what a user typed: a base URL gets
    /// <c>/chat/completions</c> (and <c>/v1</c> when it has no path at all), and an address that
    /// already ends in it is used as it is.
    /// </summary>
    /// <returns>Null when it is not an absolute http or https address.</returns>
    public static Uri? Resolve(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = path;
            return builder.Uri;
        }

        if (path.Length == 0)
            path = "/v1";
        builder.Path = $"{path}/chat/completions";
        return builder.Uri;
    }
}
