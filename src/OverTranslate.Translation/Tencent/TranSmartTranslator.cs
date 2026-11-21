using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;

namespace OverTranslate.Translation.Tencent;

/// <summary>
/// 「騰訊翻譯」: Tencent's TranSmart (騰訊交互翻譯), many texts per request, no key and no signature.
/// </summary>
/// <remarks>
/// <para>The endpoint the TranSmart site and browser extensions use (<c>/api/imt</c>),
/// added for users Google cannot reach. A list in, a list out, one answer per text — a line break
/// inside a text stays inside its answer. Measured 2026-10-08 (<c>.ai/translation-service-analysis/
/// youdao-transmart-handoff.md</c>): 4,250 characters in fifty texts was accepted and took 5.4 s,
/// 8,500 was refused as <c>outOfLimit</c>; time grows with the size of the request, so the budget
/// below keeps a request near two seconds — well inside the hedge — and lets a long screen go out
/// as requests side by side.</para>
///
/// <para>The <c>client_key</c> is what the site generates for a browser session; any key of the
/// same shape is accepted, so each instance makes its own.</para>
///
/// <para>A detected request is read as one language whatever is in it, so it is sent per script
/// (<see cref="ScriptGroups"/>).</para>
///
/// <para>Served from two hosts, tried in turn when one cannot be reached — see <see cref="Hosts"/>.</para>
/// </remarks>
public sealed class TranSmartTranslator(HttpClient http) : BatchTranslator(http)
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>The site's own host, then the one Tencent moved the site to.</summary>
    /// <remarks>
    /// <para>Measured 2026-11-21 (<c>.ai/endpoint-health/</c>): from Taiwan, and from anywhere public
    /// DNS answers with the overseas server, transmart.qq.com has served a certificate that expired
    /// on 2026-10-11, so every request failed the TLS handshake. Mainland China is answered with
    /// another server whose certificate was renewed. yi.qq.com is the same site — its page names
    /// itself TranSmart and posts to its own <c>/api/imt</c> — with the renewed certificate, and
    /// answers the same request the same way, in the same 0.8 s.</para>
    ///
    /// <para>So the original host first, which keeps mainland users on the server that has always
    /// worked for them, and the other only when a request cannot get through at all. Whichever
    /// answered is remembered for the rest of the run, not saved: either host may be fixed or
    /// retired without notice, and a fresh start asks the original again.</para>
    /// </remarks>
    internal static readonly string[] Hosts = ["transmart.qq.com", "yi.qq.com"];

    // Index into Hosts of the one that last answered.
    private int _host;

    /// <summary>How many times a request the server says it is too busy for is sent again.</summary>
    /// <remarks>1 in 22 requests answered <c>busy</c> when measured; the next one went through.</remarks>
    private const int BusyRetries = 2;

    private static readonly TimeSpan BusyDelay = TimeSpan.FromMilliseconds(300);

    private readonly string _clientKey =
        $"browser-chrome-138.0.0-Windows 10-{Guid.NewGuid()}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

    public override string Name => "TranSmart";

    protected override int MaxItemsPerRequest => 25;

    protected override int MaxCharactersPerRequest => 1000;

    /// <summary>Each paragraph its own piece, and the lines within it joined.</summary>
    /// <remarks>
    /// The endpoint keeps a line break inside a text, but translates the lines either side of it as
    /// sentences of their own: 「This sentence wraps⏎onto a second line.」 came back as
    /// 「這句話把⏎到第二行。」.
    /// </remarks>
    private protected override IReadOnlyList<TranslationRequestChunk> Split(string text) => SplitJoiningLines(text);

    /// <summary>The languages TranSmart translates from and into, in this library's codes.</summary>
    /// <remarks>
    /// Measured 2026-10-08 (from Taiwan): each code as a target from English and as a source into
    /// Chinese. Of the languages the application offers, Dutch, Polish, Czech, Danish, Greek,
    /// Estonian, Finnish, Hungarian, Indonesian, Lithuanian, Latvian, Norwegian, Romanian, Slovak,
    /// Slovenian, Swedish, Ukrainian and Bulgarian all answer <c>Unsupported-Language</c>; so do
    /// Malay and Hindi.
    /// </remarks>
    internal static readonly IReadOnlySet<string> Languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "zh-CN", "zh-TW", "en", "ja", "ko", "de", "fr", "es", "it", "pt", "ru", "tr", "ar", "vi", "th",
    };

    /// <summary>
    /// Whether TranSmart has the pair. A source that is not known yet — 自動 — is taken to be one it has;
    /// the endpoint says so itself when it is not.
    /// </summary>
    /// <remarks>
    /// Both languages in <see cref="Languages"/> is not quite enough: Russian and Thai translate into
    /// English, Japanese and Korean but not into either Chinese — those answer <c>error</c>, while
    /// Chinese into Russian or Thai works (measured 2026-10-08).
    /// </remarks>
    internal static bool Supports(string? sourceLanguage, string targetLanguage) =>
        Languages.Contains(targetLanguage) &&
        (sourceLanguage is null ||
         (Languages.Contains(sourceLanguage) &&
          !(sourceLanguage is "ru" or "th" && targetLanguage is "zh-CN" or "zh-TW")));

    protected override async Task<IReadOnlyList<TextTranslation?>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        // Refused here rather than by the endpoint, so the backups take over at once instead of after
        // two round trips that were always going to fail.
        if (!Supports(sourceLanguage, targetLanguage))
            throw new TranslationEngineException(Name, $"unsupported language pair ({sourceLanguage ?? "auto"} → {targetLanguage})");

        return sourceLanguage is null
            ? await ScriptGroups.SendAsync(Name, pieces,
                group => SendOneAsync(group, targetLanguage, null, cancellationToken))
            : await SendOneAsync(pieces, targetLanguage, sourceLanguage, cancellationToken);
    }

    private async Task<IReadOnlyList<TextTranslation?>> SendOneAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            header = new { fn = "auto_translation", client_key = _clientKey },
            type = "plain",
            model_category = "normal",
            source = new
            {
                text_list = pieces,
                lang = sourceLanguage is null ? "auto" : LanguageCodes.ToTranSmart(sourceLanguage),
            },
            target = new { lang = LanguageCodes.ToTranSmart(targetLanguage) },
        });

        for (var attempt = 0; ; attempt++)
        {
            using var document = JsonDocument.Parse(await PostAsync(payload, cancellationToken));
            var root = document.RootElement;

            var status = root.TryGetProperty("header", out var header) &&
                         header.TryGetProperty("ret_code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()!
                : "";

            if (status == "busy" && attempt < BusyRetries)
            {
                await Task.Delay(BusyDelay, cancellationToken);
                continue;
            }

            // Errors come inside a 200: "Unsupported-Language" for a pair it does not have,
            // "outOfLimit" for a request too long, "busy" now and then.
            if (status != "succ")
                throw new TranslationEngineException(Name, $"refused ({(status.Length > 0 ? status : "no status")})");

            // One language for the whole request, so every text in it was read as that.
            var detected = sourceLanguage is null &&
                           root.TryGetProperty("src_lang", out var language) && language.ValueKind == JsonValueKind.String
                ? LanguageCodes.FromTranSmart(language.GetString()!)
                : "";

            // Paired by position; a count that does not match is for the caller to refuse.
            return root.GetProperty("auto_translation").EnumerateArray()
                .Select((answer, i) => (TextTranslation?)new TextTranslation(
                    WithoutGloss(i < pieces.Count ? pieces[i] : "", RequireString(answer), targetLanguage), detected))
                .ToList();
        }
    }

    /// <summary>
    /// Sends to the host that last answered and, when it cannot be reached at all, once to the
    /// other. Any answer, refusals included, is returned as it is, for the caller to read.
    /// </summary>
    /// <remarks>
    /// Only a request that got no answer moves on: a handshake refused for an expired certificate,
    /// a connection that failed. A status or an error inside a 200 came from TranSmart itself, and
    /// the other host is the same service, so it would only say the same again.
    /// </remarks>
    private async Task<string> PostAsync(string payload, CancellationToken cancellationToken)
    {
        var first = Volatile.Read(ref _host);
        try
        {
            return await PostAsync(Hosts[first], payload, cancellationToken);
        }
        catch (HttpRequestException ex) when (!cancellationToken.IsCancellationRequested)
        {
            var other = 1 - first;
            var body = await PostAsync(Hosts[other], payload, cancellationToken);

            // Only once the other host has answered, so a request that fails on both leaves the
            // original in place for the next one.
            if (Interlocked.CompareExchange(ref _host, other, first) == first)
                Log.Info("{Engine}：{Host} 連不上（{Error}），改用 {Other}", Name, Hosts[first], ex.HttpRequestError, Hosts[other]);

            return body;
        }
    }

    private async Task<string> PostAsync(string host, string payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/api/imt")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Referrer = new Uri($"https://{host}/zh-CN/index");
        return await ReadAsync(request, cancellationToken);
    }

    // 「攻擊力（Attack Force）」, 「Back（後退）」, 「장비 (Equipment)」: the whole answer is one
    // translation with another in brackets after it.
    private static readonly Regex Glossed = new(
        @"^\s*(?<first>[^（）()]+?)\s*[（(](?<second>[^（）()]+)[）)]\s*$", RegexOptions.Compiled);

    /// <summary>A short text's answer with the dictionary-style gloss TranSmart puts after it taken off.</summary>
    /// <remarks>
    /// <para>Single words and short labels from one language that is not English into another come
    /// back as two translations, one in brackets: 装備 → 「裝備（裝備）」, 攻撃力 → 「攻擊力（Attack
    /// Force）」, 戻る → 「Back（後退）」, 閉じる → 「Close（關閉）」 (measured 2026-10-08, Japanese into
    /// both Chinese and Korean; English into anything, and anything into English, comes back
    /// plain). It looks like the English the engine pivots through, left in. On a menu that is every
    /// label, twice.</para>
    ///
    /// <para>Which half is the translation is not fixed, so the half that is not English is kept,
    /// and a pair that is the same twice is kept once. Where that cannot be told — both halves in
    /// Latin letters, as a translation into French would be — the answer is left as it came: a
    /// gloss shown is better than the translation dropped. Nothing is touched when the text itself
    /// had brackets, since then they may well be the text's own.</para>
    /// </remarks>
    internal static string WithoutGloss(string piece, string answer, string targetLanguage)
    {
        if (piece.IndexOfAny(['(', '（']) >= 0) return answer;

        var match = Glossed.Match(answer);
        if (!match.Success) return answer;

        var first = match.Groups["first"].Value.Trim();
        var second = match.Groups["second"].Value.Trim();

        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return first;
        if (targetLanguage == "en") return answer;

        var firstLatin = IsLatin(first);
        var secondLatin = IsLatin(second);
        if (firstLatin == secondLatin) return answer;
        return firstLatin ? second : first;
    }

    private static bool IsLatin(string text) => text.All(c => c < 0x250);
}
