using System.Text.Json;

namespace OverTranslate.Translation.Google;

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

    /// <remarks>「角色名稱：艾莉絲 She has been waiting…」 into Chinese comes back as it was sent.</remarks>
    protected override bool RescuesMixedScript => true;

    protected override async Task<IReadOnlyList<TextTranslation?>> SendAsync(
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

        // Paired by position; a count that does not match is the base class's to refuse.
        return root.EnumerateArray()
            .Select((answer, i) => Read(answer, i < pieces.Count ? pieces[i] : ""))
            .ToList();

        static TextTranslation Read(JsonElement answer, string piece) => answer.ValueKind == JsonValueKind.Array
            ? new TextTranslation(
                RequireString(answer[0]),
                answer.GetArrayLength() > 1 && answer[1].ValueKind == JsonValueKind.String
                    ? Detected(LanguageCodes.FromGoogle(answer[1].GetString()!), piece)
                    : "")
            : new TextTranslation(RequireString(answer), "");
    }

    /// <summary>What the endpoint says the text was in, with the one thing it reliably says wrong put right.</summary>
    /// <remarks>
    /// <para>This endpoint reports traditional Chinese as <c>en</c> — every time, whatever the
    /// target: 「測試」, 「這個問題很難」, 「我們今天去學校」, while 「测试」 comes back <c>zh-CN</c>
    /// and 「東京」 <c>ja</c>. 「(RPC)」 and 「(Chrome)」 say <c>zh-TW</c> for the same texts
    /// (measured 2026-09-28; <c>translate_a/single</c>, which GTranslate used, says <c>en</c> as
    /// well). Believed, it turns 雙語互譯 round on every traditional Chinese word, and tells 文字翻譯
    /// the text it was given was English.</para>
    ///
    /// <para>English written with no Latin letter at all is not a thing, so that answer, beside Han
    /// characters and no kana, is taken to mean what the other two endpoints say. A text with a
    /// Latin letter in it keeps whatever it was given: there the answer may well be true.</para>
    /// </remarks>
    internal static string Detected(string reported, string piece) =>
        reported == "en" && piece.Any(IsHan) && !piece.Any(c => IsLatinLetter(c) || IsKana(c)) ? "zh-TW" : reported;

    private static bool IsHan(char c) =>
        c is >= '㐀' and <= '鿿'    // Han, with Extension A
            or >= '豈' and <= '﫿';   // compatibility ideographs

    private static bool IsKana(char c) => c is >= '぀' and <= 'ヿ';

    private static bool IsLatinLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
