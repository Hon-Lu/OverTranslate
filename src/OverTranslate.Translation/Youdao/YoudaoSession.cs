using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OverTranslate.Translation.Youdao;

/// <summary>What fanyi.youdao.com's page signs and decrypts its own requests with.</summary>
internal sealed record YoudaoCredentials(string SecretKey, byte[] AesKey, byte[] AesIv);

/// <summary>
/// Fetches <see cref="YoudaoCredentials"/> the way the translator page does, and keeps them until
/// they are refused.
/// </summary>
/// <remarks>
/// <para>Three requests, each depending on the one before: the page, for the address of its script
/// (and, as a side effect, the <c>OUTFOX_SEARCH_USER_ID</c> cookie, without which the endpoint
/// answers <c>code 50</c> — 「网络验证失败」 — to all but the odd pair); the script, for the key
/// that signs the request for the real key and the fallback AES key and IV; and that request.
/// Measured 2026-10-08 against the page's script for <c>translation-website/1.0.7</c>.</para>
///
/// <para>The key getter's id carries a year (<c>webfanyi-key-getter-2025</c>) and looks like it is
/// rotated, so it is read from the script rather than written here. When the script no longer
/// matches, the failure says which piece was missing — the first thing to look at when this engine
/// stops working.</para>
/// </remarks>
internal sealed class YoudaoSession(HttpClient http, string engineName)
{
    public const string Page = "https://fanyi.youdao.com/";
    private const string KeyEndpoint = "https://dict.youdao.com/webtranslate/key";

    private static readonly Regex ScriptAddress = new(
        @"https://shared\.ydstatic\.com/dict/translation-website/[^/""']+/js/app\.[^.""']+\.js", RegexOptions.Compiled);
    private static readonly Regex KeyGetter = new(
        @"""(webfanyi-key-getter[^""]*)"",\w+=""(\w+)""", RegexOptions.Compiled);
    private static readonly Regex DecodeKey = new(@"decodeKey:""(.*?)"",", RegexOptions.Compiled);
    private static readonly Regex DecodeIv = new(@"decodeIv:""(.*?)"",", RegexOptions.Compiled);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private YoudaoCredentials? _credentials;

    /// <summary>Forgets the credentials, so the next request fetches them again.</summary>
    public void Invalidate() => _credentials = null;

    public async Task<YoudaoCredentials> GetAsync(CancellationToken cancellationToken)
    {
        if (_credentials is { } cached) return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Every request that found them missing queued here; the first one through fetched them.
            if (_credentials is { } fresh) return fresh;

            var page = await GetStringAsync(Page, cancellationToken);
            var address = ScriptAddress.Match(page);
            if (!address.Success)
                throw new TranslationEngineException(engineName, "translator page no longer names its script");

            var script = await GetStringAsync(address.Value, cancellationToken);
            var getter = KeyGetter.Match(script);
            if (!getter.Success)
                throw new TranslationEngineException(engineName, "script no longer has the key getter's id and key");
            var decodeKey = DecodeKey.Match(script);
            var decodeIv = DecodeIv.Match(script);

            var query = string.Join("&", Signed(getter.Groups[1].Value, getter.Groups[2].Value)
                .Select(field => $"{field.Key}={Uri.EscapeDataString(field.Value)}"));
            using var document = JsonDocument.Parse(await GetStringAsync($"{KeyEndpoint}?{query}", cancellationToken));
            var root = document.RootElement;
            if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 0)
                throw new TranslationEngineException(engineName, $"key request refused (code {(root.TryGetProperty("code", out var c) ? c.ToString() : "?")})");

            var data = root.GetProperty("data");
            var aesKey = StringOrEmpty(data, "aesKey") is { Length: > 0 } key ? key : decodeKey.Groups[1].Value;
            var aesIv = StringOrEmpty(data, "aesIv") is { Length: > 0 } iv ? iv : decodeIv.Groups[1].Value;
            if (aesKey.Length == 0 || aesIv.Length == 0)
                throw new TranslationEngineException(engineName, "no AES key or IV, from the key request or the script");

            _credentials = new YoudaoCredentials(
                StringOrEmpty(data, "secretKey") is { Length: > 0 } secret
                    ? secret
                    : throw new TranslationEngineException(engineName, "key request answered no secret key"),
                MD5.HashData(Encoding.UTF8.GetBytes(aesKey)),
                MD5.HashData(Encoding.UTF8.GetBytes(aesIv)));
            return _credentials;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The fields every request to dict.youdao.com carries: who it is from, and a signature over
    /// the time with <paramref name="key"/>.
    /// </summary>
    public static IEnumerable<KeyValuePair<string, string>> Signed(string keyId, string key)
    {
        var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return
        [
            new("keyid", keyId),
            new("mysticTime", time),
            new("sign", Sign(time, key)),
            new("client", "fanyideskweb"),
            new("product", "webfanyi"),
            new("appVersion", "12.0.0"),
            new("vendor", "web"),
            new("keyfrom", "fanyi.web"),
            new("pointParam", "client,mysticTime,product"),
            new("yduuid", "abcdefg"),
            new("mid", "1"),
            new("screen", "1"),
            new("model", "1"),
            new("network", "wifi"),
            new("abtest", "0"),
        ];
    }

    internal static string Sign(string time, string key) => Convert.ToHexString(
        MD5.HashData(Encoding.UTF8.GetBytes($"client=fanyideskweb&mysticTime={time}&product=webfanyi&key={key}")))
        .ToLowerInvariant();

    /// <summary>An answer as the page reads it: URL-safe base64 of AES-128-CBC.</summary>
    public static string Decrypt(string body, YoudaoCredentials credentials)
    {
        var base64 = body.Trim().Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');

        using var aes = Aes.Create();
        aes.Key = credentials.AesKey;
        return Encoding.UTF8.GetString(
            aes.DecryptCbc(Convert.FromBase64String(base64), credentials.AesIv, PaddingMode.PKCS7));
    }

    private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri(Page);
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new TranslationEngineException(engineName, $"HTTP {(int)response.StatusCode}", response.StatusCode);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string StringOrEmpty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}
