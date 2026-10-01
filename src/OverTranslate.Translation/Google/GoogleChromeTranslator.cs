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
/// path with its key. Google documents none of it.</para>
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
/// <para>The key is not kept here. It is read from the translate element the first time it is
/// needed and handed to <c>saveKey</c> to keep, and read again only when Google refuses it — see
/// <see cref="GoogleChromeKeySource"/>. A key that keeps working is never looked up a second
/// time.</para>
///
/// <para>Seen once and worth knowing: on the sentence that sends the other Google engines into a
/// repetition loop, this one does not loop — it cuts the last clause off instead, and a truncated
/// sentence looks like a finished one. Not something this side can detect.</para>
/// </remarks>
/// <param name="loadKey">The key kept from last time, or empty. Without it, kept in memory.</param>
/// <param name="saveKey">Told when a new key has been read, to keep it for next time.</param>
public sealed class GoogleChromeTranslator(
    HttpClient http, Func<string>? loadKey = null, Action<string>? saveKey = null) : BatchTranslator(http)
{
    private const string Path = "/v1/translateHtml";

    // How long a failed or refused lookup is trusted before the scripts are asked again. Without it
    // a broken lookup would be 300 KB on every request, and realtime translation sends one every
    // few seconds; the option's backups answer in the meantime.
    internal static readonly TimeSpan LookupCooldown = TimeSpan.FromMinutes(10);

    // The main host, then one of the regional ones Chrome picks between by its data-region setting.
    // Same model, same answers; a second host is only a second way in when the first answers 5xx.
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private static readonly string[] Hosts = ["translate-pa.googleapis.com", "translate-pa.us.rep.googleapis.com"];

    private readonly SemaphoreSlim _lookup = new(1, 1);
    private string _key = "";
    private long _lastLookup = long.MinValue;

    public override string Name => "Google (Chrome)";

    protected override int MaxItemsPerRequest => 50;

    /// <remarks>Counted before escaping; escaping only lengthens the few texts with markup in them.</remarks>
    protected override int MaxCharactersPerRequest => 5000;

    private string StoredKey => loadKey?.Invoke() ?? _key;

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

        var key = StoredKey;
        if (key.Length == 0)
            key = await LookUpKeyAsync(refused: "", cancellationToken);

        var (status, body) = await PostAsync(payload, key, cancellationToken);
        if (IsKeyRefusal(status, body))
        {
            // Once, with whatever key the lookup turns up. A key that is refused as well fails the
            // request like any other refusal does.
            var fresh = await LookUpKeyAsync(refused: key, cancellationToken);
            if (fresh != key)
                (status, body) = await PostAsync(payload, fresh, cancellationToken);
        }

        if ((int)status is < 200 or > 299)
            throw new TranslationEngineException(Name, $"HTTP {(int)status}", status);

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

    /// <summary>
    /// Tells a refused key from every other refusal. A key Google does not know is a 400 that says
    /// so — <c>API_KEY_INVALID</c>, and <c>API_KEY_EXPIRED</c> by the same pattern — and a key that
    /// is real but not allowed this endpoint, or none at all, is a 403.
    /// </summary>
    /// <remarks>
    /// A 400 that does not name the key is the request's fault, not the key's, and looking the
    /// key up again would not change its answer.
    /// </remarks>
    internal static bool IsKeyRefusal(HttpStatusCode status, string body) =>
        status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized ||
        status == HttpStatusCode.BadRequest && body.Contains("API_KEY_", StringComparison.Ordinal);

    /// <summary>
    /// A key other than <paramref name="refused"/>: one another request has already looked up, or a
    /// new one from the translate element.
    /// </summary>
    /// <remarks>
    /// One lookup at a time, because a screen goes out as several requests at once and all of them
    /// are refused together; the first looks the key up and the rest find it waiting.
    /// </remarks>
    private async Task<string> LookUpKeyAsync(string refused, CancellationToken cancellationToken)
    {
        await _lookup.WaitAsync(cancellationToken);
        try
        {
            var stored = StoredKey;
            if (stored.Length > 0 && stored != refused)
                return stored;

            if (_lastLookup != long.MinValue &&
                System.Diagnostics.Stopwatch.GetElapsedTime(_lastLookup) < LookupCooldown)
            {
                return stored.Length > 0
                    ? stored
                    : throw new TranslationEngineException(Name, "no key; the last lookup failed");
            }

            _lastLookup = System.Diagnostics.Stopwatch.GetTimestamp();

            IReadOnlyList<string> found;
            try
            {
                found = await GoogleChromeKeySource.FetchAsync(Http, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Info(ex, "{Engine}：讀不到翻譯元件裡的金鑰", Name);
                return stored.Length > 0
                    ? stored
                    : throw new TranslationEngineException(Name, "no key; the lookup failed", inner: ex);
            }

            // The likeliest key that is not the one just refused. When that one is refused too,
            // the next lookup passes it over for the one after.
            var key = found.FirstOrDefault(k => k != refused) ?? found[0];
            if (key != stored)
            {
                Log.Info("{Engine}：已從翻譯元件取得金鑰", Name);
                _key = key;
                saveKey?.Invoke(key);
            }

            return key;
        }
        finally
        {
            _lookup.Release();
        }
    }

    /// <summary>
    /// Sends to the main host and, when it answers 5xx or cannot be reached, once more to the
    /// regional one. Any other answer is returned as it is, for the caller to read.
    /// </summary>
    private async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string payload, string key, CancellationToken cancellationToken)
    {
        try
        {
            var answer = await PostAsync(Hosts[0], payload, key, cancellationToken);
            if ((int)answer.Status < 500)
                return answer;

            LogRetry(answer.Status);
        }
        catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
        {
            LogRetry(ex.GetType().Name);
        }

        // About one request in sixteen is a 502 on some days and none on others. Trying the
        // same request on the regional host at once costs nothing when it works, and keeps the
        // answer in this engine's voice, which the fallback cannot.
        return await PostAsync(Hosts[1], payload, key, cancellationToken);

        // Debug, not Info: at one request in sixteen it would fill the log on a bad day, and
        // when the regional host fails too the failure is logged at Info anyway.
        void LogRetry(object status) =>
            Log.Debug("{Engine}：{Host} 回 {Status}，改試 {Regional}", Name, Hosts[0],
                status is HttpStatusCode code ? (int)code : status, Hosts[1]);
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(
        string host, string payload, string key, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}{Path}")
        {
            Content = new StringContent(payload, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = new("application/json+protobuf");
        request.Headers.TryAddWithoutValidation("X-Goog-API-Key", key);

        using var response = await Http.SendAsync(request, cancellationToken);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&#39;");
}
