using System.Net;

namespace OverTranslate.Engines;

/// <summary>
/// An engine failed, or answered with something that could not be read as a translation.
/// </summary>
/// <remarks>
/// The message never contains the text that was sent. What goes up is what the user had on
/// screen, and exceptions end up in logs and diagnostic bundles — the engine, the status and what
/// was wrong with the answer are enough to tell one failure from another.
/// </remarks>
public sealed class TranslationEngineException : Exception
{
    public TranslationEngineException(string engine, string message, HttpStatusCode? statusCode = null, Exception? inner = null)
        : base($"{engine}: {message}", inner)
    {
        Engine = engine;
        StatusCode = statusCode;
    }

    /// <summary>Which engine failed.</summary>
    public string Engine { get; }

    /// <summary>
    /// The HTTP status, when the failure was one. Bing reports its errors inside a 200 answer, so
    /// for Bing this is the status it wrote there rather than the one on the response.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }
}
