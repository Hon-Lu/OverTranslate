using System.Globalization;
using System.Text.RegularExpressions;

namespace OverTranslate.Translation.Bing;

/// <summary>The credentials bing.com's translator page hands its own requests.</summary>
internal sealed record BingCredentials(string Key, string Token, string Ig, string Iid, DateTimeOffset Expires);

/// <summary>
/// Fetches <see cref="BingCredentials"/> from the translator page and keeps them until they lapse or
/// are refused. Shared by <see cref="BingTranslator"/> and speech, which ask for the same ones.
/// </summary>
/// <remarks>
/// A key and a token the page embeds for its own requests, valid for an hour. Fetched once and
/// shared by every request until they expire or <see cref="Invalidate"/> is called.
/// </remarks>
internal sealed class BingSession(HttpClient http, string engineName)
{
    public const string Host = "https://www.bing.com";

    private static readonly Regex AbusePrevention = new(
        @"params_AbusePreventionHelper\s*=\s*\[\s*(\d+)\s*,\s*""([^""]+)""\s*,\s*(\d+)\s*\]",
        RegexOptions.Compiled);
    private static readonly Regex ImpressionGuid = new(@"IG:""([0-9A-Fa-f]+)""", RegexOptions.Compiled);
    private static readonly Regex InstanceId = new(@"data-iid=""(translator\.\d+)""", RegexOptions.Compiled);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private BingCredentials? _credentials;

    /// <summary>Forgets the credentials, so the next request fetches the page again.</summary>
    public void Invalidate() => _credentials = null;

    public async Task<BingCredentials> GetAsync(CancellationToken cancellationToken)
    {
        if (_credentials is { } cached && cached.Expires > DateTimeOffset.UtcNow) return cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Every request that found them missing queued here; the first one through fetched them.
            if (_credentials is { } fresh && fresh.Expires > DateTimeOffset.UtcNow) return fresh;

            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Host}/translator");
            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new TranslationEngineException(engineName, $"HTTP {(int)response.StatusCode}", response.StatusCode);
            var page = await response.Content.ReadAsStringAsync(cancellationToken);

            var abuse = AbusePrevention.Match(page);
            if (!abuse.Success)
                throw new TranslationEngineException(engineName, "translator page has no credentials");

            var lifetime = long.TryParse(abuse.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
                ? TimeSpan.FromMilliseconds(ms)
                : TimeSpan.FromMinutes(10);

            var ig = ImpressionGuid.Match(page);
            var iid = InstanceId.Match(page);

            _credentials = new BingCredentials(
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
            _gate.Release();
        }
    }
}
