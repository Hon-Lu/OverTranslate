namespace OverTranslate.Translation.Speech;

/// <summary>Why a service could not read a text aloud, as far as the user is told.</summary>
public enum SpeechFailure
{
    /// <summary>The service was never reached: no name, no connection, no handshake in time.</summary>
    Unreachable,

    /// <summary>The service has no voice for the language; nothing was sent.</summary>
    Unsupported,

    /// <summary>
    /// The service was reached and did not read it: a refusal, a server error, no answer in time
    /// after connecting, or an answer that was not speech.
    /// </summary>
    Unavailable,
}

/// <summary>A service could not read a text aloud.</summary>
public sealed class SpeechFailedException(string service, SpeechFailure reason, string message, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>The service that failed, by the name <see cref="SpeechSynthesizer.Service"/> gives it.</summary>
    public string Service => service;

    public SpeechFailure Reason => reason;
}
