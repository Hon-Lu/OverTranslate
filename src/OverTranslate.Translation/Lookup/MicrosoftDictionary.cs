using System.Text;
using System.Text.Json;
using OverTranslate.Translation.Microsoft;
using static OverTranslate.Translation.Lookup.LookupJson;

namespace OverTranslate.Translation.Lookup;

/// <summary>
/// Microsoft's dictionary: the Translator API's <c>dictionary/lookup</c>, signed as
/// <see cref="MicrosoftTranslator"/> signs its requests.
/// </summary>
/// <remarks>
/// The request and the reading of the answer are GTranslate's
/// <c>MicrosoftTranslator.LookupDictionaryAsync</c> and <c>MicrosoftDictionaryParser</c>
/// (MIT, d4n3436/GTranslate), less its second request, <c>dictionary/examples</c>: example
/// sentences the dictionary card never showed. How to ask for them again is on
/// <see cref="DictionaryResult"/>.
/// </remarks>
public sealed class MicrosoftDictionary(HttpClient http) : DictionaryEngine(http)
{
    private const string Host = "api.cognitive.microsofttranslator.com";

    public override string Name => "MicrosoftDictionary";

    protected override async Task<DictionaryResult> QueryAsync(
        string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken)
    {
        var url = $"{Host}/dictionary/lookup?api-version=3.0" +
                  $"&from={LanguageCodes.ToMicrosoft(sourceLanguage)}&to={LanguageCodes.ToMicrosoft(targetLanguage)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + url)
        {
            Content = new StringContent(JsonSerializer.Serialize(new[] { new { Text = text } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-MT-Signature", MicrosoftTranslator.Sign(url, DateTimeOffset.UtcNow, Guid.NewGuid()));

        using var document = JsonDocument.Parse(await ReadStringAsync(request, cancellationToken));
        return MicrosoftLookupAnswer.Read(document.RootElement, text, withTransliteration: false);
    }
}

/// <summary>
/// Reads a <c>dictionary/lookup</c> answer, which Microsoft and Bing both give in the Translator
/// API's shape: a list with one answer per text sent.
/// </summary>
internal static class MicrosoftLookupAnswer
{
    /// <param name="withTransliteration">Whether to read <c>transliteration</c>, which Bing's answers carry.</param>
    public static DictionaryResult Read(JsonElement root, string text, bool withTransliteration)
    {
        if (root.ValueKind != JsonValueKind.Array)
            throw new FormatException($"expected an array, found {root.ValueKind}");
        if (root.GetArrayLength() == 0) return new DictionaryResult(text, null, []);

        var answer = root[0];
        var groups = Items(answer, "translations")
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
                    Items(item.Translation, "backTranslations")
                        .Select(back => OptionalString(back, "displayText") ?? OptionalString(back, "normalizedText"))
                        .OfType<string>()
                        .Distinct(StringComparer.Ordinal)
                        .ToList()))
                    .ToList()))
            .ToList();

        return new DictionaryResult(
            OptionalString(answer, "displaySource") ?? OptionalString(answer, "normalizedSource") ?? text, null, groups);
    }
}
