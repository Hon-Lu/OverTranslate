namespace OverTranslate.Models;

// Only what the dictionary card shows. Definitions, synonyms, example sentences, confidence and
// frequency were carried here once and never shown, so they were dropped along with the requests
// for them; OverTranslate.Translation.Lookup.DictionaryResult notes where each one comes from.
public sealed record DictionaryLookupData(
    string Source,
    string Service,
    string? Headword,
    string? Pronunciation,
    IReadOnlyList<DictionaryLookupGroupData> Groups)
{
    public IReadOnlyList<DictionaryLookupGroupData> DisplayGroups => Groups
        .Where(group => group.HasPartOfSpeech && group.Entries.Count > 0)
        .ToArray();
    public bool HasContent => DisplayGroups.Count > 0;
    public bool HasHeadword => !string.IsNullOrWhiteSpace(Headword);
    public bool HasPronunciation => !string.IsNullOrWhiteSpace(Pronunciation);
}

public sealed record DictionaryLookupGroupData(
    string? PartOfSpeech,
    IReadOnlyList<DictionaryEntryData> Entries)
{
    public bool HasPartOfSpeech => !string.IsNullOrWhiteSpace(PartOfSpeech);
    public string PartOfSpeechLabel => string.IsNullOrWhiteSpace(PartOfSpeech)
        ? "—"
        : PartOfSpeech.ToUpperInvariant();
}

public sealed record DictionaryEntryData(
    string Text,
    string? Transliteration,
    IReadOnlyList<string> BackTranslations)
{
    public string BackTranslationsText => string.Join(" · ", BackTranslations);
    public bool HasTransliteration => !string.IsNullOrWhiteSpace(Transliteration);
}
