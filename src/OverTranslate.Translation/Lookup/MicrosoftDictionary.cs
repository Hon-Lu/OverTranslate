using System.Text;
using System.Text.Json;
using OverTranslate.Translation.Microsoft;
using static OverTranslate.Translation.Lookup.LookupJson;

namespace OverTranslate.Translation.Lookup;

/// <summary>
/// Microsoft's dictionary: the Translator API's <c>dictionary/lookup</c>, then
/// <c>dictionary/examples</c> for the translations it found, signed as
/// <see cref="MicrosoftTranslator"/> signs its requests.
/// </summary>
/// <remarks>
/// The requests and the reading of the answers are GTranslate's
/// <c>MicrosoftTranslator.LookupDictionaryAsync</c> and <c>MicrosoftDictionaryParser</c>
/// (MIT, d4n3436/GTranslate).
/// </remarks>
public sealed class MicrosoftDictionary(HttpClient http) : DictionaryEngine(http)
{
    private const string Host = "api.cognitive.microsofttranslator.com";

    // Examples are asked for in one request, for at most this many of the translations found.
    private const int MaxExampleRequests = 10;

    public override string Name => "MicrosoftDictionary";

    protected override async Task<DictionaryResult> QueryAsync(
        string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken)
    {
        var languages = $"&from={LanguageCodes.ToMicrosoft(sourceLanguage)}&to={LanguageCodes.ToMicrosoft(targetLanguage)}";

        using var lookup = JsonDocument.Parse(await PostAsync(
            $"{Host}/dictionary/lookup?api-version=3.0{languages}", new[] { new { Text = text } }, cancellationToken));
        var answer = MicrosoftLookupAnswer.First(lookup.RootElement);

        // One example request for every distinct translation, each paired with the word as the
        // engine normalised it.
        var pairs = MicrosoftLookupAnswer.Translations(answer)
            .Select(translation => new
            {
                Text = MicrosoftLookupAnswer.NormalizedSource(answer) ?? text,
                Translation = OptionalString(translation, "normalizedTarget") ?? OptionalString(translation, "displayTarget"),
            })
            .Where(pair => pair.Translation is not null)
            .DistinctBy(pair => pair.Translation, StringComparer.OrdinalIgnoreCase)
            .Take(MaxExampleRequests)
            .ToList();

        if (pairs.Count == 0) return MicrosoftLookupAnswer.Read(answer, text, examplesFor: null, withTransliteration: false);

        using var examples = JsonDocument.Parse(await PostAsync(
            $"{Host}/dictionary/examples?api-version=3.0{languages}", pairs, cancellationToken));
        var examplesRoot = examples.RootElement.Clone();

        return MicrosoftLookupAnswer.Read(answer, text, target => ExamplesFor(examplesRoot, target), withTransliteration: false);
    }

    private async Task<string> PostAsync<T>(string url, T body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-MT-Signature", MicrosoftTranslator.Sign(url, DateTimeOffset.UtcNow, Guid.NewGuid()));
        return await ReadStringAsync(request, cancellationToken);
    }

    /// <summary>The example sentences for one translation, source and translation each put back together.</summary>
    internal static IReadOnlyList<DictionaryExample> ExamplesFor(JsonElement root, string target) =>
        (root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Where(answer => string.Equals(OptionalString(answer, "normalizedTarget"), target, StringComparison.OrdinalIgnoreCase))
            .SelectMany(answer => Items(answer, "examples"))
            .Select(example => new DictionaryExample(
                string.Concat(Part(example, "sourcePrefix"), Part(example, "sourceTerm"), Part(example, "sourceSuffix")),
                string.Concat(Part(example, "targetPrefix"), Part(example, "targetTerm"), Part(example, "targetSuffix"))))
            .ToList();

    // Unlike OptionalString, keeps whitespace: the prefix of an example often ends in a space.
    private static string? Part(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>
/// Reads a <c>dictionary/lookup</c> answer, which Microsoft and Bing both give in the Translator
/// API's shape.
/// </summary>
internal static class MicrosoftLookupAnswer
{
    private static readonly JsonElement None = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>The answer for the one text sent; an empty object when there is none.</summary>
    public static JsonElement First(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new FormatException($"expected an array, found {root.ValueKind}");

        return root.GetArrayLength() > 0 ? root[0].Clone() : None;
    }

    public static string? NormalizedSource(JsonElement answer) => OptionalString(answer, "normalizedSource");

    public static IEnumerable<JsonElement> Translations(JsonElement answer) => Items(answer, "translations");

    /// <param name="examplesFor">The example sentences for a translation, by its normalised form; null for none.</param>
    /// <param name="withTransliteration">Whether to read <c>transliteration</c>, which Bing's answers carry.</param>
    public static DictionaryResult Read(
        JsonElement answer, string text, Func<string, IReadOnlyList<DictionaryExample>>? examplesFor, bool withTransliteration)
    {
        var groups = Translations(answer)
            .Select(translation => (
                Translation: translation,
                Text: OptionalString(translation, "displayTarget") ?? OptionalString(translation, "normalizedTarget")))
            .Where(item => item.Text is not null)
            .GroupBy(item => OptionalString(item.Translation, "posTag"), StringComparer.OrdinalIgnoreCase)
            .Select(group => new DictionaryGroup(
                group.Key,
                group.Select(item => new DictionaryEntry(
                    item.Text!,
                    withTransliteration ? OptionalString(item.Translation, "transliteration") : null,
                    OptionalDouble(item.Translation, "confidence"),
                    null,
                    Items(item.Translation, "backTranslations")
                        .Select(back => OptionalString(back, "displayText") ?? OptionalString(back, "normalizedText"))
                        .OfType<string>()
                        .Distinct(StringComparer.Ordinal)
                        .ToList(),
                    examplesFor?.Invoke(OptionalString(item.Translation, "normalizedTarget") ?? item.Text!) ?? []))
                    .ToList(),
                [],
                []))
            .ToList();

        return new DictionaryResult(
            OptionalString(answer, "displaySource") ?? NormalizedSource(answer) ?? text, null, groups, []);
    }
}
