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
/// <para>A file is written to <c>.part</c> and renamed only once its size and SHA-256 match the
/// manifest, and the folder counts as ready only once every file has been checked that way and a
/// marker naming the version is written — so a download cut off anywhere leaves "not downloaded",
/// never a half-read model. An interrupted <c>.part</c> is resumed with a range request; a failed or
/// mismatched file is fetched again, a few times, before the download gives up.</para>
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
        try
        {
            var folder = Folder!;
            Directory.CreateDirectory(folder);
            File.Delete(Path.Combine(folder, ReadyMarker));

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
        finally
        {
            lock (_sync) _download = null;
            download.Dispose();
            RaiseChanged();
        }
    }

    /// <summary>Stops a download in progress; what was fetched so far is kept for next time.</summary>
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
        var part = target + ".part";

        if (File.Exists(target) && new FileInfo(target).Length == file.Size &&
            await HashAsync(target, token).ConfigureAwait(false) == file.Sha256.ToLowerInvariant())
        {
            Report(before + file.Size);
            return;
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await StreamToAsync(manifest.UrlOf(file), part, file.Size, before, token).ConfigureAwait(false);

                var hash = await HashAsync(part, token).ConfigureAwait(false);
                if (hash != file.Sha256.ToLowerInvariant())
                {
                    File.Delete(part);
                    throw new InvalidDataException($"{file.Name}: SHA-256 {hash} does not match the manifest");
                }

                File.Move(part, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < Attempts && !token.IsCancellationRequested &&
                                       ex is HttpRequestException or IOException or InvalidDataException)
            {
                Log.Warn(ex, "Manga model {File} attempt {Attempt} failed; retrying", file.Name, attempt);
                await Task.Delay(RetryDelay * attempt, token).ConfigureAwait(false);
            }
        }
    }

    private async Task StreamToAsync(Uri url, string part, long size, long before, CancellationToken token)
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
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);

        // A server that ignores the range sends the whole file again, from the first byte.
        if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            have = 0;
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(
            part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[1 << 16];
        long lastReport = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            have += read;
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
