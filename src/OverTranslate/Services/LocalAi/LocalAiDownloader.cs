using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using NLog;
using OverTranslate.Services.Ocr.Manga;

namespace OverTranslate.Services.LocalAi;

/// <summary>
/// Fetches one large file: resumed from where it stopped, and only given its real name once its
/// SHA-256 is right.
/// </summary>
/// <remarks>
/// <para>The same approach as <see cref="MangaModelStore"/>, with one rule changed. That store keeps
/// nothing of a failed download, on the reasoning that three hundred megabytes is not worth the
/// questions; here a file is several gigabytes, and a connection lost at 90% must not cost the 90%.
/// So a <c>.part</c> outlives a failure and even the app closing, and the next attempt carries on
/// from it. Stopping on purpose is the caller's to clean up.</para>
///
/// <para>A <c>.part</c> belongs to the source it came from (<see cref="MangaModelStore.PartPath"/>),
/// and a resumed file that does not hash right is fetched once more from the first byte.</para>
/// </remarks>
internal sealed class LocalAiDownloader
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const int Attempts = 3;

    private readonly HttpClient _http;

    internal LocalAiDownloader(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // Per read, not overall: a five-gigabyte file on a slow line is not a hung request.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OverTranslate");
    }

    /// <summary>How long to wait before the first retry; multiplied by the attempt for the next.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a transfer may go without a single byte before it counts as dropped.</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <param name="sources">Tried in order; the next one when a source has run out of attempts.</param>
    /// <param name="progress">Bytes of this file so far.</param>
    /// <param name="verifying">Called once the bytes are here and the hash is about to be taken.</param>
    /// <exception cref="InvalidDataException">The file arrived whole and did not hash right, from every source.</exception>
    internal async Task FetchAsync(
        IReadOnlyList<Uri> sources, string target, long size, string sha256,
        Action<long>? progress = null, Action? verifying = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var expected = sha256.ToLowerInvariant();

        Exception? last = null;
        foreach (var url in sources)
        {
            try
            {
                await FetchFromAsync(url, target, size, expected, progress, verifying, token).ConfigureAwait(false);
                foreach (var part in Directory.EnumerateFiles(Path.GetDirectoryName(target)!, Path.GetFileName(target) + ".*part"))
                    TryDelete(part);
                return;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && IsSourceFailure(ex))
            {
                last = ex;
                Log.Warn(ex, "Local AI file {File}: {Url} failed", Path.GetFileName(target), url);
            }
        }

        throw last ?? new InvalidOperationException($"no source for {Path.GetFileName(target)}");
    }

    private async Task FetchFromAsync(
        Uri url, string target, long size, string expected, Action<long>? progress, Action? verifying, CancellationToken token)
    {
        var part = MangaModelStore.PartPath(target, url);
        for (var attempt = 1; ; attempt++)
        {
            var resumed = false;
            try
            {
                resumed = await StreamAsync(url, part, size, progress, token).ConfigureAwait(false);

                verifying?.Invoke();
                var hash = await HashAsync(part, token).ConfigureAwait(false);
                if (hash != expected)
                {
                    File.Delete(part);
                    throw new InvalidDataException($"{Path.GetFileName(target)}: SHA-256 {hash} does not match the list");
                }

                File.Move(part, target, overwrite: true);
                return;
            }
            catch (InvalidDataException ex) when (resumed && attempt < Attempts && !token.IsCancellationRequested)
            {
                Log.Warn(ex, "Local AI file {File}: a resumed download did not check out; fetching it whole", Path.GetFileName(target));
            }
            catch (Exception ex) when (attempt < Attempts && !token.IsCancellationRequested &&
                                       ex is HttpRequestException or IOException && !IsFinalAnswer(ex))
            {
                Log.Warn(ex, "Local AI file {File} attempt {Attempt} failed; retrying", Path.GetFileName(target), attempt);
                await Task.Delay(RetryDelay * attempt, token).ConfigureAwait(false);
            }
        }
    }

    /// <returns>Whether it carried on from a part already there.</returns>
    private async Task<bool> StreamAsync(Uri url, string part, long size, Action<long>? progress, CancellationToken token)
    {
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > size)
        {
            File.Delete(part);
            have = 0;
        }

        if (have == size)
        {
            progress?.Invoke(have);
            return true;
        }

        // Before the request: a retry at 90% should not read 0% for as long as the connection takes.
        if (have > 0) progress?.Invoke(have);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);

            // A server that ignores the range sends the whole file again, from the first byte.
            var resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed) have = 0;
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var output = new FileStream(
                part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

            var buffer = new byte[1 << 20];
            long reported = 0;
            progress?.Invoke(have);
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                var read = await input.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                have += read;
                if (have > size)
                    throw new InvalidDataException($"{url}: more bytes than the list says");
                if (have - reported >= 1 << 20)
                {
                    reported = have;
                    progress?.Invoke(have);
                }
            }

            progress?.Invoke(have);
            if (have != size)
                throw new HttpIOException(HttpRequestError.ResponseEnded, $"{url}: {have} of {size} bytes arrived");
            return resumed;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new HttpIOException(HttpRequestError.ResponseEnded, $"{url}: nothing arrived for {StallTimeout.TotalSeconds:F0}s");
        }
    }

    private static bool IsSourceFailure(Exception ex) =>
        ex is HttpRequestException or IOException or InvalidDataException;

    // 404, 403 and the like: this source does not have the file, and will not on the next request.
    private static bool IsFinalAnswer(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } status } &&
        (int)status is >= 400 and < 500 &&
        status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
