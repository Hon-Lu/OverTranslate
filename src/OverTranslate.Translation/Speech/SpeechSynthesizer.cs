using System.Diagnostics;
using NLog;

namespace OverTranslate.Translation.Speech;

/// <summary>Reads a text aloud with one service's engines, and with no one else's.</summary>
/// <remarks>
/// <para>The user picks the service, and a failure is said as that service's failure rather than
/// covered up by another voice (#436): a voice that changed during the day, by rules nobody saw,
/// was worse for the user and for whoever looked into a report than a message saying what went
/// wrong and where to change it.</para>
///
/// <para>A service can have several engines for the same voices — Google's two addresses. The next
/// is asked when the one before has no voice for the language or answered with something unusable,
/// one after another: a press of the speaker is one sound. Not when the one before got no answer at
/// all: the engines of one service share its host, and the next would only wait as long again.</para>
///
/// <para>Logs engines, languages, sizes and timings, never the text — the same rule as
/// <see cref="BatchTranslator"/>, for the same reason.</para>
/// </remarks>
/// <param name="service">The service's name, as the failures carry it.</param>
/// <param name="engines">The service's engines, in the order they are asked.</param>
public sealed class SpeechSynthesizer(string service, IReadOnlyList<ISpeechEngine> engines)
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <inheritdoc cref="SpeechSynthesizer(string, IReadOnlyList{ISpeechEngine})" path="/param[@name='service']"/>
    public string Service => service;

    /// <summary>Whether any of the service's engines has a voice for <paramref name="language"/>.</summary>
    public bool Supports(string language) => engines.Any(engine => engine.Supports(language));

    /// <returns>The speech as MP3, and the name of the engine that produced it.</returns>
    /// <exception cref="SpeechFailedException">The service has no voice for the language, or failed.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the request in flight is aborted.</exception>
    public async Task<(byte[] Audio, string Engine)> SynthesizeAsync(
        string text, string language, CancellationToken cancellationToken = default)
    {
        TranslationEngineException? last = null;

        foreach (var engine in engines.Where(engine => engine.Supports(language)))
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                var audio = await engine.SynthesizeAsync(text, language, cancellationToken);
                Log.Debug("朗讀：{Engine}，{Language}，{Characters} 字，{Bytes} bytes，{Elapsed} ms",
                    engine.Name, language, text.Length, audio.Length, Elapsed(started));
                return (audio, engine.Name);
            }
            catch (TranslationEngineException ex)
            {
                last = ex;
                var noAnswer = GotNoAnswer(ex);
                Log.Info("朗讀 {Engine} 失敗：{Language}，{Characters} 字，{Elapsed} ms，{Status}，{Reason}，{Next}",
                    engine.Name, language, text.Length, Elapsed(started),
                    ex.StatusCode is { } status ? (int)status : "-", ex.Message,
                    noAnswer ? "no answer, not trying the rest of the service" : "trying the next");
                if (noAnswer) break;
            }
        }

        if (last is null)
            throw new SpeechFailedException(service, SpeechFailure.Unsupported, $"{service}: no voice for {language}");
        throw new SpeechFailedException(service, Classify(last), last.Message, last);
    }

    /// <summary>
    /// A failure that never got a response: no connection, no handshake, or none before the
    /// timeout. One with a status, or an answer that could not be used, came from the service.
    /// </summary>
    internal static bool GotNoAnswer(TranslationEngineException ex) =>
        ex.StatusCode is null && ex.InnerException is HttpRequestException { StatusCode: null } or OperationCanceledException;

    /// <summary>Which of the failures the user is told apart <paramref name="ex"/> is.</summary>
    /// <remarks>
    /// Unreachable is the service never reached: the name not resolved, the connection or the
    /// handshake refused or not made within the connection timeout — nothing of the text sent yet.
    /// A connection that was made and then gave no answer in time is the service's own trouble.
    /// </remarks>
    internal static SpeechFailure Classify(TranslationEngineException ex)
    {
        if (ex.StatusCode is not null) return SpeechFailure.Unavailable;

        return ex.InnerException switch
        {
            HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.NameResolutionError
                or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError
                or HttpRequestError.ProxyTunnelError } => SpeechFailure.Unreachable,

            // SocketsHttpHandler's ConnectTimeout is a cancellation carrying a TimeoutException of
            // its own; HttpClient.Timeout's TimeoutException carries the cancellation of the request
            // that was under way.
            OperationCanceledException { InnerException: TimeoutException { InnerException: not OperationCanceledException } }
                => SpeechFailure.Unreachable,

            _ => SpeechFailure.Unavailable,
        };
    }

    private static long Elapsed(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
