using GTranslate.Translators;
using OverTranslate.Models;

namespace OverTranslate.Services.Providers;

/// <summary>
/// Rich dictionary lookups, which are all that is still asked of GTranslate here.
/// </summary>
/// <remarks>
/// Translation moved to <see cref="OverTranslate.Translation"/>, which can send a screen in one request
/// where GTranslate sent one request per line. Dictionary lookups are one word at a time by nature,
/// so they gained nothing from moving and were left where they work. They are the next thing to
/// move into <see cref="OverTranslate.Translation"/>; with speech moved into a project of its own,
/// GTranslate leaves the project entirely.
/// </remarks>
public class GTranslateDictionaryProvider(ITranslator translator)
{
    // Friendly engine name (e.g. "GoogleTranslator", "BingTranslator") for diagnostics/logging.
    public string Name => translator.Name;

    public bool SupportsDictionary => translator is IDictionaryTranslator;

    private static string DictionaryServiceDisplay(string service) => service switch
    {
        "GoogleTranslator"    => "Google Web",
        "BingTranslator"      => "Bing",
        "MicrosoftTranslator" => "Microsoft",
        _                     => service,
    };

    public async Task<DictionaryLookupData?> LookupDictionaryAsync(
        string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        if (translator is not IDictionaryTranslator dictionaryTranslator)
            return null;

        var fromCode = EngineLanguage.SourceToEngine(sourceLang);
        if (string.IsNullOrWhiteSpace(fromCode)) return null;

        var result = await dictionaryTranslator.LookupDictionaryAsync(
            text, EngineLanguage.ToEngine(targetLang), fromCode, cancellationToken);

        var mapped = new DictionaryLookupData(
            result.Source,
            DictionaryServiceDisplay(result.Service),
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
