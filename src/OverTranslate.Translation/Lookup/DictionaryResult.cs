namespace OverTranslate.Translation.Lookup;

/// <summary>A word's dictionary entry, as one engine gave it.</summary>
/// <remarks>
/// <para>Only what the dictionary card shows. GTranslate's <c>DictionaryResult</c> (MIT,
/// d4n3436/GTranslate), which this was taken from, also carried definitions, synonyms, example
/// sentences, confidence and frequency; the card never showed any of them, so they are neither
/// asked for nor read. Should the card ever want them back, they are there to be asked for:</para>
/// <list type="bullet">
/// <item>Google — definitions <c>dt=md</c> (<c>definitions[].entry[].gloss</c>), synonyms
/// <c>dt=ss</c> (<c>synsets[].entry[].synonym[]</c>), both matched to a group by <c>pos</c>;
/// example sentences <c>dt=ex</c> (<c>examples.example[].text</c>, a few picked at random per
/// request, the word marked with <c>&lt;b&gt;</c>); the whole text's other translations
/// <c>dt=at</c>; and <c>score</c> and <c>frequency</c> on each <c>dict[].entry[]</c>.</item>
/// <item>Microsoft — example sentences from <c>dictionary/examples</c>, one more signed request
/// with <c>[{"Text":word,"Translation":translation}]</c> per translation found, each answer in
/// <c>source/target</c> <c>Prefix/Term/Suffix</c> pieces; and <c>confidence</c> on each
/// translation, as with Bing.</item>
/// </list>
/// </remarks>
/// <param name="Headword">The word the entry is for, as the engine wrote it; the text asked about when it did not say.</param>
/// <param name="Pronunciation">
/// How the word is read: in Latin letters from Google; from Youdao, the IPA for English and French
/// and the kana for Japanese.
/// </param>
/// <param name="Groups">Translations by part of speech, most likely first.</param>
public sealed record DictionaryResult(
    string Headword,
    string? Pronunciation,
    IReadOnlyList<DictionaryGroup> Groups);

/// <param name="PartOfSpeech">The engine's own word for it — Google writes <c>verb</c>, Microsoft and Bing <c>VERB</c>.</param>
public sealed record DictionaryGroup(
    string? PartOfSpeech,
    IReadOnlyList<DictionaryEntry> Entries);

/// <param name="Text">One translation of the word.</param>
/// <param name="Transliteration">The translation in Latin letters. Bing only.</param>
/// <param name="BackTranslations">Words in the source language this translation also stands for.</param>
public sealed record DictionaryEntry(
    string Text,
    string? Transliteration,
    IReadOnlyList<string> BackTranslations);
