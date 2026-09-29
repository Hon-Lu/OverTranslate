using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using NLog;

namespace OverTranslate.Services.Ocr.Manga;

internal enum MangaModelState
{
    /// <summary>This build has no manifest, so there is nothing it could download.</summary>
    Unavailable,
    NotDownloaded,
    Downloading,
    Ready,
}

/// <summary>Why a download did not finish, in terms a person can be told.</summary>
internal enum MangaDownloadFailure
{
    /// <summary>The server could not be reached, or the connection dropped mid-file.</summary>
    Network,
    /// <summary>The server answered with an error status.</summary>
    Server,
    /// <summary>A file arrived whole but did not hash to what the manifest says.</summary>
    Checksum,
    /// <summary>The files could not be written: a full disk, or a folder that cannot be written to.</summary>
    Disk,
    Other,
}

/// <summary>
/// The downloaded manga models: whether they are here, getting them, and removing them.
/// </summary>
/// <remarks>
/// <para>Kept under <c>%AppData%\OverTranslate\models\manga-vertical\v{version}</c>, next to the
/// settings and logs, so everything the app keeps outside its install folder is in one place. Not in
/// <c>%LocalAppData%\OverTranslate</c>, which is the install folder and is removed whole on uninstall.
/// <c>%AppData%</c> roams only on managed roaming profiles, which home machines do not have.</para>
///
/// <para>A file is written to a <c>.part</c> and renamed only once its size and SHA-256 match the
/// manifest, and the folder counts as ready only once every file has been checked that way and a
/// marker naming the version is written — so a download cut off anywhere leaves "not downloaded",
/// never a half-read model.</para>
///
/// <para>Nothing short of a whole download is kept. Cancelled, or given up on after every source
/// failed, the version's folder goes, files already finished included; one left by a download the
/// app was closed in the middle of goes the next time a download starts, or when the app starts
/// (<see cref="DiscardIncomplete"/>). Three hundred megabytes of half a model is not worth the
/// questions it raises — is it still there, is it used, does it resume — and starting over is the
/// one rule that needs no answer to any of them.</para>
///
/// <para>Each file is tried from the manifest's sources in order (Hugging Face first, then the GitHub
/// release). The same source is asked again, a few times, after a connection or server error, and
/// once more from the first byte if a resumed file does not hash right. The next source is tried when
/// those run out, when a whole file from this one does not hash right, or when it is too slow: under
/// <see cref="SlowBytesPerSecond"/> over the last <see cref="SlowWindow"/>. If every source turns out
/// slow, the fastest of them is used anyway, without the check — a slow line should get the models
/// slowly, not never. The download fails only when every source has.</para>
///
/// <para>Within one download, a transfer that drops is retried from where it stopped, with a range
/// request to the same source: a connection lost at 150 MB on a slow line should not cost the
/// 150 MB again, and the part being resumed was written by this download from this source moments
/// before. The whole file is still hashed at the end, and a resumed file that does not check out is
/// fetched once more from the first byte. A <c>.part</c> belongs to the source it came from (its
/// name carries a hash of the URL) and is never continued from another: Hugging Face and GitHub serve
/// the same bytes, but nothing checks that until the whole file is hashed.</para>
/// </remarks>
internal sealed class MangaModelStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const string ReadyMarker = "verified.json";
    private const int Attempts = 3;

    private readonly HttpClient _http;
    private readonly object _sync = new();
    private CancellationTokenSource? _download;

    internal MangaModelStore(MangaModelManifest? manifest, string root, HttpMessageHandler? handler = null)
    {
        Manifest = manifest;
        Root = root;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // Per request, not overall: a 170MB file on a slow line is not a hung request.
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OverTranslate");
    }

    internal static MangaModelStore CreateDefault()
    {
        MangaModelManifest? manifest = null;
        try
        {
            manifest = MangaModelManifest.Load(MangaModelManifest.ShippedPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "Manga model manifest could not be read; vertical text stays on the column pipeline");
        }

        return new MangaModelStore(manifest, DefaultRoot);
    }

    internal static string DefaultRoot => Path.Combine(AppDataPaths.Models, "manga-vertical");

    internal MangaModelManifest? Manifest { get; }

    internal string Root { get; }

    internal string? Folder => Manifest is null ? null : Path.Combine(Root, "v" + Manifest.Version);

    /// <summary>Raised on any change of <see cref="State"/>, and as a download makes progress.</summary>
    internal event EventHandler? Changed;

    internal long DownloadedBytes { get; private set; }

    /// <summary>How long to wait before the first retry of a file; doubled for the next.</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The speed under which a source is left for the next one, averaged over <see cref="SlowWindow"/>.
    /// </summary>
    /// <remarks>
    /// 256 KB/s: the whole 301 MB in about 20 minutes. GitHub's release downloads were measured at
    /// about 40 KB/s for weeks — two hours — which is six times under it; Hugging Face ran at 3.5 to
    /// 41 MB/s from the same machine, fourteen times over it even at its slowest, so a passing dip
    /// does not trip it.
    /// </remarks>
    internal long SlowBytesPerSecond { get; init; } = 256 * 1024;

    /// <summary>
    /// How long a source has to show its speed before it is judged, counted from the request, and
    /// the span the speed is averaged over after that. Long enough to ride out a stall of a few
    /// seconds; short enough that a slow source costs well under a minute.
    /// </summary>
    internal TimeSpan SlowWindow { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a transfer may go without a single byte before it counts as dropped and is retried.
    /// Also covers the one source that is never judged slow, which would otherwise wait forever.
    /// </summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    internal MangaModelState State
    {
        get
        {
            if (Manifest is null) return MangaModelState.Unavailable;
            lock (_sync)
                if (_download is not null) return MangaModelState.Downloading;
            return IsComplete() ? MangaModelState.Ready : MangaModelState.NotDownloaded;
        }
    }

    internal string PathOf(string role) =>
        Path.Combine(Folder ?? throw new InvalidOperationException("no manga model manifest"),
            Manifest!.File(role).Name);

    // The marker is written after every hash was checked; the sizes are asked again because a file
    // can be deleted or truncated by hand afterwards, and hashing 315MB on every question is not free.
    private bool IsComplete()
    {
        var folder = Folder!;
        try
        {
            var marker = Path.Combine(folder, ReadyMarker);
            if (!File.Exists(marker)) return false;
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            if (json.RootElement.GetProperty("version").GetString() != Manifest!.Version) return false;
            return Manifest.Files.All(file =>
                new FileInfo(Path.Combine(folder, file.Name)) is { Exists: true } info && info.Length == file.Size);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fetches every file this build needs. Completes normally when they are all in place and
    /// checked; throws on cancellation or when a file could not be fetched after several attempts.
    /// </summary>
    internal async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        var manifest = Manifest ?? throw new InvalidOperationException("no manga model manifest");
        CancellationTokenSource download;
        lock (_sync)
        {
            if (_download is not null) throw new InvalidOperationException("already downloading");
            _download = download = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        DownloadedBytes = 0;
        RaiseChanged();
        var folder = Folder!;
        try
        {
            if (IsComplete())
                return;
            // Whatever an earlier download left: see the remarks on keeping nothing.
            DeleteFolder(folder);
            Directory.CreateDirectory(folder);

            long before = 0;
            foreach (var file in manifest.Files)
            {
                await FetchAsync(manifest, file, folder, before, download.Token).ConfigureAwait(false);
                before += file.Size;
            }

            await File.WriteAllTextAsync(
                Path.Combine(folder, ReadyMarker),
                JsonSerializer.Serialize(new { version = manifest.Version, files = manifest.Files }),
                CancellationToken.None).ConfigureAwait(false);
            RemoveOtherVersions();
            Log.Info("Manga models v{Version} downloaded to {Folder}", manifest.Version, folder);
        }
        catch (Exception ex)
        {
            Log.Info("Manga model download {Outcome}; removing what it fetched",
                ex is OperationCanceledException ? "cancelled" : "failed");
            DeleteFolder(folder);
            throw;
        }
        finally
        {
            lock (_sync) _download = null;
            download.Dispose();
            RaiseChanged();
        }
    }

    /// <summary>Stops a download in progress, and removes everything it had fetched.</summary>
    internal void CancelDownload()
    {
        lock (_sync) _download?.Cancel();
    }

    /// <summary>Removes the downloaded models, every version of them.</summary>
    internal void Delete()
    {
        lock (_sync)
            if (_download is not null) throw new InvalidOperationException("downloading");

        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
        RaiseChanged();
    }

    /// <summary>
    /// Removes the version's folder if a download was cut off in it — the app closed mid-download.
    /// For app start; does nothing while a download is running or once the models are complete.
    /// </summary>
    internal void DiscardIncomplete()
    {
        if (Manifest is null) return;
        lock (_sync)
            if (_download is not null) return;
        if (Directory.Exists(Folder) && !IsComplete())
        {
            Log.Info("Removing the unfinished manga model download in {Folder}", Folder);
            DeleteFolder(Folder!);
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "The unfinished manga model folder {Folder} could not be removed", folder);
        }
    }

    private void RemoveOtherVersions()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Root))
                if (!string.Equals(Path.GetFullPath(folder), Path.GetFullPath(Folder!), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(folder, recursive: true);
        }
        catch (IOException ex)
        {
            Log.Warn(ex, "An older manga model folder could not be removed");
        }
    }

    private async Task FetchAsync(
        MangaModelManifest manifest, MangaModelFile file, string folder, long before, CancellationToken token)
    {
        var target = Path.Combine(folder, file.Name);

        if (File.Exists(target) && new FileInfo(target).Length == file.Size &&
            await HashAsync(target, token).ConfigureAwait(false) == file.Sha256.ToLowerInvariant())
        {
            Report(before + file.Size);
            return;
        }

        Exception? last = null;
        var slow = new List<(MangaModelSource Source, Uri Url, double BytesPerSecond)>();

        foreach (var (source, url) in manifest.SourcesOf(file))
        {
            try
            {
                await FetchFromAsync(file, url, target, before, judgeSpeed: true, token).ConfigureAwait(false);
                Finished(file, source, target);
                return;
            }
            catch (SlowSourceException ex)
            {
                // Kept, with what it had fetched, in case nothing else is faster.
                slow.Add((source, url, ex.BytesPerSecond));
                last = ex;
                Log.Warn("Manga model {File}: {Source} is too slow ({Rate:F0} KB/s), trying the next source",
                    file.Name, source.Name, ex.BytesPerSecond / 1024);
            }
            catch (Exception ex) when (!token.IsCancellationRequested && IsSourceFailure(ex))
            {
                last = ex;
                Log.Warn(ex, "Manga model {File}: {Source} failed, trying the next source", file.Name, source.Name);
            }
        }

        foreach (var (source, url, _) in slow.OrderByDescending(entry => entry.BytesPerSecond))
        {
            Log.Info("Manga model {File}: every source was slow or failed; going on with {Source}", file.Name, source.Name);
            try
            {
                await FetchFromAsync(file, url, target, before, judgeSpeed: false, token).ConfigureAwait(false);
                Finished(file, source, target);
                return;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && IsSourceFailure(ex))
            {
                last = ex;
                Log.Warn(ex, "Manga model {File}: {Source} failed as well", file.Name, source.Name);
            }
        }

        throw last ?? new InvalidOperationException($"no source for {file.Name}");
    }

    private static bool IsSourceFailure(Exception ex) =>
        ex is HttpRequestException or IOException or InvalidDataException;

    private static void Finished(MangaModelFile file, MangaModelSource source, string target)
    {
        Log.Info("Manga model {File} fetched from {Source}", file.Name, source.Name);
        // Whatever other sources left behind, and a .part from before sources had their own.
        foreach (var part in Directory.EnumerateFiles(Path.GetDirectoryName(target)!, Path.GetFileName(target) + ".*part"))
            TryDelete(part);
    }

    /// <summary>One file from one source, retried there on errors; throws when this source is done.</summary>
    private async Task FetchFromAsync(
        MangaModelFile file, Uri url, string target, long before, bool judgeSpeed, CancellationToken token)
    {
        var part = PartPath(target, url);
        for (int attempt = 1; ; attempt++)
        {
            bool resumed = false;
            try
            {
                resumed = await StreamToAsync(url, part, file.Size, before, judgeSpeed, token).ConfigureAwait(false);

                var hash = await HashAsync(part, token).ConfigureAwait(false);
                if (hash != file.Sha256.ToLowerInvariant())
                {
                    File.Delete(part);
                    throw new InvalidDataException($"{file.Name}: SHA-256 {hash} does not match the manifest");
                }

                File.Move(part, target, overwrite: true);
                return;
            }
            catch (InvalidDataException ex) when (resumed && attempt < Attempts && !token.IsCancellationRequested)
            {
                // The part it was resumed from may be what was wrong; once more from the first byte.
                Log.Warn(ex, "Manga model {File}: a resumed download did not check out; fetching it whole", file.Name);
            }
            catch (Exception ex) when (attempt < Attempts && !token.IsCancellationRequested &&
                                       ex is HttpRequestException or IOException)
            {
                Log.Warn(ex, "Manga model {File} attempt {Attempt} from {Url} failed; retrying", file.Name, attempt, url);
                await Task.Delay(RetryDelay * attempt, token).ConfigureAwait(false);
            }
        }
    }

    // <file>.<first 8 hex of the URL's SHA-256>.part: see the remarks on resuming.
    internal static string PartPath(string target, Uri url) =>
        $"{target}.{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..8].ToLowerInvariant()}.part";

    /// <returns>Whether it carried on from a part already there.</returns>
    private async Task<bool> StreamToAsync(
        Uri url, string part, long size, long before, bool judgeSpeed, CancellationToken token)
    {
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > size)
        {
            File.Delete(part);
            have = 0;
        }

        if (have == size)
        {
            Report(before + have);
            return true;
        }

        using var watch = new TransferWatch(this, judgeSpeed, token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, watch.Token).ConfigureAwait(false);

            // A server that ignores the range sends the whole file again, from the first byte.
            bool resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed) have = 0;
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync(watch.Token).ConfigureAwait(false);
            await using var output = new FileStream(
                part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            long lastReport = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer, watch.Token).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), watch.Token).ConfigureAwait(false);
                have += read;
                watch.Received(read);
                if (have > size)
                    throw new InvalidDataException($"{url}: more bytes than the manifest says");
                if (have - lastReport >= 1 << 20)
                {
                    lastReport = have;
                    Report(before + have);
                }
            }

            Report(before + have);
            if (have != size)
                throw new HttpIOException(HttpRequestError.ResponseEnded, $"{url}: {have} of {size} bytes arrived");
            return resumed;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && watch.Verdict is { } verdict)
        {
            throw verdict;
        }
    }

    /// <summary>
    /// Watches one transfer: cancels it when it has been under <see cref="SlowBytesPerSecond"/> for
    /// <see cref="SlowWindow"/> (if asked to judge speed), or has had nothing for <see cref="StallTimeout"/>.
    /// </summary>
    private sealed class TransferWatch : IDisposable
    {
        private readonly MangaModelStore _store;
        private readonly bool _judgeSpeed;
        private readonly CancellationTokenSource _cancel;
        private readonly System.Threading.Timer _timer;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();
        private long _bytes;
        private long _lastBytes;
        private TimeSpan _lastProgress;

        internal TransferWatch(MangaModelStore store, bool judgeSpeed, CancellationToken token)
        {
            _store = store;
            _judgeSpeed = judgeSpeed;
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            _samples.Enqueue((TimeSpan.Zero, 0));
            var tick = TimeSpan.FromTicks(Math.Clamp(store.SlowWindow.Ticks / 20, TimeSpan.TicksPerMillisecond * 10, TimeSpan.TicksPerSecond));
            _timer = new System.Threading.Timer(_ => Check(), null, tick, tick);
        }

        internal CancellationToken Token => _cancel.Token;

        /// <summary>Why the transfer was stopped, when it was this that stopped it.</summary>
        internal Exception? Verdict { get; private set; }

        internal void Received(int bytes) => Interlocked.Add(ref _bytes, bytes);

        private void Check()
        {
            if (Verdict is not null) return;
            var now = _clock.Elapsed;
            var bytes = Interlocked.Read(ref _bytes);

            if (bytes != _lastBytes)
            {
                _lastBytes = bytes;
                _lastProgress = now;
            }
            else if (now - _lastProgress >= _store.StallTimeout)
            {
                Stop(new HttpIOException(HttpRequestError.ResponseEnded,
                    $"nothing arrived for {_store.StallTimeout.TotalSeconds:F0}s"));
                return;
            }

            _samples.Enqueue((now, bytes));
            while (_samples.Count > 1 && now - _samples.ElementAt(1).At >= _store.SlowWindow)
                _samples.Dequeue();

            var (from, fromBytes) = _samples.Peek();
            if (_judgeSpeed && now >= _store.SlowWindow && now - from >= _store.SlowWindow)
            {
                var rate = (bytes - fromBytes) / (now - from).TotalSeconds;
                if (rate < _store.SlowBytesPerSecond)
                    Stop(new SlowSourceException(rate));
            }
        }

        private void Stop(Exception verdict)
        {
            Verdict = verdict;
            try
            {
                _cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            _cancel.Dispose();
        }
    }

    private sealed class SlowSourceException(double bytesPerSecond)
        : Exception($"{bytesPerSecond / 1024:F0} KB/s")
    {
        internal double BytesPerSecond { get; } = bytesPerSecond;
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

    /// <summary>What <see cref="DownloadAsync"/>'s exception means to the person who pressed download.</summary>
    internal static MangaDownloadFailure Classify(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: not null } => MangaDownloadFailure.Server,
        HttpRequestException or HttpIOException => MangaDownloadFailure.Network,
        IOException { InnerException: System.Net.Sockets.SocketException } => MangaDownloadFailure.Network,
        InvalidDataException => MangaDownloadFailure.Checksum,
        IOException or UnauthorizedAccessException => MangaDownloadFailure.Disk,
        _ => MangaDownloadFailure.Other,
    };

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    private void Report(long bytes)
    {
        DownloadedBytes = bytes;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
