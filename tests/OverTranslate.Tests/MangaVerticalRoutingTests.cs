using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// When the manga models are not used, the vertical path must be the column pipeline, unchanged.
/// </summary>
public sealed class MangaVerticalRoutingTests : IDisposable
{
    private static readonly string[] Roles = ["detector", "encoder", "decoder-cross", "decoder-step", "vocabulary"];
    private static readonly byte[] NotAModel = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-manga-route-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly OcrTextBlock[] Columns =
    [
        new("縦書き", new System.Windows.Rect(72, 10, 8, 30), Confidence: 0.9),
        new("二列目", new System.Windows.Rect(40, 10, 8, 30), Confidence: 0.9),
    ];

    private MangaModelStore Store() =>
        new(new MangaModelManifest("1", [new MangaModelSource("test", "https://models.example/{name}", null)],
            [.. Roles.Select(role => new MangaModelFile(role, role + ".bin", NotAModel.Length,
                Convert.ToHexString(SHA256.HashData(NotAModel)).ToLowerInvariant()))]),
            _root, new Serve());

    private static (DirectMlDevice.Adapter?, string?) NoGpu() => (null, "only a software adapter (WARP)");

    // Not a real adapter; creating a DirectML session on it fails, which is the point.
    private static (DirectMlDevice.Adapter?, string?) BrokenGpu() =>
        (new DirectMlDevice.Adapter(99, "broken", 0x10DE, 1, false, 0), null);

    private static async Task<List<OcrTextBlock>> Columnsread(MangaOcrEngine? manga, string language = "JA")
    {
        using var page = new Bitmap(100, 60);
        return await OcrService.RecognizeVerticalAsync(new FixedEngine(), page, language, default, manga);
    }

    private static string Signature(IEnumerable<OcrTextBlock> blocks) =>
        string.Join("|", blocks.Select(b => $"{b.Text}@{b.Bounds}/{b.RenderGlyphHeight}/{b.Confidence}/{b.RunsAcross}"));

    [Fact]
    public async Task ModelsNotDownloaded_ReadsTheColumns()
    {
        using var manga = new MangaOcrEngine(Store(), BrokenGpu);

        Assert.Equal("models not downloaded", manga.UnavailableReason);
        Assert.Equal(MangaUnavailable.NotDownloaded, manga.Unavailable);
        Assert.Equal(Signature(await Columnsread(null)), Signature(await Columnsread(manga)));
    }

    [Fact]
    public async Task NoHardwareGpu_ReadsTheColumns_WithoutTryingToLoad()
    {
        var store = Store();
        await store.DownloadAsync();
        using var manga = new MangaOcrEngine(store, NoGpu);

        Assert.Equal("only a software adapter (WARP)", manga.UnavailableReason);
        Assert.Equal(MangaUnavailable.NoGpu, manga.Unavailable);
        Assert.Equal(Signature(await Columnsread(null)), Signature(await Columnsread(manga)));
    }

    [Fact]
    public async Task ALoadThatFails_ReadsTheColumns_AndIsRemembered()
    {
        var store = Store();
        await store.DownloadAsync();
        using var manga = new MangaOcrEngine(store, BrokenGpu);
        Assert.Null(manga.UnavailableReason);

        Assert.Equal(Signature(await Columnsread(null)), Signature(await Columnsread(manga)));
        // This store downloads no DirectML, so no session is even tried: see DirectMlDevice.
        Assert.StartsWith("DirectML could not be loaded", manga.UnavailableReason);
        Assert.Equal(MangaUnavailable.LoadFailed, manga.Unavailable);

        // Only new files clear it.
        store.Delete();
        await store.DownloadAsync();
        Assert.Null(manga.UnavailableReason);
        Assert.Equal(MangaUnavailable.None, manga.Unavailable);
    }

    [Fact]
    public async Task ADownloadedDirectMlThatIsNotTheRightFile_IsNotLoaded_AndSaysWhy()
    {
        var dll = Convert.ToHexString(SHA256.HashData(NotAModel)).ToLowerInvariant();
        var manifest = new MangaModelManifest("1", [new MangaModelSource("test", "https://models.example/{name}", null)],
            [.. Roles.Select(role => new MangaModelFile(role, role + ".bin", NotAModel.Length, dll))])
        {
            DirectMl = new MangaModelManifest("9.9.9", [new MangaModelSource("test", "https://runtime.example/{name}", null)],
                [new MangaModelFile("directml", "DirectML.dll", NotAModel.Length, dll)]),
        };
        // Hashes right — it is what this manifest pins — but carries no signature at all.
        var store = new MangaModelStore(manifest, Path.Combine(_root, "models"), new Serve(), Path.Combine(_root, "runtimes"))
        {
            CheckRuntime = _ => null,
        };
        await store.DownloadAsync();
        using var manga = new MangaOcrEngine(store, BrokenGpu);

        Assert.Equal(Signature(await Columnsread(null)), Signature(await Columnsread(manga)));
        Assert.Equal(MangaUnavailable.LoadFailed, manga.Unavailable);
        Assert.Contains("Authenticode", manga.UnavailableReason);

        // Changed after the download: the hash says so before the signature is asked.
        File.WriteAllBytes(store.RuntimePath!, [8, 7, 6, 5, 4, 3, 2, 1]);
        Assert.Contains("does not hash", DirectMlDevice.Load(store.RuntimePath, manifest.DirectMl.Files[0]));
        File.Delete(store.RuntimePath!);
        Assert.Contains("not been downloaded", DirectMlDevice.Load(store.RuntimePath, manifest.DirectMl.Files[0]));
    }

    [Fact]
    public async Task AnotherLanguage_NeverReachesTheModels()
    {
        var store = Store();
        await store.DownloadAsync();
        using var manga = new MangaOcrEngine(store, BrokenGpu);

        Assert.Equal(Signature(await Columnsread(null, "ZH-HANT")), Signature(await Columnsread(manga, "ZH-HANT")));
        // Had it tried, the broken adapter would have been found out.
        Assert.Null(manga.UnavailableReason);
    }

    [Fact]
    public async Task TheRealtimePath_FallsBackTheSameWay()
    {
        var store = Store();
        await store.DownloadAsync();
        using var manga = new MangaOcrEngine(store, NoGpu);
        using var page = new Bitmap(100, 60);

        var without = await OcrService.TryRecognizeVerticalAsync(new FixedEngine(), page, "JA", null, default);
        var with = await OcrService.TryRecognizeVerticalAsync(new FixedEngine(), page, "JA", null, default, manga);

        Assert.Equal(Signature(without!), Signature(with!));
    }

    [Fact]
    public void AnOcrServiceWithoutModels_NeverSkipsTheSecondLook()
    {
        using var ocr = new OcrService();
        Assert.False(ocr.ReadsVerticalWithMangaModels("JA"));
    }

    [Theory]
    [InlineData(false, 0x10DE, 0x2484, true)]
    [InlineData(true, 0x10DE, 0x2484, false)]
    [InlineData(false, 0x1414, 0x8C, false)]    // Microsoft Basic Render Driver
    public void OnlyHardwareAdaptersAreChosen(bool softwareFlag, uint vendor, uint device, bool chosen)
    {
        var adapter = new DirectMlDevice.Adapter(0, "a", vendor, device, softwareFlag, 0);
        Assert.Equal(chosen, DirectMlDevice.Choose([adapter]).Adapter is not null);
    }

    [Fact]
    public void TheFirstHardwareAdapterIsChosen_EvenAfterASoftwareOne()
    {
        var (adapter, reason) = DirectMlDevice.Choose(
        [
            new DirectMlDevice.Adapter(0, "Microsoft Basic Render Driver", 0x1414, 0x8C, true, 0),
            new DirectMlDevice.Adapter(1, "GPU", 0x10DE, 0x2484, false, 8L << 30),
        ]);

        Assert.Null(reason);
        Assert.Equal(1, adapter!.Value.Index);
    }

    [Fact]
    public void NoAdapters_SaysSo() =>
        Assert.Equal("no graphics adapter", DirectMlDevice.Choose([]).Reason);

    private sealed class FixedEngine : IOcrEngine
    {
        public Task<List<OcrTextBlock>> RecognizeAsync(
            Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken = default,
            bool verticalText = false) =>
            Task.FromResult(Columns.AsDetected());

        public Task<List<OcrTextBlock>?> TryRecognizeAsync(
            Bitmap bitmap, string sourceLanguage, int? maxDetectSize = null,
            CancellationToken cancellationToken = default, bool verticalText = false) =>
            Task.FromResult<List<OcrTextBlock>?>(Columns.AsDetected());

        public void Dispose()
        {
        }
    }

    private sealed class Serve : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(NotAModel) });
        }
    }

    // ── Before downloading: can this machine run them at all ─────────────────

    private MangaModelStore Store(Serve server) =>
        new(new MangaModelManifest("1", [new MangaModelSource("test", "https://models.example/{name}", null)],
            [.. Roles.Select(role => new MangaModelFile(role, role + ".bin", NotAModel.Length,
                Convert.ToHexString(SHA256.HashData(NotAModel)).ToLowerInvariant()))]),
            _root, server);

    private static (DirectMlDevice.Adapter?, string?) AGpu() =>
        (new DirectMlDevice.Adapter(0, "Some GPU", 0x10DE, 1, false, 8L << 30), null);

    [Fact]
    public async Task WithAGpu_TheModelsCanBeDownloaded()
    {
        var server = new Serve();
        using var manga = new MangaOcrEngine(Store(server), AGpu);

        Assert.Equal(MangaUnavailable.None, manga.DeviceSupport);
        await manga.DownloadModelsAsync();

        Assert.Equal(Roles.Length, server.Requests);
    }

    [Fact]
    public async Task WithoutAGpu_NothingIsDownloaded_AndItIsKnownBeforehand()
    {
        var server = new Serve();
        var store = Store(server);
        using var manga = new MangaOcrEngine(store, NoGpu);

        // Asked with nothing downloaded: the answer does not wait for 301 MB.
        Assert.Equal(MangaModelState.NotDownloaded, store.State);
        Assert.Equal(MangaUnavailable.NoGpu, manga.DeviceSupport);

        await Assert.ThrowsAsync<NotSupportedException>(() => manga.DownloadModelsAsync());
        Assert.Equal(0, server.Requests);
        Assert.False(Directory.Exists(Path.Combine(_root, "v1")));
    }

    [Fact]
    public void TheAdapterCheck_RunsOnce()
    {
        var asked = 0;
        using var manga = new MangaOcrEngine(Store(new Serve()), () => { asked++; return NoGpu(); });

        _ = manga.DeviceSupport;
        _ = manga.DeviceSupport;
        _ = manga.Unavailable;

        Assert.Equal(1, asked);
    }

    [Theory]
    // Nothing downloaded: a GPU means it can be; none means it cannot.
    [InlineData("NotDownloaded", "None", "NotDownloaded", false, "NotDownloaded")]
    [InlineData("NotDownloaded", "NoGpu", "NotDownloaded", false, "Unsupported")]
    [InlineData("NotDownloaded", "None", "NotDownloaded", true, "Failed")]
    // Downloaded on a machine that no longer has a GPU: not supported, still deletable.
    [InlineData("Ready", "NoGpu", "NoGpu", false, "Unsupported")]
    // Downloaded and a GPU, but loading or reading failed: the existing 無法使用.
    [InlineData("Ready", "None", "LoadFailed", false, "Unusable")]
    [InlineData("Ready", "None", "ReadFailed", false, "Unusable")]
    [InlineData("Ready", "None", "None", false, "Ready")]
    // Switched off on the card: its own state, not 無法使用.
    [InlineData("Ready", "None", "Disabled", false, "Disabled")]
    [InlineData("Downloading", "None", "NotDownloaded", false, "Downloading")]
    [InlineData("Unavailable", "NoGpu", "NotDownloaded", false, "Hidden")]
    public void TheCard_ShowsWhatTheMachineAndTheStoreSay(
        string store, string device, string engine, bool failed, string expected) =>
        Assert.Equal(expected,
            OverTranslate.Views.Settings.MangaModelCard.StateOf(
                Enum.Parse<MangaModelState>(store), Enum.Parse<MangaUnavailable>(device),
                Enum.Parse<MangaUnavailable>(engine), failed).ToString());
}
