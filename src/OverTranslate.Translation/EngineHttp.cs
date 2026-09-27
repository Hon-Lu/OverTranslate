using System.Net;

namespace OverTranslate.Translation;

/// <summary>
/// The HTTP client every engine is given.
/// </summary>
/// <remarks>
/// <para>Offers HTTP/2 on every request. .NET sends HTTP/1.1 unless told otherwise, and over
/// HTTP/1.1 every request in flight at once needs a connection of its own: a screen for Bing is
/// twenty-odd simultaneous requests, and a hedged group is two copies of one. Over HTTP/2 they
/// share one connection per host, which is also how the browser the user agent names would send
/// them.</para>
///
/// <para>Not a fix for being blocked, and worth saying so because it once looked like one: a
/// "Sorry…" 429 from Google arrived on an HTTP/1.1 request and an HTTP/2 request right after it
/// succeeded, but alternating the two, sixteen in a row, then got sixteen successes. The 429 was
/// the endpoint limiting a burst of one-text requests — twenty-four sent at once still lose one
/// or two — which is the thing sending a screen as one request actually fixes
/// (<c>.ai/translation-service-analysis/implementation/</c>).</para>
///
/// <para>Applied in a handler rather than through <see cref="HttpClient.DefaultRequestVersion"/>
/// alone, because that default only reaches requests the client builds itself, and every engine
/// here builds its own.</para>
/// </remarks>
public static class EngineHttp
{
    /// <summary>What every request says it is, unless an engine has reason to say otherwise.</summary>
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";

    /// <param name="timeout">
    /// How long one request may take before it is abandoned. Engines never wait longer than this on
    /// their own; a caller that wants a shorter answer passes a cancellation token.
    /// </param>
    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var sockets = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,

            // Bing's credentials come with cookies the page expects back.
            UseCookies = true,
            CookieContainer = new CookieContainer(),

            // A pooled connection outliving a DNS change is how a long-running app ends up talking
            // to a host that has been retired.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };

        var client = new HttpClient(new PreferHttp2Handler(sockets))
        {
            Timeout = timeout,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>Offers HTTP/2 on requests that were built asking for less.</summary>
    internal sealed class PreferHttp2Handler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Version < HttpVersion.Version20)
            {
                request.Version = HttpVersion.Version20;

                // Or lower: a server that cannot do HTTP/2 still gets its request.
                request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
