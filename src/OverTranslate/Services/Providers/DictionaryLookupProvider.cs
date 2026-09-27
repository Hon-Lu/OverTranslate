using OverTranslate.Models;
using OverTranslate.Translation.Lookup;

namespace OverTranslate.Services.Providers;

/// <summary>
/// Rich dictionary lookups: one engine from <see cref="OverTranslate.Translation.Lookup"/>, spoken to
/// in the application's language codes and answering in its dictionary model.
/// </summary>
/// <param name="service">The name the dictionary card credits the entry to, e.g. "Google Web".</param>
public class DictionaryLookupProvider(IDictionaryEngine engine, string service)
{
    public string Name => engine.Name;

    public async Task<DictionaryLookupData?> LookupDictionaryAsync(
        string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        var fromCode = EngineLanguage.SourceToEngine(sourceLang);
        if (string.IsNullOrWhiteSpace(fromCode)) return null;

        var result = await engine.LookupAsync(text, EngineLanguage.ToEngine(targetLang), fromCode, cancellationToken);

        var mapped = new DictionaryLookupData(
            text,
            service,
            result.Headword,
            result.Pronunciation,
            result.Groups.Select(group => new DictionaryLookupGroupData(
                group.PartOfSpeech,
                group.Entries.Select(entry => new DictionaryEntryData(
                    entry.Text,
                    entry.Transliteration,
                    entry.Confidence,
                    entry.Frequency,
                    entry.BackTranslations,
                    entry.Examples.Select(example => new DictionaryExampleData(
                        example.Source, example.Translation)).ToList())).ToList(),
                group.Definitions,
                group.Synonyms)).ToList(),
            result.Examples.Select(example => new DictionaryExampleData(
                example.Source, example.Translation)).ToList());

        return mapped.HasContent ? mapped : null;
    }
}
