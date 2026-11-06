using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using NLog;
using OverTranslate.Services.Ocr.Manga;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// A recognition model for one writing system, downloaded the first time someone asks to read it.
/// </summary>
/// <remarks>
/// <para>Downloaded rather than shipped because each one only ever serves the people who read that
/// script, and "it is only a few megabytes" is equally true of every script there is. The installer
/// keeps only the model behind 自動, which reads Chinese, English and Japanese; every other language,
/// Korean included, is a pack.</para>
///
/// <para>The files live and are checked exactly as the manga models are, so the same
/// <see cref="MangaModelStore"/> does the work, given this pack's own manifest and folder. The
/// detector and classifier are the shipped shared ones; a pack is only a recogniser and its
/// dictionary.</para>
///
/// <para>Public, with these properties, because the pickers' item templates bind to it. Everything a
/// template reads changes on the UI thread.</para>
/// </remarks>
public sealed class OcrLanguagePack : INotifyPropertyChanged
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly string _languagesKey;
    private bool _ready;
    private bool _downloading;
    private int _percent;

    internal OcrLanguagePack(string key, string languagesKey, IReadOnlyList<string> codes, MangaModelStore store)
    {
        Key = key;
        _languagesKey = languagesKey;
        Codes = codes;
        Store = store;
        store.Changed += (_, _) => OnUi(Refresh);
        // For the life of the app, like the pack itself: RowText is the one thing here a binding holds
        // in the interface's language.
        LocalizationService.LanguageChanged += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RowText)));
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The state changed, on the UI thread: ready, downloading, progress or failure.</summary>
    internal event EventHandler? Changed;

    /// <summary>The name <see cref="OnnxOcrEngine"/> knows this model by.</summary>
    internal string Key { get; }

    /// <summary>The source languages this model reads.</summary>
    internal IReadOnlyList<string> Codes { get; }

    internal MangaModelStore Store { get; }

    public bool IsReady => _ready;

    public bool IsDownloading => _downloading;

    public int Percent => _percent;

    /// <summary>Every language this model reads, as a phrase: 俄文和烏克蘭文.</summary>
    public string LanguagesText => LocalizationService.Get(_languagesKey);

    internal int Megabytes =>
        Store.Manifest is { } manifest ? (int)Math.Max(1, Math.Round(manifest.TotalBytes / 1_000_000.0)) : 0;

    /// <summary>Whether a picker's row has anything to say beside the language's name.</summary>
    public bool ShowsRowHint => !_ready;

    public bool ShowsDownloadMark => !_ready && !_downloading;

    /// <summary>What a picker's row says beside the name: the size, or how far along it is.</summary>
    public string RowText => _downloading ? $"{_percent}%" : LocalizationService.Format("S.OcrPack.RowSize", Megabytes);

    /// <summary>Why the last download did not finish, until the next attempt or until it is dismissed.</summary>
    internal (MangaDownloadFailure Kind, int? Status)? Failure { get; private set; }

    internal string? FailureText => Failure switch
    {
        null => null,
        (MangaDownloadFailure.Server, var status) =>
            LocalizationService.Format("S.Settings.MangaModelsErrorServer", status ?? 0),
        (MangaDownloadFailure.Network, _) => LocalizationService.Get("S.Settings.MangaModelsErrorNetwork"),
        (MangaDownloadFailure.Checksum, _) => LocalizationService.Get("S.Settings.MangaModelsErrorChecksum"),
        (MangaDownloadFailure.Disk, _) => LocalizationService.Get("S.Settings.MangaModelsErrorDisk"),
        _ => LocalizationService.Get("S.Settings.MangaModelsErrorOther"),
    };

    internal string PathOf(string role) => Store.PathOf(role);

    /// <summary>
    /// Starts the download and returns. It belongs to the pack rather than to whichever window asked,
    /// because that window can close while it runs: a capture toolbar put away mid-download still
    /// finds the language ready the next time.
    /// </summary>
    internal void StartDownload() => _ = DownloadAsync();

    internal async Task DownloadAsync()
    {
        // Asked about the store, not _downloading, which hears of it a dispatch later: a second press
        // in that gap would otherwise come back as a failed download.
        if (Store.State == MangaModelState.Downloading) return;
        Failure = null;
        try
        {
            await Store.DownloadAsync();
        }
        catch (OperationCanceledException)
        {
            // Cancelled from a card; the store has removed what arrived.
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "OCR language pack {Key} download failed", Key);
            Failure = (MangaModelStore.Classify(ex),
                ex is HttpRequestException { StatusCode: { } status } ? (int)status : null);
        }

        OnUi(Refresh);
    }

    internal void CancelDownload() => Store.CancelDownload();

    internal void ClearFailure()
    {
        if (Failure is null) return;
        Failure = null;
        Refresh();
    }

    /// <summary>Removes the model. Whoever had one of its languages selected falls back; see <see cref="Controls.OcrSourcePicker"/>.</summary>
    internal void Delete()
    {
        Store.Delete();
        OnUi(Refresh);
    }

    internal void Refresh()
    {
        var state = Store.State;
        var downloading = state == MangaModelState.Downloading;
        var total = Store.TotalBytes;
        var percent = downloading && total > 0
            ? (int)Math.Clamp(Store.DownloadedBytes * 100 / total, 0, 100)
            : 0;

        // Reported per chunk read; a template only needs to hear when the number it shows moved. The
        // failure is compared too, since a download that fails ends in the state it started from.
        var ready = state == MangaModelState.Ready;
        if (ready == _ready && downloading == _downloading && percent == _percent && _lastFailure == Failure)
            return;

        _ready = ready;
        _downloading = downloading;
        _percent = percent;
        _lastFailure = Failure;

        foreach (var name in new[]
                 {
                     nameof(IsReady), nameof(IsDownloading), nameof(Percent),
                     nameof(ShowsRowHint), nameof(ShowsDownloadMark), nameof(RowText),
                 })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private (MangaDownloadFailure Kind, int? Status)? _lastFailure;

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}

/// <summary>The language packs this build knows of.</summary>
internal static class OcrLanguagePacks
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    internal static IReadOnlyList<OcrLanguagePack> All { get; } =
    [
        // One model for both: its dictionary holds the whole Cyrillic range, і ї є ґ included.
        Create("cyrillic", "S.OcrPack.CyrillicLanguages", ["RU", "UK"]),
        // Shipped in the installer until the automatic model became the only one there.
        Create("korean", "S.Lang.Korean", ["KO"]),
    ];

    internal static OcrLanguagePack? ForCode(string? code) =>
        code is null
            ? null
            : All.FirstOrDefault(pack => pack.Codes.Contains(code, StringComparer.OrdinalIgnoreCase));

    internal static OcrLanguagePack? ForModelKey(string modelKey) =>
        All.FirstOrDefault(pack => pack.Key == modelKey);

    /// <summary>Whether this language can be read right now: it needs no pack, or its pack is here.</summary>
    internal static bool IsUsable(string? code) => ForCode(code) is not { IsReady: false };

    /// <summary>A download the app was closed in the middle of is not kept; see <see cref="MangaModelStore.DiscardIncomplete"/>.</summary>
    internal static void DiscardIncomplete()
    {
        foreach (var pack in All)
            pack.Store.DiscardIncomplete();
    }

    private static OcrLanguagePack Create(string key, string languagesKey, IReadOnlyList<string> codes)
    {
        MangaModelManifest? manifest = null;
        try
        {
            manifest = MangaModelManifest.Load(Path.Combine(AppContext.BaseDirectory, "ocrmodels", $"lang-{key}.json"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "OCR language pack manifest {Key} could not be read", key);
        }

        return new OcrLanguagePack(key, languagesKey, codes,
            new MangaModelStore(manifest, Path.Combine(AppDataPaths.Models, "ocr-" + key)));
    }
}
