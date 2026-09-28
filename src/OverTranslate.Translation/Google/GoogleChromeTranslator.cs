using System.Net;
using System.Text;
using System.Text.Json;
using NLog;

namespace OverTranslate.Translation.Google;

/// <summary>
/// 「Google 翻譯 (Beta)」: the endpoint behind Chrome's own "translate this page".
/// </summary>
/// <remarks>
/// <para><c>translate-pa.googleapis.com/v1/translateHtml</c> is what translates a whole web page —
/// every text node of it in one request — so a list of texts is the shape it was built for. That
/// it is Chrome's is read from the code, not guessed: Chromium's <c>translate_script.cc</c> loads
/// Google's translate element (<c>translate_a/element.js</c>), and the element's script calls this
/// path with this key. Google documents none of it.</para>
///
/// <para>A different model from 「Google 翻譯 (標準)」, not a faster route to the same one: only 3 of
/// 12 test sentences matched, and over 80 reviewed ones its translations were better in 40 and
/// worse in 3. It is offered as Beta rather than as the default for how it fails when it does:
/// an unfamiliar name next to a familiar phrase can vanish — 「millsage*1st Single」 comes back
/// 「首支單曲」 — or be read as a word, which reads as a finished translation and cannot be told
/// from one on this side. See <c>.ai/translation-service-analysis/google-comparison.md</c>.</para>
///
/// <para>The request shape follows <c>isdzjfs/OverTranslate</c>'s <c>GoogleTranslateHtmlProvider</c>,
/// which found it; the code is this library's own.</para>
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
    private const string Path = "/v1/translateHtml";

    // The main host, then one of the regional ones Chrome picks between by its data-region setting.
    // Same model, same answers; a second host is only a second way in when the first answers 5xx.
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly string[] Hosts = ["translate-pa.googleapis.com", "translate-pa.us.rep.googleapis.com"];
    private const string ApiKey = "AIzaSyATBXajvzQLTDHEQbcpq0Ihe0vWDHmO520";

    public override string Name => "Google (Chrome)";

    protected override int MaxItemsPerRequest => 50;

    /// <remarks>Counted before escaping; escaping only lengthens the few texts with markup in them.</remarks>
    protected override int MaxCharactersPerRequest => 5000;

    private protected override IReadOnlyList<TranslationRequestChunk> Split(string text) =>
        SplitParagraphs(text, joinLines: true);

    protected override async Task<IReadOnlyList<TextTranslation?>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new object[]
        {
            new object[] { pieces.Select(Escape).ToArray(), sourceLanguage ?? "auto", targetLanguage },

            // What Chrome's own page translation sends: the translate element in library mode.
            // Other tags, and the request's two optional fields, were tried and changed nothing
            // except "ests", which translates only from English (google-comparison.md 9.3).
            "te_lib",
        });

        string body;
        try
        {
            body = await PostAsync(Hosts[0], payload, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsServerSide(ex))
        {
            // About one request in sixteen is a 502 on some days and none on others. Trying the
            // same request on the regional host at once costs nothing when it works, and keeps the
            // answer in this engine's voice, which the fallback cannot.
            //
            // Debug, not Info: at one request in sixteen it would fill the log on a bad day, and
            // when the regional host fails too the failure is logged at Info anyway.
            Log.Debug("{Engine}：{Host} 回 {Status}，改試 {Regional}", Name, Hosts[0],
                ex is TranslationEngineException { StatusCode: { } status } ? (int)status : ex.GetType().Name,
                Hosts[1]);
            body = await PostAsync(Hosts[1], payload, cancellationToken);
        }

        using var document = JsonDocument.Parse(body);
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

    private Task<string> PostAsync(string host, string payload, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{Path}")
        {
            Content = new StringContent(payload, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new("application/json+protobuf");
        request.Headers.TryAddWithoutValidation("X-Goog-API-Key", ApiKey);
        return SendDisposingAsync(request, cancellationToken);

        async Task<string> SendDisposingAsync(HttpRequestMessage message, CancellationToken token)
        {
            using (message) return await ReadAsync(message, token);
        }
    }

    private static bool IsServerSide(Exception ex) =>
        ex is HttpRequestException ||
        ex is TranslationEngineException { StatusCode: { } status } && (int)status >= 500;

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");
}
