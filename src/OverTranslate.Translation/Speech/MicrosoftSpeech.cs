using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using OverTranslate.Translation.Bing;
using OverTranslate.Translation.Microsoft;

namespace OverTranslate.Translation.Speech;

/// <summary>The Azure neural voices Microsoft and Bing read with, one per language.</summary>
internal static class MicrosoftVoices
{
    /// <remarks>
    /// <para>Google codes to Azure voices, for the languages the application offers. The choices
    /// are GTranslate's <c>MicrosoftTranslator.DefaultVoices</c> (MIT, d4n3436/GTranslate) with one
    /// exception: GTranslate reads Traditional Chinese with Xiaoxiao, a Mainland voice, and this
    /// uses HsiaoChen, a Taiwanese one.</para>
    ///
    /// <para>Keyed by Google's codes, which is what the old service never did: it asked for
    /// <c>zh-Hans</c> and <c>zh-Hant</c>, which GTranslate's table does not have, so neither
    /// Microsoft nor Bing ever read Chinese at all.</para>
    /// </remarks>
    private static readonly Dictionary<string, (string Name, string Locale)> Voices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bg"]    = ("bg-BG-BorislavNeural", "bg-BG"),
        ["cs"]    = ("cs-CZ-AntoninNeural", "cs-CZ"),
        ["da"]    = ("da-DK-ChristelNeural", "da-DK"),
        ["de"]    = ("de-DE-KatjaNeural", "de-DE"),
        ["el"]    = ("el-GR-NestorasNeural", "el-GR"),
        ["en"]    = ("en-US-AriaNeural", "en-US"),
        ["es"]    = ("es-ES-ElviraNeural", "es-ES"),
        ["et"]    = ("et-EE-AnuNeural", "et-EE"),
        ["fi"]    = ("fi-FI-NooraNeural", "fi-FI"),
        ["fr"]    = ("fr-FR-DeniseNeural", "fr-FR"),
        ["hu"]    = ("hu-HU-TamasNeural", "hu-HU"),
        ["id"]    = ("id-ID-ArdiNeural", "id-ID"),
        ["it"]    = ("it-IT-DiegoNeural", "it-IT"),
        ["ja"]    = ("ja-JP-NanamiNeural", "ja-JP"),
        ["ko"]    = ("ko-KR-SunHiNeural", "ko-KR"),
        ["lt"]    = ("lt-LT-OnaNeural", "lt-LT"),
        ["lv"]    = ("lv-LV-EveritaNeural", "lv-LV"),
        ["nl"]    = ("nl-NL-ColetteNeural", "nl-NL"),
        ["no"]    = ("nb-NO-PernilleNeural", "nb-NO"),
        ["pl"]    = ("pl-PL-ZofiaNeural", "pl-PL"),
        ["pt"]    = ("pt-BR-FranciscaNeural", "pt-BR"),
        ["ro"]    = ("ro-RO-EmilNeural", "ro-RO"),
        ["ru"]    = ("ru-RU-DariyaNeural", "ru-RU"),
        ["sk"]    = ("sk-SK-LukasNeural", "sk-SK"),
        ["sl"]    = ("sl-SI-RokNeural", "sl-SI"),
        ["sv"]    = ("sv-SE-SofieNeural", "sv-SE"),
        ["tr"]    = ("tr-TR-EmelNeural", "tr-TR"),
        ["uk"]    = ("uk-UA-PolinaNeural", "uk-UA"),
        ["zh-CN"] = ("zh-CN-XiaoxiaoNeural", "zh-CN"),
        ["zh-TW"] = ("zh-TW-HsiaoChenNeural", "zh-TW"),
    };

    public static bool Has(string language) => Voices.ContainsKey(language);

    /// <summary>The SSML that asks for <paramref name="text"/> in the voice for <paramref name="language"/>.</summary>
    public static string Ssml(string text, string language)
    {
        var (name, locale) = Voices[language];

        // Escaped for XML: a line with "&" or "<" in it is otherwise not SSML at all, and refused.
        return $"<speak version='1.0' xml:lang='{locale}'><voice xml:lang='{locale}' name='{name}'>" +
               $"{SecurityElement.Escape(text)}</voice></speak>";
    }
}

/// <summary>
/// Microsoft's speech, as its Android translator app asks for it: a speech token from the app's
/// own endpoint, then SSML to the Azure speech service in the region the token names.
/// </summary>
/// <remarks>
/// The token is fetched with the same signed request <see cref="MicrosoftTranslator"/> makes — the
/// token microsoft.md notes GTranslate fetching and the translator refusing — and kept until shortly
/// before the expiry written inside it. The shape is GTranslate's
/// <c>MicrosoftTranslator.TextToSpeechAsync</c> (MIT, d4n3436/GTranslate).
/// </remarks>
public sealed class MicrosoftSpeech(HttpClient http) : SpeechEngine(http)
{
    private const string TokenUrl = "dev.microsofttranslator.com/apps/endpoint?api-version=1.0";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Token, string Region, DateTimeOffset Expires)? _token;

    public override string Name => "MicrosoftSpeech";

    public override bool Supports(string language) => MicrosoftVoices.Has(language);

    protected override async Task<byte[]> SpeakAsync(string text, string language, CancellationToken cancellationToken)
    {
        var (token, region) = await GetTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1")
        {
            Content = new StringContent(MicrosoftVoices.Ssml(text, language), Encoding.UTF8, "application/ssml+xml"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        request.Headers.TryAddWithoutValidation("X-Microsoft-OutputFormat", "audio-16khz-32kbitrate-mono-mp3");

        try
        {
            return await ReadBytesAsync(request, cancellationToken);
        }
        catch (TranslationEngineException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            _token = null;   // expired early or revoked; the next press fetches a new one
            throw;
        }
    }

    private async Task<(string Token, string Region)> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is { } cached && cached.Expires > DateTimeOffset.UtcNow) return (cached.Token, cached.Region);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is { } fresh && fresh.Expires > DateTimeOffset.UtcNow) return (fresh.Token, fresh.Region);

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + TokenUrl);
            request.Headers.TryAddWithoutValidation("X-ClientVersion", "N/A");
            request.Headers.TryAddWithoutValidation("X-UserId", "0");
            request.Headers.TryAddWithoutValidation("X-MT-Signature",
                MicrosoftTranslator.Sign(TokenUrl, DateTimeOffset.UtcNow, Guid.NewGuid()));

            using var document = JsonDocument.Parse(await ReadStringAsync(request, cancellationToken));
            var token = document.RootElement.GetProperty("t").GetString() ?? throw new FormatException("no token");
            var region = document.RootElement.GetProperty("r").GetString() ?? throw new FormatException("no region");

            // A minute early, so a press never goes out with a token about to lapse.
            _token = (token, region, ExpiryOf(token) - TimeSpan.FromMinutes(1));
            return (token, region);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The <c>exp</c> inside a JWT, or five minutes from now when it cannot be read.</summary>
    internal static DateTimeOffset ExpiryOf(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length == 3)
            {
                var payload = parts[1].Replace('-', '+').Replace('_', '/');
                payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
                if (document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds))
                    return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // Falls through to the guess below.
        }

        return DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5);
    }
}

/// <summary>
/// Bing's speech: the same Azure voices as <see cref="MicrosoftSpeech"/>, asked for through
/// bing.com's translator page with the credentials that page hands out.
/// </summary>
/// <remarks>The shape is GTranslate's <c>BingTranslator.TextToSpeechAsync</c> (MIT, d4n3436/GTranslate).</remarks>
public sealed class BingSpeech : SpeechEngine
{
    private readonly BingSession _session;

    public BingSpeech(HttpClient http) : base(http) => _session = new BingSession(http, "BingSpeech");

    public override string Name => "BingSpeech";

    public override bool Supports(string language) => MicrosoftVoices.Has(language);

    protected override async Task<byte[]> SpeakAsync(string text, string language, CancellationToken cancellationToken)
    {
        var credentials = await _session.GetAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{BingSession.Host}/tfettts?isVertical=1&IG={credentials.Ig}&IID={credentials.Iid}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["ssml"]  = MicrosoftVoices.Ssml(text, language),
                ["token"] = credentials.Token,
                ["key"]   = credentials.Key,
            }),
        };

        using var response = await Http.SendAsync(request, cancellationToken);
        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        // Refusals are JSON in a 200, as with translations ({"statusCode":…}); speech is MP3.
        var refused = audio.Length > 0 && audio[0] == (byte)'{';
        if (!response.IsSuccessStatusCode || refused)
        {
            _session.Invalidate();
            throw response.IsSuccessStatusCode
                ? new TranslationEngineException(Name, "refused")
                : new TranslationEngineException(Name, $"HTTP {(int)response.StatusCode}", response.StatusCode);
        }

        return audio;
    }
}
