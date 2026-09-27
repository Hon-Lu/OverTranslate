namespace OverTranslate.Translation.Lookup;

/// <summary>A word's dictionary entry, as one engine gave it.</summary>
/// <remarks>
/// The shape is GTranslate's <c>DictionaryResult</c> (MIT, d4n3436/GTranslate), which the
/// application's own dictionary model was written against; not every engine fills every part.
/// </remarks>
/// <param name="Headword">The word the entry is for, as the engine wrote it; the text asked about when it did not say.</param>
/// <param name="Pronunciation">How the word is read, in Latin letters. Google only.</param>
/// <param name="Groups">Translations by part of speech, most likely first.</param>
/// <param name="Examples">Example sentences for the word as a whole. Google only.</param>
public sealed record DictionaryResult(
    string Headword,
    string? Pronunciation,
    IReadOnlyList<DictionaryGroup> Groups,
    IReadOnlyList<DictionaryExample> Examples);

/// <param name="PartOfSpeech">
/// The engine's own word for it — Google writes <c>verb</c>, Microsoft and Bing <c>VERB</c>. Null
/// for Google's other translations of the whole text, which belong to no part of speech.
/// </param>
/// <param name="Definitions">What the word means, in the source language. Google only.</param>
/// <param name="Synonyms">Words that mean the same, in the source language. Google only.</param>
public sealed record DictionaryGroup(
    string? PartOfSpeech,
    IReadOnlyList<DictionaryEntry> Entries,
    IReadOnlyList<string> Definitions,
    IReadOnlyList<string> Synonyms);

/// <param name="Text">One translation of the word.</param>
/// <param name="Transliteration">The translation in Latin letters. Bing only.</param>
/// <param name="Confidence">How sure the engine is of this translation, 0 to 1.</param>
/// <param name="Frequency">Google's frequency band for the translation.</param>
/// <param name="BackTranslations">Words in the source language this translation also stands for.</param>
/// <param name="Examples">Example sentences with the word translated this way. Microsoft only.</param>
public sealed record DictionaryEntry(
    string Text,
    string? Transliteration,
    double? Confidence,
    long? Frequency,
    IReadOnlyList<string> BackTranslations,
    IReadOnlyList<DictionaryExample> Examples);

/// <param name="Source">The sentence. Google's mark the word with <c>&lt;b&gt;</c>, as it sends them.</param>
/// <param name="Translation">The sentence translated, when the engine gives one. Microsoft only.</param>
public sealed record DictionaryExample(string Source, string? Translation);
