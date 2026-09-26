using System.Text.Json;

namespace OverTranslate.Engines.Google;

/// <summary>
/// 「Google 翻譯 (Web)」: Google's lightweight translation endpoint, many texts per request.
/// </summary>
/// <remarks>
/// <para>GTranslate's <c>GoogleTranslator</c> used <c>translate_a/single</c>, which takes one text.
/// This is its sibling <c>translate_a/t</c> on the same host, which takes any number of <c>q</c>
/// fields and answers each in order. Same model: fourteen test sentences came back identical from
/// both, byte for byte, so a user who chose this engine sees the translations they already know —
/// only fewer requests behind them.</para>
///
/// <para>Measured 2026-09-27: 200 texts / 16,000 characters in one request was accepted and
/// answered in full, and a request every half second for a minute succeeded 119 times out of 119.
/// The budget below is far inside that, for the reason every budget here is: the endpoint is
/// undocumented, and one failed request now takes a whole screen with it.</para>
///
/// <para>Two answer shapes, decided by whether the source language was given: detected, each
/// answer is <c>["translation","ja"]</c>; given, each is just the string.</para>
/// </remarks>
public sealed class GoogleWebTranslator(HttpClient http) : BatchTranslator(http)
{
    private const string Endpoint = "https://translate.googleapis.com/translate_a/t";

    public override string Name => "Google (Web)";

    protected override int MaxItemsPerRequest => 50;

    protected override int MaxCharactersPerRequest => 5000;

    protected override async Task<IReadOnlyList<TextTranslation>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var url = $"{Endpoint}?client=gtx&sl={Uri.EscapeDataString(sourceLanguage ?? "auto")}" +
                  $"&tl={Uri.EscapeDataString(targetLanguage)}&format=text";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(pieces.Select(piece => new KeyValuePair<string, string>("q", piece))),
        };

        using var document = JsonDocument.Parse(await ReadAsync(request, cancellationToken));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new TranslationEngineException(Name, $"unexpected answer ({root.ValueKind})");

        return root.EnumerateArray().Select(Read).ToList();

        static TextTranslation Read(JsonElement answer) => answer.ValueKind == JsonValueKind.Array
            ? new TextTranslation(
                RequireString(answer[0]),
                answer.GetArrayLength() > 1 && answer[1].ValueKind == JsonValueKind.String
                    ? LanguageCodes.FromGoogle(answer[1].GetString()!)
                    : "")
            : new TextTranslation(RequireString(answer), "");
    }
}
