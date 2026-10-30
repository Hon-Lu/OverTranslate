using System.Net;
using System.Text;
using System.Text.Json;

namespace OverTranslate.Translation.Yandex;

/// <summary>
/// 「Yandex 翻譯」: the endpoint Yandex Browser translates pages with, many texts per request, no key.
/// </summary>
/// <remarks>
/// <para>Added for Russian speakers, for whom it is the engine of choice (issue #355). English into
/// Russian and back is answered by a language model — the answer says <c>"llm":true</c> — and every
/// other pair by Yandex's older machine translation, which is noticeably weaker: 「The sword is
/// broken.」 came back in Japanese as 「剣は破となりました。」 (measured 2026-10-30).</para>
///
/// <para>A list in, a list out, one answer per text, and a text is answered the same alone as in a
/// batch: sixteen menu labels and lines sent together and one by one came back word for word the
/// same, language model included. A request is refused as <c>413</c> past about 2,000 characters,
/// counted the same in Latin and in kana; the number of texts does not matter — two hundred went
/// through in 1.8 s. The budget below keeps a request near a second and a half.</para>
///
/// <para>The site's own endpoints are no use: <c>translate.yandex.com</c> answers a captcha and the
/// page-translation widget <c>429 limited</c>. The browser's needs no session — the <c>id</c> is any
/// 32 hex digits, so each instance makes its own.</para>
///
/// <para>Sent as HTML, because <c>format=text</c> is refused as <c>413</c> whatever its length; the
/// texts are escaped going out and unescaped coming back. A detected request is read as one
/// language — the first text's — and everything else in it is left as it was, so it is sent per
/// script (<see cref="ScriptGroups"/>), Cyrillic apart from Latin.</para>
/// </remarks>
public sealed class YandexTranslator(HttpClient http) : BatchTranslator(http)
{
    private const string Endpoint = "https://browser.translate.yandex.net/api/v1/tr.json/translate";

    private readonly string _id = $"{Guid.NewGuid():N}-0-0";

    public override string Name => "Yandex";

    protected override int MaxItemsPerRequest => 50;

    protected override int MaxCharactersPerRequest => 1000;

    /// <summary>Each paragraph its own piece, and the lines within it joined.</summary>
    /// <remarks>
    /// The language model keeps a line break and reads across it, but the older engine translates
    /// either side of one as a sentence of its own: 「おはよう⏎ございます」 into Russian came back as
    /// 「доброе утро.⏎да, сэр.」.
    /// </remarks>
    private protected override IReadOnlyList<TranslationRequestChunk> Split(string text) => SplitJoiningLines(text);

    /// <summary>
    /// Whether Yandex has the pair. Every language the application offers is one it translates from
    /// and into, except Traditional Chinese as a target: it has one Chinese, written simplified.
    /// </summary>
    /// <remarks>
    /// <c>zh-TW</c> and <c>zh-Hant</c> both answer <c>501</c>, "translation direction is not
    /// supported" (measured 2026-10-30). Refused here, so a backup writes Traditional Chinese rather
    /// than this engine writing Simplified.
    /// </remarks>
    internal static bool Supports(string? sourceLanguage, string targetLanguage) => targetLanguage != "zh-TW";

    protected override async Task<IReadOnlyList<TextTranslation?>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        if (!Supports(sourceLanguage, targetLanguage))
            throw new TranslationEngineException(Name, $"unsupported language pair ({sourceLanguage ?? "auto"} → {targetLanguage})");

        return sourceLanguage is null
            ? await ScriptGroups.SendAsync(Name, pieces,
                group => SendOneAsync(group, targetLanguage, null, cancellationToken), separateCyrillic: true)
            : await SendOneAsync(pieces, targetLanguage, sourceLanguage, cancellationToken);
    }

    private async Task<IReadOnlyList<TextTranslation?>> SendOneAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var target = LanguageCodes.ToYandex(targetLanguage);
        var lang = sourceLanguage is null ? target : $"{LanguageCodes.ToYandex(sourceLanguage)}-{target}";

        // Every text its own parameter, in a GET: the endpoint answers a POST with 405.
        var query = new StringBuilder($"{Endpoint}?id={_id}&srv=yabrowser&format=html&lang={Uri.EscapeDataString(lang)}");
        foreach (var piece in pieces)
            query.Append("&text=").Append(Uri.EscapeDataString(Escape(piece)));

        using var request = new HttpRequestMessage(HttpMethod.Get, query.ToString());
        using var document = JsonDocument.Parse(await ReadAsync(request, cancellationToken));
        var root = document.RootElement;

        // A refusal is a status of its own as well as this field, but read here for the cases that
        // come inside a 200 all the same.
        var code = root.TryGetProperty("code", out var status) && status.ValueKind == JsonValueKind.Number
            ? status.GetInt32()
            : 0;
        if (code != 200)
            throw new TranslationEngineException(Name, $"refused ({(code != 0 ? code : "no status")})");

        // "ja-ru": one language for the whole request, so every text in it was read as that.
        var detected = sourceLanguage is null &&
                       root.TryGetProperty("lang", out var pair) && pair.ValueKind == JsonValueKind.String &&
                       pair.GetString()! is var direction && direction.EndsWith($"-{target}", StringComparison.Ordinal)
            ? LanguageCodes.FromYandex(direction[..^(target.Length + 1)])
            : "";

        // Paired by position; a count that does not match is for the caller to refuse.
        return root.GetProperty("text").EnumerateArray()
            .Select(answer => (TextTranslation?)new TextTranslation(WebUtility.HtmlDecode(RequireString(answer)), detected))
            .ToList();
    }

    /// <summary>A text as HTML, so that what reads as markup in it is kept as text.</summary>
    /// <remarks>
    /// Only the three that can start or break markup: <see cref="WebUtility.HtmlEncode"/> would also
    /// turn every accented letter into a numeric reference, which is the text the engine reads.
    /// 「A &amp;lt; B」 comes back as it went (measured 2026-10-30).
    /// </remarks>
    internal static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
