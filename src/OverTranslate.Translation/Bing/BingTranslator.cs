using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

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
/// <para>Credentials come from the translator page itself — a key and a token it embeds for its own
/// requests, valid for an hour — and are fetched once and shared by every request until they
/// expire or are refused. Errors do not use the HTTP status: a refused request is a 200 whose body
/// is <c>{"statusCode":400}</c>, so the body is what decides.</para>
/// </remarks>
public sealed class BingTranslator(HttpClient http) : BatchTranslator(http)
{
    private const string Host = "https://www.bing.com";

    private static readonly Regex AbusePrevention = new(
        @"params_AbusePreventionHelper\s*=\s*\[\s*(\d+)\s*,\s*""([^""]+)""\s*,\s*(\d+)\s*\]",
        RegexOptions.Compiled);
    private static readonly Regex ImpressionGuid = new(@"IG:""([0-9A-Fa-f]+)""", RegexOptions.Compiled);
    private static readonly Regex InstanceId = new(@"data-iid=""(translator\.\d+)""", RegexOptions.Compiled);

    private readonly SemaphoreSlim _credentialsGate = new(1, 1);
    private Credentials? _credentials;
    private int _sequence;

    private sealed record Credentials(string Key, string Token, string Ig, string Iid, DateTimeOffset Expires);

    public override string Name => "Bing";

    protected override int MaxItemsPerRequest => 1;

    /// <remarks>Measured: 1,000 passes, 1,001 is refused, with or without <c>isVertical</c>.</remarks>
    protected override int MaxCharactersPerRequest => 1000;

    protected override async Task<IReadOnlyList<TextTranslation?>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        var credentials = await GetCredentialsAsync(cancellationToken);
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
            if (status != HttpStatusCode.BadRequest) _credentials = null;

            throw new TranslationEngineException(Name, $"refused ({(int?)status})", status);
        }

        var answer = root[0];
        var detected = answer.TryGetProperty("detectedLanguage", out var language) &&
                       language.TryGetProperty("language", out var code2) && code2.ValueKind == JsonValueKind.String
            ? LanguageCodes.FromMicrosoft(code2.GetString()!)
            : "";

        return [new TextTranslation(RequireString(answer.GetProperty("translations")[0].GetProperty("text")), detected)];
    }

    private async Task<Credentials> GetCredentialsAsync(CancellationToken cancellationToken)
    {
        if (_credentials is { } cached && cached.Expires > DateTimeOffset.UtcNow) return cached;

        await _credentialsGate.WaitAsync(cancellationToken);
        try
        {
            // Every request that found them missing queued here; the first one through fetched them.
            if (_credentials is { } fresh && fresh.Expires > DateTimeOffset.UtcNow) return fresh;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Host}/translator");
            var page = await ReadAsync(request, cancellationToken);

            var abuse = AbusePrevention.Match(page);
            if (!abuse.Success)
                throw new TranslationEngineException(Name, "translator page has no credentials");

            var lifetime = long.TryParse(abuse.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                ? TimeSpan.FromMilliseconds(ms)
                : TimeSpan.FromMinutes(10);

            var ig = ImpressionGuid.Match(page);
            var iid = InstanceId.Match(page);

            _credentials = new Credentials(
                abuse.Groups[1].Value,
                abuse.Groups[2].Value,
                ig.Success ? ig.Groups[1].Value : Guid.NewGuid().ToString("N").ToUpperInvariant(),
                iid.Success ? iid.Groups[1].Value : "translator.5024",

                // Renewed a little early, so a request is never sent with a token about to lapse.
                DateTimeOffset.UtcNow + lifetime - TimeSpan.FromMinutes(Math.Min(5, lifetime.TotalMinutes / 2)));

            return _credentials;
        }
        finally
        {
            _credentialsGate.Release();
        }
    }
}
