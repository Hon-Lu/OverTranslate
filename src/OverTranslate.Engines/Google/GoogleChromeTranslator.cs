using System.Net;
using System.Text;
using System.Text.Json;

namespace OverTranslate.Engines.Google;

/// <summary>
/// 「Google 翻譯 (Chrome)」: the endpoint behind the browser's own "translate this page".
/// </summary>
/// <remarks>
/// <para><c>translate-pa.googleapis.com/v1/translateHtml</c> is what translates a whole web page —
/// every text node of it in one request — so a list of texts is the shape it was built for. It
/// is a different model from the other two Google engines, not a faster route to the same one:
/// only 3 of 12 test sentences matched them, and its translations read noticeably less literal.
/// That is why it is an engine of its own and not a replacement for 「Google 翻譯 (Web)」 — a user
/// who picked that one would otherwise wake up to a different translator. See
/// <c>.ai/translation-service-analysis/google.md</c>.</para>
///
/// <para>Named for where it is met rather than for its protocol: nobody choosing an engine knows
/// what "translateHtml" is, and "the one Chrome uses" is a fair description of what they get. The
/// key is the public one the browser's translation script carries, not anybody's credential. The
/// request shape follows <c>isdzjfs/OverTranslate</c>'s <c>GoogleTranslateHtmlProvider</c>, which
/// found it; the code is this library's own.</para>
///
/// <para>Its input is HTML, and that has two consequences. Markup characters must be escaped or
/// 「Press &lt;A&gt;」 loses its button, and whitespace is whitespace: a line break inside a
/// paragraph is a space to it, and a blank line between two paragraphs disappears entirely. So
/// paragraphs are sent as separate pieces and put back with their blank line between them, and
/// lone line breaks are sent as the spaces the endpoint would read them as anyway.</para>
///
/// <para>Seen once and worth knowing: on the sentence that sends the other Google engines into a
/// repetition loop, this one does not loop — it cuts the last clause off instead, and a truncated
/// sentence looks like a finished one. Not something this side can detect.</para>
/// </remarks>
public sealed class GoogleChromeTranslator(HttpClient http) : BatchTranslator(http)
{
    private const string Endpoint = "https://translate-pa.googleapis.com/v1/translateHtml";
    private const string ApiKey = "AIzaSyATBXajvzQLTDHEQbcpq0Ihe0vWDHmO520";

    public override string Name => "Google (Chrome)";

    protected override int MaxItemsPerRequest => 50;

    /// <remarks>Counted before escaping; escaping only lengthens the few texts with markup in them.</remarks>
    protected override int MaxCharactersPerRequest => 5000;

    private protected override IReadOnlyList<TranslationRequestChunk> Split(string text) =>
        SplitParagraphs(text, joinLines: true);

    protected override async Task<IReadOnlyList<TextTranslation>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new object[]
        {
            new object[] { pieces.Select(Escape).ToArray(), sourceLanguage ?? "auto", targetLanguage },
            "wt_lib",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new("application/json+protobuf");
        request.Headers.TryAddWithoutValidation("X-Goog-API-Key", ApiKey);

        using var document = JsonDocument.Parse(await ReadAsync(request, cancellationToken));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 ||
            root[0].ValueKind != JsonValueKind.Array)
            throw new TranslationEngineException(Name, "unexpected answer");

        var translations = root[0];
        var languages = root.GetArrayLength() > 1 && root[1].ValueKind == JsonValueKind.Array &&
                        root[1].GetArrayLength() == translations.GetArrayLength()
            ? root[1]
            : default;

        var answers = new List<TextTranslation>(translations.GetArrayLength());
        for (var i = 0; i < translations.GetArrayLength(); i++)
        {
            var detected = languages.ValueKind == JsonValueKind.Array &&
                           languages[i].ValueKind == JsonValueKind.String
                ? LanguageCodes.FromGoogle(languages[i].GetString()!)
                : "";

            answers.Add(new TextTranslation(WebUtility.HtmlDecode(RequireString(translations[i])), detected));
        }

        return answers;
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");
}
