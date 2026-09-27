using System.Text.Json;
using static OverTranslate.Translation.Lookup.LookupJson;

namespace OverTranslate.Translation.Lookup;

/// <summary>
/// Google's dictionary: <c>translate_a/single</c>, the one-text endpoint the Web engine's
/// <c>translate_a/t</c> is the sibling of, asked for its dictionary parts as well as the translation.
/// </summary>
/// <remarks>
/// <para>The request and the reading of the answer are GTranslate's
/// <c>GoogleTranslator.LookupDictionaryAsync</c> and <c>GoogleDictionaryParser</c>
/// (MIT, d4n3436/GTranslate), with one thing left out: the <c>tk</c> token. Measured 2026-09-27
/// with and without it, the answers were the same apart from the example sentences, which differ
/// between any two requests — Google picks a few of them each time.</para>
///
/// <para>The parts asked for: <c>bd</c> translations by part of speech, <c>at</c> other
/// translations of the whole text, <c>md</c> definitions, <c>ss</c> synonyms, <c>ex</c> example
/// sentences, and <c>t</c>, whose sentences carry the pronunciation.</para>
/// </remarks>
public sealed class GoogleDictionary(HttpClient http) : DictionaryEngine(http)
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/single";

    public override string Name => "GoogleDictionary";

    protected override async Task<DictionaryResult> QueryAsync(
        string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken)
    {
        var url = $"{Endpoint}?client=gtx&sl={Uri.EscapeDataString(sourceLanguage)}&tl={Uri.EscapeDataString(targetLanguage)}" +
                  "&dt=t&dt=bd&dt=at&dt=ex&dt=md&dt=ss&dj=1&source=input";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]),
        };

        using var document = JsonDocument.Parse(await ReadStringAsync(request, cancellationToken));
        return Read(document.RootElement, text);
    }

    internal static DictionaryResult Read(JsonElement root, string text)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException($"expected an object, found {root.ValueKind}");

        var groups = Items(root, "dict").Select(group =>
        {
            var pos = OptionalString(group, "pos");

            var entries = Items(group, "entry")
                .Where(entry => OptionalString(entry, "word") is not null)
                .Select(entry => new DictionaryEntry(
                    OptionalString(entry, "word")!,
                    null,
                    OptionalDouble(entry, "score"),
                    entry.TryGetProperty("frequency", out var frequency) && frequency.TryGetInt64(out var band) ? band : null,
                    Items(entry, "reverse_translation")
                        .Where(back => back.ValueKind == JsonValueKind.String)
                        .Select(back => back.GetString()!)
                        .ToList(),
                    []))
                .ToList();

            // Definitions and synonyms come in lists of their own, matched to a group by its part of speech.
            var definitions = SamePartOfSpeech(root, "definitions", pos)
                .SelectMany(definition => Items(definition, "entry"))
                .Select(entry => OptionalString(entry, "gloss"))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var synonyms = SamePartOfSpeech(root, "synsets", pos)
                .SelectMany(synset => Items(synset, "entry"))
                .SelectMany(entry => Items(entry, "synonym"))
                .Where(synonym => synonym.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(synonym.GetString()))
                .Select(synonym => synonym.GetString()!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new DictionaryGroup(pos, entries, definitions, synonyms);
        }).ToList();

        // The whole text's other translations, as one group of their own — only for a word that has
        // an entry, since for anything else they are just the translation again.
        if (groups.Count > 0)
        {
            var known = new HashSet<string>(groups.SelectMany(group => group.Entries).Select(entry => entry.Text), StringComparer.Ordinal);
            var alternatives = Items(root, "alternative_translations")
                .SelectMany(group => Items(group, "alternative"))
                .Select(alternative => (Word: OptionalString(alternative, "word_postproc"), Score: OptionalDouble(alternative, "score")))
                .Where(alternative => alternative.Word is not null && known.Add(alternative.Word))
                .Select(alternative => new DictionaryEntry(alternative.Word!, null, alternative.Score, null, [], []))
                .ToList();

            if (alternatives.Count > 0) groups.Add(new DictionaryGroup(null, alternatives, [], []));
        }

        var examples = root.TryGetProperty("examples", out var examplesObject)
            ? Items(examplesObject, "example")
                .Select(example => OptionalString(example, "text"))
                .OfType<string>()
                .Select(sentence => new DictionaryExample(sentence, null))
                .ToList()
            : [];

        var pronunciation = Items(root, "sentences")
            .Select(sentence => OptionalString(sentence, "src_translit"))
            .FirstOrDefault(reading => reading is not null);

        return new DictionaryResult(text, pronunciation, groups, examples);
    }

    private static IEnumerable<JsonElement> SamePartOfSpeech(JsonElement root, string property, string? pos) =>
        Items(root, property).Where(group =>
            string.Equals(OptionalString(group, "pos"), pos, StringComparison.OrdinalIgnoreCase));
}
