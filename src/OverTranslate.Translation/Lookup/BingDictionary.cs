using System.Net;
using System.Text.Json;
using OverTranslate.Translation.Bing;

namespace OverTranslate.Translation.Lookup;

/// <summary>
/// Bing's dictionary: <c>tlookupv3</c> on bing.com's translator page, with the credentials that
/// page hands out (<see cref="BingSession"/>).
/// </summary>
/// <remarks>
/// <para>The request and the reading of the answer are GTranslate's
/// <c>BingTranslator.LookupDictionaryAsync</c> and <c>BingDictionaryParser</c>
/// (MIT, d4n3436/GTranslate). The answer is the Translator API's <c>dictionary/lookup</c>, as
/// <see cref="MicrosoftDictionary"/> gets it, plus a transliteration of each translation.</para>
///
/// <para>Refusals are a 200 with <c>{"statusCode":…}</c>, as with <see cref="BingTranslator"/>.</para>
/// </remarks>
public sealed class BingDictionary : DictionaryEngine
{
    private readonly BingSession _session;

    public BingDictionary(HttpClient http) : base(http) => _session = new BingSession(http, "BingDictionary");

    public override string Name => "BingDictionary";

    protected override async Task<DictionaryResult> QueryAsync(
        string text, string targetLanguage, string sourceLanguage, CancellationToken cancellationToken)
    {
        var credentials = await _session.GetAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{BingSession.Host}/tlookupv3?isVertical=1&IG={credentials.Ig}&IID={credentials.Iid}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["from"]  = LanguageCodes.ToMicrosoft(sourceLanguage),
                ["to"]    = LanguageCodes.ToMicrosoft(targetLanguage),
                ["text"]  = text,
                ["token"] = credentials.Token,
                ["key"]   = credentials.Key,
            }),
        };

        using var document = JsonDocument.Parse(await ReadStringAsync(request, cancellationToken));
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            var status = root.TryGetProperty("statusCode", out var code) && code.TryGetInt32(out var value)
                ? (HttpStatusCode)value
                : (HttpStatusCode?)null;

            // As in BingTranslator: anything but a refusal of the request itself may be stale credentials.
            if (status != HttpStatusCode.BadRequest) _session.Invalidate();

            throw new TranslationEngineException(Name, $"refused ({(int?)status})", status);
        }

        return MicrosoftLookupAnswer.Read(root, text, withTransliteration: true);
    }
}
