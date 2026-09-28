using System.Net;
using System.Text.Json;

namespace OverTranslate.Translation.Bing;

/// <summary>
/// 「Bing 翻譯」: the translator on bing.com, one text per request.
/// </summary>
/// <remarks>
/// <para>The one engine here that cannot take a list, and not for want of trying
/// (<c>.ai/translation-service-analysis/bing.md</c>): repeated fields translate only the first,
/// and texts joined with line breaks, blank lines or numbered markers come back merged and
/// renumbered — Bing now translates with a language model, and a model that reads eight lines as
/// one passage answers with five. Most of the time the count happens to match, which is worse than
/// failing: the texts would be misplaced without anything noticing. So each text is its own request,
/// sent side by side, and this is the engine the application's fallback exists for.</para>
///
/// <para>The same model means the same text can come back worded differently on another request —
/// two of six test sentences did over five tries. Nothing here can change that.</para>
///
/// <para>Credentials come from the translator page itself (<see cref="BingSession"/>). Errors do not
/// use the HTTP status: a refused request is a 200 whose body is <c>{"statusCode":400}</c>, so the
/// body is what decides.</para>
/// </remarks>
public sealed class BingTranslator(HttpClient http) : BatchTranslator(http)
{
    private const string Host = BingSession.Host;

    private readonly BingSession _session = new(http, "Bing");
    private int _sequence;

    public override string Name => "Bing";

    protected override int MaxItemsPerRequest => 1;

    /// <remarks>Measured: 1,000 passes, 1,001 is refused, with or without <c>isVertical</c>.</remarks>
    protected override int MaxCharactersPerRequest => 1000;

    protected override async Task<IReadOnlyList<TextTranslation?>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var credentials = await _session.GetAsync(cancellationToken);
        var sequence = Interlocked.Increment(ref _sequence);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{Host}/ttranslatev3?isVertical=1&IG={credentials.Ig}&IID={credentials.Iid}.{sequence}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["fromLang"] = sourceLanguage is null ? "auto-detect" : LanguageCodes.ToMicrosoft(sourceLanguage),
                ["to"]       = LanguageCodes.ToMicrosoft(targetLanguage),
                ["text"]     = pieces[0],
                ["token"]    = credentials.Token,
                ["key"]      = credentials.Key,
            }),
        };

        using var document = JsonDocument.Parse(await ReadAsync(request, cancellationToken));
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            var status = root.TryGetProperty("statusCode", out var code) && code.TryGetInt32(out var value)
                ? (HttpStatusCode)value
                : (HttpStatusCode?)null;

            // Anything but a refusal of the text itself may be the credentials going stale before
            // their hour is up, and asking the page again is cheap next to failing every request
            // until it runs out.
            if (status != HttpStatusCode.BadRequest) _session.Invalidate();

            throw new TranslationEngineException(Name, $"refused ({(int?)status})", status);
        }

        var answer = root[0];
        var detected = answer.TryGetProperty("detectedLanguage", out var language) &&
                       language.TryGetProperty("language", out var code2) && code2.ValueKind == JsonValueKind.String
            ? LanguageCodes.FromMicrosoft(code2.GetString()!)
            : "";

        return [new TextTranslation(RequireString(answer.GetProperty("translations")[0].GetProperty("text")), detected)];
    }
}
