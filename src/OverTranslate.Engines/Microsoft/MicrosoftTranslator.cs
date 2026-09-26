using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OverTranslate.Engines.Microsoft;

/// <summary>
/// 「Microsoft 翻譯」: the Translator API as Microsoft's own Android app calls it, many texts per
/// request.
/// </summary>
/// <remarks>
/// <para>The official request format is already a list — <c>[{"Text":…},{"Text":…}]</c> — and the
/// answers come back one per text, in order. GTranslate sent a list of one. Measured
/// (<c>.ai/translation-service-analysis/microsoft.md</c>): 24 texts in one request came back
/// identical to the same 24 sent one at a time, and identical again when repeated; 177 requests at
/// one every half second, and 10 at the same instant, all succeeded.</para>
///
/// <para>The limit is 1,000 characters for the whole request, counted as <see cref="string.Length"/>
/// counts them, and going over is answered with a 429 that looks like throttling and is not. Many
/// short texts are slow even under it — 200 took eight seconds — so requests are also kept to a few
/// dozen texts and sent side by side.</para>
///
/// <para>Signed, not keyed: every request carries an HMAC of its own URL made with the Android
/// app's key. The scheme and the key are GTranslate's <c>MicrosoftTranslator.GetSignature</c>
/// (MIT, d4n3436/GTranslate). The token GTranslate also fetches is for speech; sent here as a
/// bearer it is refused with a 401.</para>
/// </remarks>
public sealed class MicrosoftTranslator(HttpClient http) : BatchTranslator(http)
{
    private const string Host = "api.cognitive.microsofttranslator.com";

    private static readonly byte[] PrivateKey =
    [
        0xa2, 0x29, 0x3a, 0x3d, 0xd0, 0xdd, 0x32, 0x73, 0x97, 0x7a, 0x64, 0xdb, 0xc2, 0xf3, 0x27, 0xf5,
        0xd7, 0xbf, 0x87, 0xd9, 0x45, 0x9d, 0xf0, 0x5a, 0x09, 0x66, 0xc6, 0x30, 0xc6, 0x6a, 0xaa, 0x84,
        0x9a, 0x41, 0xaa, 0x94, 0x3a, 0xa8, 0xd5, 0x1a, 0x6e, 0x4d, 0xaa, 0xc9, 0xa3, 0x70, 0x12, 0x35,
        0xc7, 0xeb, 0x12, 0xf6, 0xe8, 0x23, 0x07, 0x9e, 0x47, 0x10, 0x95, 0x91, 0x88, 0x55, 0xd8, 0x17,
    ];

    public override string Name => "Microsoft";

    /// <remarks>Measured, not guessed: 1,000 passes and 1,001 is a 429, in every script tried.</remarks>
    protected override int MaxCharactersPerRequest => 1000;

    protected override int MaxItemsPerRequest => 25;

    protected override async Task<IReadOnlyList<TextTranslation>> SendAsync(
        IReadOnlyList<string> pieces, string targetLanguage, string? sourceLanguage,
        CancellationToken cancellationToken)
    {
        // Detected per text when no source is given, and that is what a screen needs: told the
        // source is Japanese, a Korean line in the same request comes back untranslated.
        var url = $"{Host}/translate?api-version=3.0&to={LanguageCodes.ToMicrosoft(targetLanguage)}" +
                  (sourceLanguage is null ? "" : $"&from={LanguageCodes.ToMicrosoft(sourceLanguage)}");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://" + url)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(pieces.Select(piece => new { Text = piece })),
                Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-MT-Signature", Sign(url, DateTimeOffset.UtcNow, Guid.NewGuid()));

        using var document = JsonDocument.Parse(await ReadAsync(request, cancellationToken));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            throw new TranslationEngineException(Name, $"unexpected answer ({root.ValueKind})");

        return root.EnumerateArray().Select(answer => new TextTranslation(
            RequireString(answer.GetProperty("translations")[0].GetProperty("text")),
            answer.TryGetProperty("detectedLanguage", out var detected) &&
            detected.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String
                ? LanguageCodes.FromMicrosoft(language.GetString()!)
                : "")).ToList();
    }

    /// <summary>The <c>X-MT-Signature</c> for one URL.</summary>
    /// <param name="url">Host, path and query, without the scheme.</param>
    /// <remarks>
    /// Bound to the URL, target language included — a signature made for <c>to=ja</c> is refused on
    /// <c>to=zh-Hant</c> — but not single-use, and still accepted after seventy seconds. Signing
    /// every request costs one HMAC and removes the question.
    /// </remarks>
    internal static string Sign(string url, DateTimeOffset now, Guid nonce)
    {
        var guid = nonce.ToString("N");
        var date = now.UtcDateTime.ToString(@"ddd, dd MMM yyyy HH:mm:ssG\MT", CultureInfo.InvariantCulture);
        var message = $"MSTranslatorAndroidApp{Uri.EscapeDataString(url)}{date}{guid}".ToLowerInvariant();
        var hash = HMACSHA256.HashData(PrivateKey, Encoding.UTF8.GetBytes(message));
        return $"MSTranslatorAndroidApp::{Convert.ToBase64String(hash)}::{date}::{guid}";
    }
}
