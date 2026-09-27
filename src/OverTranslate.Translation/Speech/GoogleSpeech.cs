using System.Text.Json;

namespace OverTranslate.Translation.Speech;

/// <summary>The languages Google Translate has a voice for, which both Google speech endpoints share.</summary>
internal static class GoogleVoices
{
    /// <remarks>
    /// The list GTranslate carries (MIT, d4n3436/GTranslate), in Google's codes. Slovenian is the
    /// one language the application offers that is not on it, and goes to Microsoft.
    /// </remarks>
    private static readonly HashSet<string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        "af", "am", "ar", "bg", "bn", "bs", "ca", "cs", "cy", "da", "de", "el", "en", "eo", "es", "et", "eu",
        "fi", "fr", "fr-CA", "gl", "gu", "ha", "he", "hi", "hr", "hu", "hy", "id", "is", "it", "iw", "ja", "jv",
        "km", "kn", "ko", "la", "lt", "lv", "mk", "ml", "mr", "ms", "my", "ne", "nl", "no", "pa", "pl", "pt",
        "pt-PT", "ro", "ru", "si", "sk", "sq", "sr", "su", "sv", "sw", "ta", "te", "th", "tl", "tr", "uk", "ur",
        "vi", "yue", "zh-CN", "zh-TW",
    };

    public static bool Has(string language) => Languages.Contains(language);

    /// <summary>The most characters one Google speech request reads.</summary>
    public const int MaxCharacters = 200;
}

/// <summary>
/// Google's speech through <c>translate_tts</c>, the address behind the speaker on the old
/// translator page — a GET per piece of at most 200 characters, answered with MP3.
/// </summary>
public sealed class GoogleWebSpeech(HttpClient http) : SpeechEngine(http)
{
    public override string Name => "GoogleWebSpeech";

    public override bool Supports(string language) => GoogleVoices.Has(language);

    protected override async Task<byte[]> SpeakAsync(string text, string language, CancellationToken cancellationToken)
    {
        var pieces = SpeechText.Split(text, GoogleVoices.MaxCharacters);

        // Side by side, as the page itself fetches them; total and idx say where each belongs.
        var audio = await Task.WhenAll(pieces.Select((piece, i) =>
        {
            var url = "https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob" +
                      $"&tl={Uri.EscapeDataString(language)}&q={Uri.EscapeDataString(piece)}" +
                      $"&total={pieces.Count}&idx={i}&textlen={piece.Length}";
            return SendAsync(url);
        }));

        return Concatenate(audio);

        async Task<byte[]> SendAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            return await ReadBytesAsync(request, cancellationToken);
        }
    }
}

/// <summary>
/// Google's speech through <c>batchexecute</c>, the call the current translator page makes
/// (<c>jQ1olc</c>): the same voices, with the MP3 carried as base64 inside the answer.
/// </summary>
/// <remarks>The request shape is GTranslate's <c>GoogleTranslator2.TextToSpeechAsync</c> (MIT, d4n3436/GTranslate).</remarks>
public sealed class GoogleRpcSpeech(HttpClient http) : SpeechEngine(http)
{
    private const string RpcId = "jQ1olc";

    public override string Name => "GoogleRpcSpeech";

    public override bool Supports(string language) => GoogleVoices.Has(language);

    protected override async Task<byte[]> SpeakAsync(string text, string language, CancellationToken cancellationToken)
    {
        var pieces = SpeechText.Split(text, GoogleVoices.MaxCharacters);
        return Concatenate(await Task.WhenAll(pieces.Select(SendAsync)));

        async Task<byte[]> SendAsync(string piece)
        {
            // The last field is the "slow" switch, off.
            var call = new object?[]
            {
                RpcId,
                JsonSerializer.Serialize(new object?[] { piece, language, null, "undefined", new object[] { 0 } }),
                null,
                "generic",
            };

            using var request = new HttpRequestMessage(HttpMethod.Post,
                "https://translate.google.com/_/TranslateWebserverUi/data/batchexecute?rpcids=" + RpcId)
            {
                Content = new FormUrlEncodedContent(
                    [new KeyValuePair<string, string>("f.req", JsonSerializer.Serialize(new object[] { new object[] { call } }))]),
            };

            var body = await ReadStringAsync(request, cancellationToken);

            // Opens with )]}' like every batchexecute answer; the JSON starts after it.
            var start = body.IndexOf('[');
            if (start < 0) throw new TranslationEngineException(Name, "unexpected answer");
            using var document = JsonDocument.Parse(body.AsMemory(start));

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 3 ||
                    entry[0].ValueKind != JsonValueKind.String || entry[0].GetString() != "wrb.fr")
                    continue;

                // Refused calls come back without data, as translations do (GoogleRpcTranslator).
                if (entry[2].ValueKind != JsonValueKind.String)
                    throw new TranslationEngineException(Name, "call refused");

                using var data = JsonDocument.Parse(entry[2].GetString()!);
                return Convert.FromBase64String(data.RootElement[0].GetString()
                    ?? throw new FormatException("no audio"));
            }

            throw new TranslationEngineException(Name, "no answer in the envelope");
        }
    }
}
