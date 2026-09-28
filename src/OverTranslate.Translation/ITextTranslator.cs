namespace OverTranslate.Translation;

/// <summary>One text's translation, and the language the engine read it as.</summary>
/// <param name="Text">The translation, or the text itself when there was nothing to translate.</param>
/// <param name="DetectedLanguage">
/// In the vocabulary described on <see cref="ITextTranslator"/>, or empty when the engine did not
/// say — which is every engine whenever the source language was given rather than detected.
/// </param>
public sealed record TextTranslation(string Text, string DetectedLanguage)
{
    /// <summary>
    /// The engine answered, but the answer is known to leave part of the text in the original —
    /// and the attempt to put that right failed. Shown all the same, being the best there is, but
    /// not an answer to keep: a caller that caches should ask again instead.
    /// </summary>
    /// <remarks>
    /// Set only where it is known, never inferred from a translation that equals its text: names,
    /// numbers and <c>OK</c> translate to themselves, and asking again for those would never end.
    /// </remarks>
    public bool Untranslated { get; init; }
}

/// <summary>
/// An engine that translates a list of texts, one answer per text, in order.
/// </summary>
/// <remarks>
/// <para>A list rather than one text at a time because that is the whole reason this library
/// exists. A screen is twenty texts at once; sent as twenty requests, one of them failing is one
/// hole in the picture, and filling that hole from another engine is how a screen came to be
/// translated by two engines in two different voices. An engine that can take the twenty in one
/// request is asked once — see <c>.ai/translation-service-analysis/README.md</c>.</para>
///
/// <para>All or nothing: either every text comes back or the call throws. A partial answer would
/// put the question of which texts are missing on every caller, and the caller that has a use for
/// the distinction — the resilient wrapper in the application — gets it from
/// <see cref="Plan"/> instead, before anything is sent.</para>
///
/// <para>Language codes are written the way Google writes them, since that is the set the
/// application was already speaking through GTranslate: <c>en</c>, <c>ja</c>, <c>zh-CN</c>,
/// <c>zh-TW</c>, <c>no</c>, <c>pt</c>. Each engine translates them to its own on the way out
/// (Microsoft's <c>zh-Hant</c>, <c>nb</c>) and back again on the way in, so a detected language
/// means the same thing whichever engine reported it.</para>
/// </remarks>
public interface ITextTranslator
{
    /// <summary>A short, stable name for logs and for telling engines apart.</summary>
    string Name { get; }

    /// <summary>
    /// How <paramref name="texts"/> would be divided into requests, as lists of indices into it.
    /// </summary>
    /// <remarks>
    /// Each group is what one request carries, so it is also what one failure takes down. A caller
    /// that wants to retry or fall back does it per group: that is the smallest unit this engine
    /// can lose, and anything finer would only be pretending. Every index appears exactly once;
    /// texts with nothing to translate are placed like any other.
    /// </remarks>
    IReadOnlyList<IReadOnlyList<int>> Plan(IReadOnlyList<string> texts);

    /// <param name="targetLanguage">The language to translate into.</param>
    /// <param name="sourceLanguage">The language the texts are in, or null to have it detected.</param>
    /// <returns>One translation per text, in the order given.</returns>
    /// <exception cref="TranslationEngineException">The engine failed or answered with something unusable.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; requests in flight are aborted.</exception>
    Task<IReadOnlyList<TextTranslation>> TranslateAsync(
        IReadOnlyList<string> texts, string targetLanguage, string? sourceLanguage = null,
        CancellationToken cancellationToken = default);
}
