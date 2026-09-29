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
        new(new MangaModelManifest("1", "https://models.example/",
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
        Assert.Equal(Signature(await Columnsread(null)), Signature(await Columnsread(manga)));
    }

    [Fact]
    public async Task NoHardwareGpu_ReadsTheColumns_WithoutTryingToLoad()
    {
        var store = Store();
        await store.DownloadAsync();
        using var manga = new MangaOcrEngine(store, NoGpu);

        Assert.Equal("only a software adapter (WARP)", manga.UnavailableReason);
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
        Assert.StartsWith("loading on broken failed", manga.UnavailableReason);

        // Only new files clear it.
        store.Delete();
        await store.DownloadAsync();
        Assert.Null(manga.UnavailableReason);
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

    [Theory]
    [InlineData(true, 17763, true)]      // 1809 with the copy the app ships
    [InlineData(false, 17763, false)]    // 1809 without it: no system copy to fall back on
    [InlineData(false, 18362, true)]     // 1903 has DirectML in System32
    public void DirectMlIsOnlyUsedWhereItCanLoad(bool shipped, int build, bool available) =>
        Assert.Equal(available, DirectMlDevice.DirectMlLibraryAvailable("app", build, _ => shipped));

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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(NotAModel) });
    }
}
