using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;

namespace OverTranslate.Services;

/// <summary>Why an upload did not happen, in the terms the user needs to hear it.</summary>
public enum DiagnosticUploadFailure
{
    /// <summary>No endpoint is configured, so nothing was attempted.</summary>
    NotConfigured,

    /// <summary>Nothing answered: offline, a corporate proxy, a firewall, a timeout.</summary>
    Unreachable,

    /// <summary>The bundle is over the endpoint's size limit.</summary>
    TooLarge,

    /// <summary>Too many uploads from this address too quickly.</summary>
    RateLimited,

    /// <summary>The endpoint answered, but not with a code we can show.</summary>
    Rejected,
}

/// <summary>An upload that did not produce a code. Always recoverable — see the remarks.</summary>
/// <remarks>
/// Every one of these is met the same way by the caller: say what happened and open Explorer on the
/// zip that is still sitting on the disk. The bundle is written before the upload is attempted
/// precisely so that this is always possible, and so that the offline path from #126 stays whole.
/// </remarks>
public sealed class DiagnosticUploadException(DiagnosticUploadFailure reason, string detail)
    : Exception(detail)
{
    public DiagnosticUploadFailure Reason { get; } = reason;
}

/// <summary>
/// Hands a diagnostic bundle to the collection endpoint and returns the short code the user pastes
/// into their problem report.
/// </summary>
/// <remarks>
/// The design constraint that shapes everything here: this runs because a person pressed a button,
/// and only then. There is no retry loop, no background queue, no crash handler that calls it. The
/// bundle contains the log, and with 記錄詳細資訊 switched on the log contains text that was on the
/// user's screen — which is exactly the log worth sending, and exactly why sending it can never be
/// something that happens on its own.
///
/// The receiving end is a Cloudflare Worker in front of an R2 bucket, kept in its own public
/// repository so that what happens to the upload can be read rather than promised:
/// https://github.com/Hon-Lu/OverTranslate-Diag-Worker
/// </remarks>
public static class DiagnosticUploadService
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Where bundles go. Baked into the build rather than fetched from anywhere: it is one string,
    /// and standing up a remote configuration mechanism to hold one string costs more than it saves
    /// — including in the failure mode, where a broken config endpoint would take the diagnostic
    /// upload down with it.
    /// </summary>
    /// <remarks>
    /// Having none is still a working state rather than a broken one — <see cref="IsConfigured"/>
    /// goes false, the button says "export" and only exports, and the #126 path is what remains.
    /// That is what every build was before the worker existed.
    /// </remarks>
    private const string DefaultEndpoint =
        "https://overtranslate-diag.overtranslate.workers.dev/v1/bundle";

    /// <summary>
    /// Overrides <see cref="DefaultEndpoint"/>, for pointing a local build at a `wrangler dev`
    /// instance. Setting it to anything that is not an http(s) address — "off" does nicely — turns
    /// uploading off, which is the supported way to opt out of a feature that already requires a
    /// deliberate press.
    /// </summary>
    /// <remarks>
    /// Not "set it to an empty string", which is what this used to say and does not work: on Windows
    /// setting a variable to "" deletes it, and a deleted variable falls through to the endpoint
    /// compiled in — the exact opposite of what someone typing that would be trying to achieve.
    /// </remarks>
    private const string EndpointVariable = "OVERTRANSLATE_DIAG_ENDPOINT";

    /// <summary>Matches the worker's own limit. Checked here so an oversized bundle costs no upload.</summary>
    public const long MaxUploadBytes = 5 * 1024 * 1024;

    /// <summary>
    /// Generous, because the upload happens over a connection we know nothing about and the user is
    /// watching a button that says it is working. Short enough that a black-holed connection gives
    /// up while they are still in the room.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>What the worker returns, and the only shape this accepts back.</summary>
    private static readonly Regex CodePattern = new(
        @"^[0-9A-HJKMNP-TV-Z]{3}-[0-9A-HJKMNP-TV-Z]{3}$", RegexOptions.Compiled);

    public static string Endpoint =>
        Environment.GetEnvironmentVariable(EndpointVariable) ?? DefaultEndpoint;

    /// <summary>
    /// Whether there is somewhere to upload to. Tested by parsing rather than by checking for a
    /// non-empty string, which also settles what happens to a mistyped address: it turns the feature
    /// off, rather than sending a log full of someone's screen to whatever that address resolves to.
    /// </summary>
    public static bool IsConfigured => IsUsableEndpoint(Endpoint);

    private static bool IsUsableEndpoint(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Uploads a bundle and returns its code. Throws <see cref="DiagnosticUploadException"/> for
    /// every failure, so callers have exactly one thing to catch and one thing to do about it.
    /// </summary>
    public static async Task<string> UploadAsync(string bundlePath, CancellationToken token = default)
    {
        var endpoint = Endpoint;
        if (!IsUsableEndpoint(endpoint))
            throw new DiagnosticUploadException(DiagnosticUploadFailure.NotConfigured, "No endpoint");

        var info = new FileInfo(bundlePath);
        if (info.Length > MaxUploadBytes)
        {
            // Refused here rather than by the server: the same answer, minus the several megabytes
            // spent on someone's metered connection to hear it.
            throw new DiagnosticUploadException(
                DiagnosticUploadFailure.TooLarge, $"{info.Length} bytes exceeds {MaxUploadBytes}");
        }

        using var stream = File.OpenRead(bundlePath);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
        // Which build and which Windows answers most of the "why does this only happen to them"
        // questions. Neither is anything the receiving end trusts — they are a client's word about
        // itself, and any client can say anything.
        request.Headers.TryAddWithoutValidation("x-overtranslate-version", AppVersion);
        request.Headers.TryAddWithoutValidation("x-overtranslate-os", RuntimeInformation.OSDescription);

        // Sent here as well as sitting inside the bundle, because the two are read at different
        // moments: the copy in appsettings.redacted.json is found by someone who has already
        // downloaded the zip, and by then they know which report they are reading. This one arrives
        // with the upload itself, which is what lets a second report from an install announce itself
        // as one rather than waiting to be recognised.
        //
        // Empty only if the upload somehow runs before startup settled it, and sent regardless:
        // an absent header reads as "this client did not say", which is the truth, where an omitted
        // one would be indistinguishable from an older build that could not.
        request.Headers.TryAddWithoutValidation("x-overtranslate-id", AppIdentityService.Current);

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The expected failure, not the exceptional one: the people most likely to press this
            // button are behind a network that is part of what they are reporting.
            Log.Warn(ex, "Diagnostic upload could not reach {0}", endpoint);
            throw new DiagnosticUploadException(DiagnosticUploadFailure.Unreachable, ex.Message);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                Log.Warn("Diagnostic upload refused with {0}: {1}", (int)response.StatusCode, body);
                throw new DiagnosticUploadException(
                    Classify(response.StatusCode), $"HTTP {(int)response.StatusCode}");
            }

            var code = ParseCode(body);
            if (code is null)
            {
                Log.Warn("Diagnostic upload returned no usable code: {0}", body);
                throw new DiagnosticUploadException(DiagnosticUploadFailure.Rejected, "No code in response");
            }

            Log.Info("Diagnostic bundle uploaded as {0}", code);
            return code;
        }
    }

    /// <summary>
    /// Which of the things the user can be told happened. Everything that is not specifically "too
    /// big" or "too often" collapses into <see cref="DiagnosticUploadFailure.Rejected"/>: the
    /// difference between a 500 and a 503 changes nothing they can do, and the fallback is the same
    /// either way.
    /// </summary>
    public static DiagnosticUploadFailure Classify(HttpStatusCode status) => status switch
    {
        HttpStatusCode.RequestEntityTooLarge => DiagnosticUploadFailure.TooLarge,
        HttpStatusCode.TooManyRequests       => DiagnosticUploadFailure.RateLimited,
        _                                    => DiagnosticUploadFailure.Rejected,
    };

    /// <summary>
    /// The code from a response body, or null if there is not one in the shape we expect.
    /// </summary>
    /// <remarks>
    /// Validated against <see cref="CodePattern"/> rather than shown as received. This string goes
    /// into a box the user is told to copy into a public forum post, and the endpoint it comes from
    /// is reachable by anything on the internet — including a captive portal answering 200 with its
    /// own login page. A code that does not look like a code is a failure, not a code.
    /// </remarks>
    public static string? ParseCode(string body)
    {
        try
        {
            var code = JsonDocument.Parse(body).RootElement.TryGetProperty("code", out var value)
                ? value.GetString()
                : null;

            return code is not null && CodePattern.IsMatch(code) ? code : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string AppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
