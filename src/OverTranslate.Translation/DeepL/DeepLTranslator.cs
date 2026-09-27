using System.Text.Json;

namespace OverTranslate.Translation.DeepL;

/// <summary>
/// 「DeepL」: the official API, with the user's own key, many texts per request.
/// </summary>
/// <remarks>
/// <para>The one engine here that is a published API rather than an endpoint borrowed from a web
/// page or an app, so its limits are the documented ones: at most 50 texts per request and a body
/// of at most 128 KiB. Texts go as repeated <c>text</c> fields in a form body, the way the
/// application always sent them, and the answers come back one per text, in order.</para>
///
/// <para>Built per key rather than once: the key is the user's, it changes when they edit it, and
/// it decides the host — a free-plan key ends in <c>:fx</c> and is only accepted on
/// <c>api-free.deepl.com</c>. There is nothing else to keep between calls.</para>
/// </remarks>
public sealed class DeepLTranslator(HttpClient http, string apiKey) : BatchTranslator(http)
{
    public override string Name => "DeepL";

    protected override int MaxItemsPerRequest => 50;

    /// <remarks>
    /// Kept well under 128 KiB for the worst case: form encoding writes a CJK character as nine
    /// bytes (<c>%E4%BD%A0</c>), and 10,000 of them is about 88 KiB.
    /// </remarks>
    protected override int MaxCharactersPerRequest => 10000;

    /// <remarks>
    /// As long as a request, because DeepL has none of the short per-text limits the free engines
    /// have and a text is best translated whole. Cutting at <see cref="TranslationRequestChunks.SafeMaxCharacters"/>
    /// would only put seams into paragraphs DeepL could have read in one piece.
    /// </remarks>
    protected override int MaxCharactersPerPiece => MaxCharactersPerRequest;

    /// <summary>The host a key belongs to.</summary>
    internal static string EndpointFor(string key) =>
        key.TrimEnd().EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate";

    protected override async Task<IReadOnlyList<TextTranslation?>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var fields = pieces.Select(piece => new KeyValuePair<string, string>("text", piece)).ToList();

        // Detected per text when no source is given, which a screen of mixed languages needs.
        if (sourceLanguage is not null)
            fields.Add(new("source_lang", LanguageCodes.ToDeepLSource(sourceLanguage)));
        fields.Add(new("target_lang", LanguageCodes.ToDeepLTarget(targetLanguage)));

        using var request = new HttpRequestMessage(HttpMethod.Post, EndpointFor(apiKey))
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {apiKey}");

        using var document = JsonDocument.Parse(await ReadAsync(request, cancellationToken));

        return document.RootElement.GetProperty("translations").EnumerateArray().Select(answer =>
            new TextTranslation(
                RequireString(answer.GetProperty("text")),
                answer.TryGetProperty("detected_source_language", out var detected) &&
                detected.ValueKind == JsonValueKind.String
                    ? LanguageCodes.FromDeepL(detected.GetString()!)
                    : "")).ToList();
    }
}
