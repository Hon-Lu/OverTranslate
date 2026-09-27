namespace OverTranslate.Translation.Speech;

/// <summary>An engine that reads a text aloud: one text in, one MP3 out.</summary>
/// <remarks>
/// <para>Nothing like <see cref="ITextTranslator"/> apart from the endpoints behind it. There is
/// no list to pack, no group to fall back per, no answer to line up with its question: a user
/// presses the speaker for one text and waits for one sound. So none of the batching machinery
/// applies, and the fallback (<see cref="SpeechSynthesizer"/>) is a plain "next one, please".</para>
///
/// <para>Language codes are Google's, as everywhere in this library: <c>en</c>, <c>ja</c>,
/// <c>zh-CN</c>, <c>zh-TW</c>, <c>no</c>, <c>pt</c>.</para>
/// </remarks>
public interface ISpeechEngine
{
    /// <summary>A short, stable name for logs.</summary>
    string Name { get; }

    /// <summary>
    /// Whether this engine has a voice for <paramref name="language"/>. Asked before anything is
    /// sent, so a language it cannot speak goes to the next engine instead of being read aloud by
    /// whichever voice the engine falls back to — an English voice reading Swedish, as it used to.
    /// </summary>
    bool Supports(string language);

    /// <returns>The speech as MP3.</returns>
    /// <exception cref="TranslationEngineException">The engine failed or answered with something unusable.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; requests in flight are aborted.</exception>
    Task<byte[]> SynthesizeAsync(string text, string language, CancellationToken cancellationToken = default);
}
