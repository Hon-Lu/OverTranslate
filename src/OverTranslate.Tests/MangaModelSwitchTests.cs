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
/// 使用漫畫直排模型 on the card, and the line under the source pickers that says whether a page
/// will go to the models.
/// </summary>
public sealed class MangaModelSwitchTests : IDisposable
{
    private static readonly string[] Roles = ["detector", "encoder", "decoder-cross", "decoder-step", "vocabulary"];
    private static readonly byte[] NotAModel = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-manga-switch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private MangaModelStore Store() =>
        new(new MangaModelManifest("1", [new MangaModelSource("test", "https://models.example/{name}", null)],
            [.. Roles.Select(role => new MangaModelFile(role, role + ".bin", NotAModel.Length,
                Convert.ToHexString(SHA256.HashData(NotAModel)).ToLowerInvariant()))]),
            _root, new Serve());

    // Not a real adapter; a load on it fails, and a failed load is remembered — which is how these
    // tests tell "never tried" from "tried and fell back".
    private static (DirectMlDevice.Adapter?, string?) BrokenGpu() =>
        (new DirectMlDevice.Adapter(99, "broken", 0x10DE, 1, false, 0), null);

    private static readonly OcrTextBlock[] Columns =
    [
        new("縦書き", new System.Windows.Rect(72, 10, 8, 30), Confidence: 0.9),
        new("二列目", new System.Windows.Rect(40, 10, 8, 30), Confidence: 0.9),
    ];

    private static string Signature(IEnumerable<OcrTextBlock> blocks) =>
        string.Join("|", blocks.Select(b => $"{b.Text}@{b.Bounds}/{b.RenderGlyphHeight}/{b.Confidence}/{b.RunsAcross}"));

    // ── The hint ─────────────────────────────────────────────────────────────

    [Theory]
    // Horizontal never says anything, whatever the rest is.
    [InlineData(false, "JA",   "Ready",         "None",  "None",         true,  "None")]
    [InlineData(false, "AUTO", "NotDownloaded", "None",  "NotDownloaded", true, "None")]
    [InlineData(false, "JA",   "Downloading",   "None",  "NotDownloaded", true, "None")]
    // Vertical, not Japanese: say that Japanese is what brings the models in — whatever their state.
    [InlineData(true,  "AUTO", "Ready",         "None",  "None",         true,  "ChooseJapanese")]
    [InlineData(true,  "EN",   "Ready",         "None",  "None",         true,  "ChooseJapanese")]
    [InlineData(true,  "ZH-HANT", "NotDownloaded", "None", "NotDownloaded", true, "ChooseJapanese")]
    [InlineData(true,  "KO",   "Downloading",   "None",  "NotDownloaded", true, "ChooseJapanese")]
    // Vertical Japanese: where the models stand.
    [InlineData(true,  "JA",   "Ready",         "None",  "None",         true,  "Applied")]
    [InlineData(true,  "ja",   "Ready",         "None",  "None",         true,  "Applied")]
    [InlineData(true,  "JA",   "Downloading",   "None",  "NotDownloaded", true, "Downloading")]
    [InlineData(true,  "JA",   "NotDownloaded", "None",  "NotDownloaded", true, "NotInstalled")]
    // Switched off on the card: nothing, in either language.
    [InlineData(true,  "JA",   "Ready",         "None",  "Disabled",     true,  "None")]
    [InlineData(true,  "AUTO", "Ready",         "None",  "Disabled",     true,  "None")]
    // A machine that cannot run them, or models that would not load or read: nothing.
    [InlineData(true,  "JA",   "NotDownloaded", "NoGpu", "NotDownloaded", true, "None")]
    [InlineData(true,  "JA",   "Ready",         "NoGpu", "NoGpu",        true,  "None")]
    [InlineData(true,  "JA",   "Ready",         "None",  "LoadFailed",   true,  "None")]
    [InlineData(true,  "AUTO", "Ready",         "None",  "ReadFailed",   true,  "None")]
    // 顯示模型提示 off: nothing, anywhere.
    [InlineData(true,  "JA",   "Ready",         "None",  "None",         false, "None")]
    [InlineData(true,  "AUTO", "Ready",         "None",  "None",         false, "None")]
    [InlineData(true,  "JA",   "NotDownloaded", "None",  "NotDownloaded", false, "None")]
    // A build with no manifest has no models to talk about.
    [InlineData(true,  "JA",   "Unavailable",   "None",  "NotDownloaded", true, "None")]
    [InlineData(true,  "AUTO", "Unavailable",   "None",  "NotDownloaded", true, "None")]
    public void TheHint_FollowsDirectionLanguageModelsAndSwitches(
        bool vertical, string language, string store, string device, string engine, bool showHint, string expected) =>
        Assert.Equal(expected, MangaModelHints.For(
            vertical, language, Enum.Parse<MangaModelState>(store), Enum.Parse<MangaUnavailable>(device),
            Enum.Parse<MangaUnavailable>(engine), showHint).ToString());

    [Theory]
    // Japanese: where the models stand, whatever direction the blocks will be.
    [InlineData("JA",      "Ready",         "None",       "None",          "Applied")]
    [InlineData("ja",      "Ready",         "None",       "None",          "Applied")]
    [InlineData("JA",      "Downloading",   "None",       "NotDownloaded", "Downloading")]
    [InlineData("JA",      "NotDownloaded", "None",       "NotDownloaded", "NotInstalled")]
    // Anything else: nothing — no 選擇日文 line on this page.
    [InlineData("EN",      "Ready",         "None",       "None",          "None")]
    [InlineData("ZH-HANT", "NotDownloaded", "None",       "NotDownloaded", "None")]
    [InlineData("KO",      "Downloading",   "None",       "NotDownloaded", "None")]
    [InlineData(null,      "Ready",         "None",       "None",          "None")]
    // Switched off, cannot run, failed, or no models in this build: nothing.
    [InlineData("JA",      "Ready",         "None",       "Disabled",      "None")]
    [InlineData("JA",      "NotDownloaded", "NoGpu",      "NotDownloaded", "None")]
    [InlineData("JA",      "Ready",         "None",       "LoadFailed",    "None")]
    [InlineData("JA",      "Ready",         "None",       "ReadFailed",    "None")]
    [InlineData("JA",      "Unavailable",   "None",       "NotDownloaded", "None")]
    public void TheRealtimePageHint_IsForJapaneseOnly_WhateverTheDirection(
        string? language, string store, string device, string engine, string expected) =>
        Assert.Equal(expected, MangaModelHints.ForRealtimePage(
            language, Enum.Parse<MangaModelState>(store), Enum.Parse<MangaUnavailable>(device),
            Enum.Parse<MangaUnavailable>(engine)).ToString());

    // ── Switched off: the column pipeline, without touching the models ──────

    [Fact]
    public async Task SwitchedOff_TheScreenshotPath_ReadsTheColumns_WithoutTryingTheModels()
    {
        var store = Store();
        await store.DownloadAsync();
        using var manga = new MangaOcrEngine(store, BrokenGpu) { Enabled = false };
        using var page = new Bitmap(100, 60);

        var without = await OcrService.RecognizeVerticalAsync(new FixedEngine(), page, "JA", default);
        var with = await OcrService.RecognizeVerticalAsync(new FixedEngine(), page, "JA", default, manga);

        Assert.Equal(Signature(without), Signature(with));
        Assert.Equal(MangaUnavailable.Disabled, manga.Unavailable);
        // Had it tried, the broken adapter would have been found out and remembered.
        Assert.Equal("switched off in settings", manga.UnavailableReason);

        // And on again, the next page goes to them (here: tries, and finds the adapter broken).
        manga.Enabled = true;
        await OcrService.RecognizeVerticalAsync(new FixedEngine(), page, "JA", default, manga);
        Assert.Equal(MangaUnavailable.LoadFailed, manga.Unavailable);
    }

    [Fact]
    public async Task SwitchedOff_TheRealtimePath_ReadsTheColumns_WithoutTryingTheModels()
    {
        var store = Store();
        await store.DownloadAsync();
        using var manga = new MangaOcrEngine(store, BrokenGpu) { Enabled = false };
        using var page = new Bitmap(100, 60);

        var without = await OcrService.TryRecognizeVerticalAsync(new FixedEngine(), page, "JA", null, default);
        var with = await OcrService.TryRecognizeVerticalAsync(new FixedEngine(), page, "JA", null, default, manga);

        Assert.Equal(Signature(without!), Signature(with!));
        Assert.Equal("switched off in settings", manga.UnavailableReason);
    }

    [Fact]
    public void SwitchedOff_TheRealtimeLoop_KeepsItsSecondLook()
    {
        using var ocr = new OcrService(Store());
        ocr.Manga!.Enabled = false;
        Assert.False(ocr.ReadsVerticalWithMangaModels("JA"));
    }

    [Fact]
    public async Task ACardThatCannotRunThem_StaysUnsupported_WhateverTheSwitch()
    {
        var store = Store();
        await store.DownloadAsync();
        using var manga = new MangaOcrEngine(store, () => (null, "only a software adapter (WARP)")) { Enabled = false };

        // The device is the reason given, not the switch: the card shows 不支援 and the hint nothing.
        Assert.Equal(MangaUnavailable.NoGpu, manga.Unavailable);
    }

    // ── Delete, download again: back on ──────────────────────────────────────

    [Fact]
    public async Task DeletedAndDownloadedAgain_TheSwitchIsBackOn()
    {
        var store = Store();
        await store.DownloadAsync();
        using var manga = new MangaOcrEngine(store, BrokenGpu);
        var changes = new List<bool>();
        manga.EnabledChanged += (_, _) => changes.Add(manga.Enabled);

        manga.Enabled = false;
        Assert.Equal(MangaUnavailable.Disabled, manga.Unavailable);

        store.Delete();
        Assert.True(manga.Enabled);

        await store.DownloadAsync();
        Assert.True(manga.Enabled);
        Assert.Equal(MangaUnavailable.None, manga.Unavailable);
        // Off, then on once — the settings file is written from these.
        Assert.Equal([false, true], changes);
    }

    [Fact]
    public void BothSwitches_DefaultOn_AndOlderFilesReadThatWay()
    {
        var fresh = new OverTranslate.Models.AppSettings();
        Assert.True(fresh.UseMangaModels);
        Assert.True(fresh.Capture.ShowModelHint);

        var old = SettingsService.Parse("""{"AutoTranslateAfterSelection":true}""");
        Assert.True(old.UseMangaModels);
        Assert.True(old.Capture.ShowModelHint);

        var off = SettingsService.Parse("""{"UseMangaModels":false,"Capture":{"ShowModelHint":false}}""");
        Assert.False(off.UseMangaModels);
        Assert.False(off.Capture.ShowModelHint);
    }

    // ── Switched off: the loaded models are let go of at once ────────────────

    /// <summary>Needs the real models downloaded and a GPU DirectML can use; skipped otherwise.</summary>
    [RealMangaModelsFact]
    public async Task SwitchedOff_TheLoadedModelsAreReleasedAtOnce()
    {
        using var manga = new MangaOcrEngine(MangaModelStore.CreateDefault());
        using var page = new Bitmap(640, 480);
        using (var g = Graphics.FromImage(page)) g.Clear(Color.White);

        // Held warm, as a running realtime session holds them: the idle countdown would not free them.
        manga.SetKeepWarm(true);
        var (outcome, _) = await manga.ReadAsync(page, wait: true, default);
        Assert.Equal(MangaReadOutcome.Read, outcome);
        Assert.True(manga.IsLoaded);

        manga.Enabled = false;
        Assert.False(manga.IsLoaded);
        Assert.Equal(MangaReadOutcome.Unavailable, (await manga.ReadAsync(page, wait: true, default)).Outcome);
        Assert.False(manga.IsLoaded);

        // On again: loaded by the next page that wants them.
        manga.Enabled = true;
        Assert.False(manga.IsLoaded);
        Assert.Equal(MangaReadOutcome.Read, (await manga.ReadAsync(page, wait: true, default)).Outcome);
        Assert.True(manga.IsLoaded);
    }

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

/// <summary>Runs only where the manga models are downloaded and a GPU DirectML can use is present.</summary>
public sealed class RealMangaModelsFactAttribute : FactAttribute
{
    public RealMangaModelsFactAttribute()
    {
        try
        {
            if (MangaModelStore.CreateDefault().State != MangaModelState.Ready)
                Skip = "The manga models are not downloaded on this machine.";
            else if (DirectMlDevice.Choose().Adapter is null)
                Skip = "No GPU DirectML can use.";
        }
        catch (Exception ex)
        {
            Skip = "Could not check for the manga models: " + ex.Message;
        }
    }
}
