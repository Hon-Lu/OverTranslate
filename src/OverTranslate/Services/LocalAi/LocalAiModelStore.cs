using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NLog;

namespace OverTranslate.Services.LocalAi;

internal enum LocalAiDownloadPhase
{
    None,
    Downloading,
    /// <summary>The bytes are all here and the SHA-256 is being taken.</summary>
    Verifying,
}

/// <summary>
/// The downloaded models: which are here, getting one, removing one. The llama.cpp build comes along
/// with the first model that needs it.
/// </summary>
/// <remarks>
/// <para>Each model has a folder of its own, <c>models\local-ai\{id}</c>
/// (<see cref="AppDataPaths.Models"/>), which counts as downloaded only once its hash has been
/// checked and a marker naming that hash written. The marker is what is read afterwards, with the
/// size: hashing five gigabytes every time a settings window opens is not free.</para>
///
/// <para>One download at a time. It belongs to this store rather than to the window that started
/// it, so closing the window leaves it running.</para>
///
/// <para>A download that fails keeps what it fetched, for the retry to carry on from. Cancelling is
/// someone saying they do not want it, and takes its bytes with it.</para>
/// </remarks>
internal sealed class LocalAiModelStore
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const string ReadyMarker = "verified.json";

    private readonly LocalAiDownloader _downloader;
    private readonly object _sync = new();
    private CancellationTokenSource? _download;
    private Stopwatch? _clock;
    private long _resumedFrom;

    internal LocalAiModelStore(LocalAiCatalog? catalog, string root, LocalAiRuntimeStore? runtime, LocalAiDownloader downloader)
    {
        Catalog = catalog;
        Root = root;
        Runtime = runtime;
        _downloader = downloader;
    }

    internal static LocalAiModelStore CreateDefault()
    {
        LocalAiCatalog? catalog = null;
        try
        {
            catalog = LocalAiCatalog.Load(LocalAiCatalog.ShippedPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "The local AI model list could not be read");
        }

        var downloader = new LocalAiDownloader();
        return new LocalAiModelStore(
            catalog,
            DefaultRoot,
            catalog is null ? null : new LocalAiRuntimeStore(catalog.Runtime, LocalAiRuntimeStore.DefaultRoot, downloader),
            downloader);
    }

    internal static string DefaultRoot => Path.Combine(AppDataPaths.Models, "local-ai");

    /// <summary>Null when the shipped list could not be read, and then nothing can be offered.</summary>
    internal LocalAiCatalog? Catalog { get; }

    internal string Root { get; }

    internal LocalAiRuntimeStore? Runtime { get; }

    /// <summary>Raised on any change of state, and as a download makes progress; on any thread.</summary>
    internal event EventHandler? Changed;

    internal string FolderOf(LocalAiModel model) => Path.Combine(Root, model.Id);

    internal string PathOf(LocalAiModel model) => Path.Combine(FolderOf(model), model.File);

    /// <summary>The model being fetched, while one is.</summary>
    internal LocalAiModel? Downloading { get; private set; }

    internal LocalAiDownloadPhase Phase { get; private set; }

    /// <summary>Bytes received so far by the download in progress, the runtime's included.</summary>
    internal long DownloadedBytes { get; private set; }

    /// <summary>What <see cref="DownloadedBytes"/> will come to.</summary>
    internal long TotalBytes { get; private set; }

    /// <summary>The fraction done, 0 to 1; 0 when nothing is downloading.</summary>
    internal double Fraction => TotalBytes > 0 ? Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1) : 0;

    /// <summary>How long the rest should take at the speed so far, once there is a speed to go by.</summary>
    internal TimeSpan? Remaining
    {
        get
        {
            var clock = _clock;
            var fetched = DownloadedBytes - _resumedFrom;
            if (clock is null || fetched <= 0 || clock.Elapsed < TimeSpan.FromSeconds(2)) return null;
            return TimeSpan.FromSeconds((TotalBytes - DownloadedBytes) / (fetched / clock.Elapsed.TotalSeconds));
        }
    }

    internal bool IsDownloaded(LocalAiModel model)
    {
        try
        {
            var marker = Path.Combine(FolderOf(model), ReadyMarker);
            if (!File.Exists(marker)) return false;
            if (new FileInfo(PathOf(model)) is not { Exists: true } file || file.Length != model.Size) return false;
            using var json = JsonDocument.Parse(File.ReadAllText(marker));
            return string.Equals(json.RootElement.GetProperty("sha256").GetString(), model.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    internal IReadOnlyList<LocalAiModel> Downloaded =>
        Catalog is null ? [] : [.. Catalog.Models.Where(IsDownloaded)];

    /// <summary>
    /// How many bytes getting <paramref name="model"/> will fetch: the model, and the runtime when it
    /// is not here yet. What a failed attempt already fetched is not taken off: the button says the
    /// size of the model, not the size of the rest.
    /// </summary>
    internal long DownloadSize(LocalAiModel model) => model.Size + RuntimeDownloadSize;

    /// <summary>What the runtime still takes over the wire: nothing once it is here.</summary>
    internal long RuntimeDownloadSize => Runtime is { IsReady: false } runtime ? runtime.Runtime.Size : 0;

    /// <summary>Fetches <paramref name="model"/> and whatever it needs, and checks it.</summary>
    /// <exception cref="OperationCanceledException">Cancelled; nothing of it is left.</exception>
    internal async Task DownloadAsync(LocalAiModel model, CancellationToken cancellationToken = default)
    {
        if (Runtime is not { } runtime) throw new InvalidOperationException("no local AI model list");

        CancellationTokenSource download;
        lock (_sync)
        {
            if (_download is not null) throw new InvalidOperationException("already downloading");
            _download = download = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        var runtimeSize = RuntimeDownloadSize;
        Downloading = model;
        Phase = LocalAiDownloadPhase.Downloading;
        TotalBytes = runtimeSize + model.Size;
        DownloadedBytes = 0;
        _resumedFrom = -1;
        _clock = Stopwatch.StartNew();
        RaiseChanged();
        try
        {
            if (runtimeSize > 0)
                await runtime.EnsureAsync(Report, download.Token).ConfigureAwait(false);

            await _downloader.FetchAsync(
                [new Uri(model.Url)], PathOf(model), model.Size, model.Sha256,
                bytes => Report(runtimeSize + bytes),
                () =>
                {
                    Phase = LocalAiDownloadPhase.Verifying;
                    RaiseChanged();
                },
                download.Token).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                Path.Combine(FolderOf(model), ReadyMarker),
                JsonSerializer.Serialize(new { sha256 = model.Sha256 }),
                CancellationToken.None).ConfigureAwait(false);
            Log.Info("Local AI model {Model:l} downloaded to {Folder}", model.Id, FolderOf(model));
        }
        catch (OperationCanceledException)
        {
            Log.Info("Local AI model {Model:l} download cancelled; removing what it fetched", model.Id);
            DeleteFolder(FolderOf(model));
            if (runtimeSize > 0) runtime.DiscardIncomplete();
            throw;
        }
        finally
        {
            lock (_sync) _download = null;
            Downloading = null;
            Phase = LocalAiDownloadPhase.None;
            TotalBytes = DownloadedBytes = 0;
            _clock = null;
            download.Dispose();
            RaiseChanged();
        }
    }

    internal void CancelDownload()
    {
        lock (_sync) _download?.Cancel();
    }

    /// <summary>Removes a model, and what a failed download of it left.</summary>
    /// <remarks>The caller has made sure nothing has its file open.</remarks>
    internal void Delete(LocalAiModel model)
    {
        lock (_sync)
            if (_download is not null && Downloading == model) throw new InvalidOperationException("downloading");

        if (Directory.Exists(FolderOf(model)))
            Directory.Delete(FolderOf(model), recursive: true);
        RaiseChanged();
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
            Log.Warn(ex, "The local AI model folder {Folder} could not be removed", folder);
        }
    }

    private void Report(long bytes)
    {
        // The first report of a resumed file is what was already here, and took no time to fetch.
        if (_resumedFrom < 0) _resumedFrom = bytes;
        DownloadedBytes = bytes;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>The free space where the models are kept: the drive's letter and its free bytes.</summary>
    /// <returns>Null when it cannot be told — a network path, a drive that went away.</returns>
    internal (string Drive, long FreeBytes)? FreeSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(Root));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return (root.TrimEnd('\\', ':'), new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// How many bytes must be free to take <paramref name="model"/>: what it fetches, and room to
    /// unpack the runtime next to its zip.
    /// </summary>
    internal long SpaceNeeded(LocalAiModel model)
    {
        const long unpackedRuntime = 100_000_000;
        return DownloadSize(model) + (RuntimeDownloadSize > 0 ? unpackedRuntime : 0);
    }
}
