namespace OverTranslate.Translation.Lookup;

/// <summary>An engine that looks a word up: one word in, its dictionary entry out.</summary>
/// <remarks>
/// <para>Like speech, nothing like <see cref="ITextTranslator"/> apart from the endpoints behind
/// it: a user asks about one word at a time, so there is nothing to pack into a request and no
/// answer to line up with its question. The order to ask engines in, and what to do when one has
/// nothing, depends on the target language and is the application's to decide.</para>
///
/// <para>Language codes are Google's, as everywhere in this library: <c>en</c>, <c>ja</c>,
/// <c>zh-CN</c>, <c>zh-TW</c>, <c>no</c>, <c>pt</c>. The source language is required — a
/// dictionary is for a language, and none of these endpoints look up in "whatever this is".</para>
/// </remarks>
public interface IDictionaryEngine
{
    /// <summary>A short, stable name for logs.</summary>
    string Name { get; }

    /// <returns>The entry, which has no groups when the engine has none for the word.</returns>
    /// <exception cref="TranslationEngineException">
    /// The engine failed or answered with something unusable — which includes a pair of languages it
    /// has no dictionary between: Microsoft and Bing refuse English to Traditional Chinese with a 400.
    /// </exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; requests in flight are aborted.</exception>
    Task<DictionaryResult> LookupAsync(
        string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken = default);
}
