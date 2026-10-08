using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OverTranslate.Translation.Youdao;

/// <summary>
/// 「有道翻譯」: the translator on fanyi.youdao.com, many texts per request as lines of one text.
/// </summary>
/// <remarks>
/// <para>The page's own endpoint (<c>dict.youdao.com/webtranslate</c>), added for users Google
/// cannot reach. Signed, and answered encrypted, with credentials the page fetches for itself —
/// see <see cref="YoudaoSession"/>.</para>
///
/// <para>Not a list endpoint: it takes one text, <c>i</c>, and answers one entry per line of it. So
/// the texts are sent as lines, which is only safe because no piece is ever sent with a line break
/// of its own — one inside a text would come back as an entry more, and every answer after it would
/// belong to the text before (measured 2026-10-08: four texts with one line break among them came
/// back as five). <see cref="Split"/> joins a text's lines, and a count that still does not match
/// fails the request rather than being lined up by guesswork. Fifty texts, 4,300 characters, came
/// back in 0.4 s with every line in place.</para>
///
/// <para>A detected request is read as one language whatever is in it, so it is sent per script
/// (<see cref="ScriptGroups"/>).</para>
/// </remarks>
public sealed class YoudaoTranslator(HttpClient http) : BatchTranslator(http)
{
    private const string Endpoint = "https://dict.youdao.com/webtranslate";

    private readonly YoudaoSession _session = new(http, "Youdao");

    public override string Name => "Youdao";

    protected override int MaxItemsPerRequest => 50;

    /// <remarks>
    /// The page stops at 5,000 characters; the endpoint itself took 8,600 when measured, but that
    /// is not a limit anybody promised.
    /// </remarks>
    protected override int MaxCharactersPerRequest => 4500;

    private static readonly Regex LineBreak = new(@"\s*\n\s*", RegexOptions.Compiled);

    /// <summary>Each paragraph its own piece, and the lines within it joined.</summary>
    private protected override IReadOnlyList<TranslationRequestChunk> Split(string text) => SplitJoiningLines(text);

    protected override async Task<IReadOnlyList<TextTranslation?>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken) =>
        sourceLanguage is null
            ? await ScriptGroups.SendAsync(Name, pieces,
                group => SendOneAsync(group, targetLanguage, null, cancellationToken))
            : await SendOneAsync(pieces, targetLanguage, sourceLanguage, cancellationToken);

    private async Task<IReadOnlyList<TextTranslation?>> SendOneAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        // Credentials that have stopped working look like any other refusal, so a refusal is asked
        // again once with fresh ones before it is believed.
        for (var attempt = 0; ; attempt++)
        {
            var credentials = await _session.GetAsync(cancellationToken);
            try
            {
                return await SendWithAsync(credentials, pieces, targetLanguage, sourceLanguage, cancellationToken);
            }
            catch (Exception ex) when (attempt == 0 && ex is TranslationEngineException { StatusCode: null } or CryptographicException or FormatException)
            {
                _session.Invalidate();
            }
        }
    }

    private async Task<IReadOnlyList<TextTranslation?>> SendWithAsync(
        YoudaoCredentials credentials, IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            // Split has already joined every piece's lines; this only makes sure of it, since one
            // left in would put every answer after it against the wrong text.
            new("i", string.Join("\n", pieces.Select(piece => LineBreak.Replace(piece.Trim(), " ")))),
            new("from", sourceLanguage is null ? "auto" : LanguageCodes.ToYoudao(sourceLanguage)),
            new("to", LanguageCodes.ToYoudao(targetLanguage)),
            new("useTerm", "false"),
            new("dictResult", "true"),
        };
        fields.AddRange(YoudaoSession.Signed("webfanyi", credentials.SecretKey));

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        request.Headers.Referrer = new Uri(YoudaoSession.Page);

        using var document = JsonDocument.Parse(YoudaoSession.Decrypt(await ReadAsync(request, cancellationToken), credentials));
        var root = document.RootElement;

        var code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var value) ? value : -1;
        if (code != 0)
            throw new TranslationEngineException(Name, $"refused (code {code})");

        // "ja2zh-CHT": what it read the whole request as, then what it wrote.
        var detected = sourceLanguage is null &&
                       root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                       type.GetString()!.Split('2') is [{ Length: > 0 } from, _]
            ? LanguageCodes.FromYoudao(from)
            : "";

        // One entry per line sent, each the sentences of that line; a count that does not match is
        // for the caller to refuse.
        return root.GetProperty("translateResult").EnumerateArray()
            .Select(line => (TextTranslation?)new TextTranslation(
                string.Concat(line.EnumerateArray().Select(segment => RequireString(segment.GetProperty("tgt")))).TrimEnd('\r', '\n'),
                detected))
            .ToList();
    }
}
