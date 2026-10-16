using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Ocr.Manga;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The manga models loaded as a realtime session starts, the 載入模型中… covering the wait, and what
/// a session holds warm between starting and 結束.
/// </summary>
public sealed class RealtimeModelLoadingTests : IDisposable
{
    private static readonly string[] Roles = ["detector", "encoder", "decoder-cross", "decoder-step", "vocabulary"];
    private static readonly byte[] NotAModel = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ot-model-loading-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ── The hint: from the press until every load has ended ──────────────────

    private sealed class Shown
    {
        private readonly List<bool> _changes = [];

        internal void Add(bool shown)
        {
            lock (_changes) _changes.Add(shown);
        }

        internal List<bool> Changes
        {
            get
            {
                lock (_changes) return [.. _changes];
            }
        }
    }

    // The end of a load arrives from a pool thread; here it is run where it arrives.
    private static RealtimeModelLoadingHint Hint(Shown shown) => new(shown.Add, action => action());

    private static TaskCompletionSource Load() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public void NothingToLoad_IsNeverShown()
    {
        var shown = new Shown();
        var hint = Hint(shown);

        hint.Wait(Task.CompletedTask);

        Assert.Empty(shown.Changes);
    }

    [Fact]
    public async Task ALoad_IsShownAtOnce_AndGoesWhenItEnds()
    {
        var shown = new Shown();
        var hint = Hint(shown);
        var load = Load();

        hint.Wait(load.Task);
        Assert.Equal([true], shown.Changes);

        load.SetResult();
        await WaitUntil(() => shown.Changes.Count == 2, Patience);
        Assert.Equal([true, false], shown.Changes);
    }

    [Fact]
    public async Task TwoLoads_StayShownUntilBothHaveEnded()
    {
        var shown = new Shown();
        var hint = Hint(shown);
        var recogniser = Load();
        var manga = Load();

        hint.Wait(Task.WhenAll(recogniser.Task, manga.Task));
        recogniser.SetResult();
        await Task.Delay(200);
        Assert.Equal([true], shown.Changes);

        manga.SetResult();
        await WaitUntil(() => shown.Changes.Count == 2, Patience);
        Assert.Equal([true, false], shown.Changes);
    }

    [Fact]
    public async Task AFailedLoad_TakesItDownToo()
    {
        var shown = new Shown();
        var hint = Hint(shown);
        var load = Load();

        hint.Wait(load.Task);
        load.SetException(new InvalidOperationException("would not load"));

        await WaitUntil(() => shown.Changes.Count == 2, Patience);
        Assert.Equal([true, false], shown.Changes);
    }

    [Fact]
    public async Task ALaterStart_TakesOver_AndTheEarlierLoadEndingLeavesItUp()
    {
        var shown = new Shown();
        var hint = Hint(shown);
        var first = Load();
        var second = Load();

        hint.Wait(first.Task);
        hint.Wait(second.Task);
        first.SetResult();
        await Task.Delay(200);
        Assert.Equal([true], shown.Changes);

        second.SetResult();
        await WaitUntil(() => shown.Changes.Count == 2, Patience);
        Assert.Equal([true, false], shown.Changes);
    }

    [Fact]
    public async Task TheSessionEnding_TakesItDown_AndNothingAfterBringsItBack()
    {
        var shown = new Shown();
        var hint = Hint(shown);
        var load = Load();

        hint.Wait(load.Task);
        hint.Dispose();
        load.SetResult();
        hint.Wait(Load().Task);
        await Task.Delay(200);

        Assert.Equal([true, false], shown.Changes);
    }

    // ── The manga engine: a preload held open ───────────────────────────────

    private MangaModelStore Store() =>
        new(new MangaModelManifest("1", [new MangaModelSource("test", "https://models.example/{name}", null)],
            [.. Roles.Select(role => new MangaModelFile(role, role + ".bin", NotAModel.Length,
                Convert.ToHexString(SHA256.HashData(NotAModel)).ToLowerInvariant()))]),
            _root, new Serve());

    // Not a real adapter, and no DirectML runtime in this store: a load on it fails, after the hook.
    private static (DirectMlDevice.Adapter?, string?) AGpu() =>
        (new DirectMlDevice.Adapter(0, "Some GPU", 0x10DE, 1, false, 8L << 30), null);

    private sealed class Held
    {
        private readonly SemaphoreSlim _go = new(0);
        private readonly SemaphoreSlim _started = new(0);
        private int _loads;

        internal bool Holds { get; init; } = true;
        internal int Loads => Volatile.Read(ref _loads);

        internal void BeforeLoad()
        {
            Interlocked.Increment(ref _loads);
            _started.Release();
            if (Holds) Assert.True(_go.Wait(Patience), "never let go");
        }

        internal void WaitStarted() => Assert.True(_started.Wait(Patience), "the load never started");

        internal void Finish() => _go.Release();
    }

    [Fact]
    public async Task APageDuringThePreload_IsBusy_AndTheModelsAreLoadedOnce()
    {
        var store = Store();
        await store.DownloadAsync();
        var held = new Held();
        using var manga = new MangaOcrEngine(store, AGpu, held.BeforeLoad);

        var preload = manga.PreloadAsync();
        held.WaitStarted();
        Assert.True(manga.IsLoading);

        using var page = new Bitmap(64, 64);
        var (outcome, detection) = await manga.DetectAsync(page, wait: false, default);
        Assert.Equal(MangaReadOutcome.Busy, outcome);
        Assert.Null(detection);

        held.Finish();
        await preload.WaitAsync(Patience);

        Assert.Equal(1, held.Loads);
        Assert.False(manga.IsLoading);
        Assert.False(manga.InMemory);
        // It failed here (no runtime in this store), and the next page goes to the columns.
        Assert.Equal(MangaUnavailable.LoadFailed, manga.Unavailable);
        Assert.Equal(MangaReadOutcome.Unavailable, (await manga.ReadAsync(page, wait: false, default)).Outcome);
        Assert.Equal(1, held.Loads);
    }

    [Fact]
    public async Task NotDownloaded_NothingIsLoaded()
    {
        var held = new Held { Holds = false };
        using var manga = new MangaOcrEngine(Store(), AGpu, held.BeforeLoad);

        await manga.PreloadAsync().WaitAsync(Patience);

        Assert.Equal(0, held.Loads);
        Assert.False(manga.IsLoading);
    }

    /// <summary>Needs the real models downloaded and a GPU DirectML can use; skipped otherwise.</summary>
    [RealMangaModelsFact]
    public async Task TheRealModels_EndedWhileTheyLoad_GoWhenTheIdleCountdownEnds()
    {
        var held = new Held();
        using var manga = new MangaOcrEngine(
            MangaModelStore.CreateDefault(), beforeLoad: held.BeforeLoad, idleReleaseDelay: TimeSpan.FromSeconds(2));
        manga.SetKeepWarm(true);

        var preload = manga.PreloadAsync();
        held.WaitStarted();
        // 結束: the load cannot be interrupted; EndRead starts the countdown as it finishes.
        manga.ReleaseNow();
        held.Finish();
        await preload.WaitAsync(Patience);

        Assert.True(manga.IsLoaded);
        Assert.True(manga.InMemory);
        await WaitUntil(() => !manga.IsLoaded, TimeSpan.FromSeconds(10));
        Assert.False(manga.IsLoaded);
    }

    /// <summary>Needs the real models downloaded and a GPU DirectML can use; skipped otherwise.</summary>
    [RealMangaModelsFact]
    public async Task TheRealModels_SwitchedOffWhileTheyLoad_GoAsTheLoadEnds()
    {
        var held = new Held();
        using var manga = new MangaOcrEngine(MangaModelStore.CreateDefault(), beforeLoad: held.BeforeLoad);
        manga.SetKeepWarm(true);

        var preload = manga.PreloadAsync();
        held.WaitStarted();
        manga.Enabled = false;
        held.Finish();
        await preload.WaitAsync(Patience);

        Assert.False(manga.IsLoaded);
    }

    /// <summary>Needs the real models downloaded and a GPU DirectML can use; skipped otherwise.</summary>
    [RealMangaModelsFact]
    public async Task TheRealModels_Preloaded_ReadAPageWithoutLoadingAgain()
    {
        var held = new Held { Holds = false };
        using var manga = new MangaOcrEngine(MangaModelStore.CreateDefault(), beforeLoad: held.BeforeLoad);
        manga.SetKeepWarm(true);

        await manga.PreloadAsync().WaitAsync(Patience);
        Assert.True(manga.IsLoaded);

        using var page = new Bitmap(640, 480);
        using (var g = Graphics.FromImage(page)) g.Clear(Color.White);
        Assert.Equal(MangaReadOutcome.Read, (await manga.ReadAsync(page, wait: false, default)).Outcome);
        Assert.Equal(1, held.Loads);
    }

    // ── The session: what it holds warm ──────────────────────────────────────

    private sealed class NoPicture : IRealtimeCaptureBackend
    {
        public string Name => "none";
        public Bitmap? GrabRegion(Rectangle screenBounds) => null;
        public CaptureMiss DescribeMiss(Rectangle screenBounds) => CaptureMiss.Waiting;
        public string DescribeActivity() => "";
        public string DescribeSteps() => "";

        public void Dispose()
        {
        }
    }

    private static RealtimeRegion Block(int id, RealtimeTextOrientation orientation) =>
        new(id, new Rectangle(id * 300, 0, 280, 200), RealtimeBlockMode.Panel, orientation);

    // One vertical among horizontal ones: the session as a whole wants the manga models.
    private static readonly RealtimeRegion[] Mixed =
        [Block(0, RealtimeTextOrientation.Vertical), Block(1, RealtimeTextOrientation.Horizontal),
         Block(2, RealtimeTextOrientation.Horizontal)];

    private static readonly RealtimeRegion[] Across =
        [Block(0, RealtimeTextOrientation.Horizontal), Block(1, RealtimeTextOrientation.Horizontal)];

    private static void StartSession(RealtimeTranslationSession session, IReadOnlyList<RealtimeRegion> regions) =>
        session.Start(regions, "JA", "ZH-HANT", Models.TranslationProvider.Google, new NoPicture());

    // The recogniser's own flag, read rather than exposed for a test.
    private static bool RecogniserKeptWarm(OcrService ocr)
    {
        var engine = typeof(OcrService).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ocr)!;
        return (bool)engine.GetType().GetField("_keepWarm", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
    }

    [Fact]
    public async Task Session_FramingKeepsBothWarm_AndOnlyTheEndLetsGo()
    {
        var store = Store();
        await store.DownloadAsync();
        var held = new Held { Holds = false };
        using var ocr = new OcrService(new MangaOcrEngine(store, AGpu, held.BeforeLoad));
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        StartSession(session, Mixed);
        Assert.True(ocr.Manga!.KeepsWarm);
        Assert.True(RecogniserKeptWarm(ocr));

        // 暫停, then 編輯 from the pause: both still held.
        session.Pause();
        Assert.True(ocr.Manga.KeepsWarm);
        Assert.True(RecogniserKeptWarm(ocr));
        session.Stop();
        Assert.True(ocr.Manga.KeepsWarm);
        Assert.True(RecogniserKeptWarm(ocr));

        // 結束.
        session.Stop(releaseRecogniser: true);
        Assert.False(ocr.Manga.KeepsWarm);
        Assert.False(RecogniserKeptWarm(ocr));
        await WaitUntil(() => !ocr.Manga.IsLoading, Patience);
    }

    [Fact]
    public async Task Session_NeverStarted_FramingTurnsNothingOn()
    {
        using var ocr = new OcrService(new MangaOcrEngine(Store(), AGpu));
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        session.Stop();

        Assert.False(ocr.Manga!.KeepsWarm);
        Assert.False(RecogniserKeptWarm(ocr));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Session_WithNoVerticalBlock_DoesNotHoldTheMangaModels_OrLoadThem()
    {
        var store = Store();
        await store.DownloadAsync();
        var held = new Held { Holds = false };
        using var ocr = new OcrService(new MangaOcrEngine(store, AGpu, held.BeforeLoad));
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        StartSession(session, Across);
        await Task.Delay(200);

        Assert.False(ocr.Manga!.KeepsWarm);
        Assert.True(RecogniserKeptWarm(ocr));
        Assert.Equal(0, held.Loads);
        session.Stop(releaseRecogniser: true);
    }

    /// <summary>Needs the real models downloaded and a GPU DirectML can use; skipped otherwise.</summary>
    [RealMangaModelsFact]
    public async Task TheRealModels_Session_MixedLoadsOnce_HorizontalLetsGoAtOnce_VerticalLoadsAgain()
    {
        var held = new Held { Holds = false };
        using var ocr = new OcrService(new MangaOcrEngine(MangaModelStore.CreateDefault(), beforeLoad: held.BeforeLoad));
        var manga = ocr.Manga!;
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        StartSession(session, Mixed);
        await WaitUntil(() => manga.IsLoaded && !manga.IsLoading, Patience);
        Assert.True(manga.IsLoaded);
        Assert.Equal(1, held.Loads);

        // Back to framing: kept. Every block horizontal, 開始翻譯: let go at once.
        session.Stop();
        Assert.True(manga.IsLoaded);
        StartSession(session, Across);
        Assert.False(manga.IsLoaded);
        Assert.False(manga.KeepsWarm);
        Assert.True(RecogniserKeptWarm(ocr));

        // Vertical again: loaded again.
        session.Stop();
        StartSession(session, Mixed);
        await WaitUntil(() => manga.IsLoaded && !manga.IsLoading, Patience);
        Assert.Equal(2, held.Loads);

        session.Stop(releaseRecogniser: true);
        Assert.False(manga.IsLoaded);
    }

    // ── What a start waits for ────────────────────────────────────────────────

    [Fact]
    public async Task Start_Cold_WaitsForTheRecogniser_ThenNothing()
    {
        using var ocr = new OcrService(new MangaOcrEngine(Store(), AGpu));
        var engine = Recogniser(ocr);

        var loading = ocr.KeepWarmForRealtime("JA", vertical: false);
        Assert.False(engine.IsLoaded("JA"));
        await loading.WaitAsync(Patience);
        Assert.True(engine.IsLoaded("JA"));

        // Already in memory, as after 暫停 or framing: nothing to wait for.
        Assert.True(ocr.KeepWarmForRealtime("JA", vertical: false).IsCompleted);
        Assert.True(ocr.KeepWarmForRealtime("JA", vertical: true).IsCompleted);
        ocr.ReleaseModel();
    }

    [Fact]
    public async Task Start_RecogniserWarm_WaitsForTheMangaModelsOnly()
    {
        var store = Store();
        await store.DownloadAsync();
        var held = new Held();
        using var ocr = new OcrService(new MangaOcrEngine(store, AGpu, held.BeforeLoad));
        await ocr.KeepWarmForRealtime("JA", vertical: false).WaitAsync(Patience);

        var loading = ocr.KeepWarmForRealtime("JA", vertical: true);
        held.WaitStarted();
        await Task.Delay(200);
        Assert.False(loading.IsCompleted);

        // It fails here (no runtime in this store), and a failure ends the wait as well.
        held.Finish();
        await loading.WaitAsync(Patience);
        Assert.Equal(1, held.Loads);
        Assert.Equal(MangaUnavailable.LoadFailed, ocr.Manga!.Unavailable);

        // Failed, the models are not this session's to wait for any more.
        Assert.True(ocr.KeepWarmForRealtime("JA", vertical: true).IsCompleted);
        ocr.ReleaseModel();
    }

    [Fact]
    public async Task Start_Cold_WaitsForBoth_UntilTheLaterHasEnded()
    {
        var store = Store();
        await store.DownloadAsync();
        var held = new Held();
        using var ocr = new OcrService(new MangaOcrEngine(store, AGpu, held.BeforeLoad));
        var engine = Recogniser(ocr);

        var loading = ocr.KeepWarmForRealtime("JA", vertical: true);
        held.WaitStarted();
        await WaitUntil(() => engine.IsLoaded("JA"), Patience);
        Assert.True(engine.IsLoaded("JA"));
        await Task.Delay(200);
        Assert.False(loading.IsCompleted);

        held.Finish();
        await loading.WaitAsync(Patience);
        ocr.ReleaseModel();
    }

    [Fact]
    public async Task Start_Horizontal_NeverWaitsForTheMangaModels()
    {
        var store = Store();
        await store.DownloadAsync();
        var held = new Held();
        using var ocr = new OcrService(new MangaOcrEngine(store, AGpu, held.BeforeLoad));

        await ocr.KeepWarmForRealtime("JA", vertical: false).WaitAsync(Patience);

        Assert.Equal(0, held.Loads);
        ocr.ReleaseModel();
    }

    // ── The busy line in the log ─────────────────────────────────────────────

    private sealed class StillPicture : IRealtimeCaptureBackend
    {
        public string Name => "still";

        public Bitmap? GrabRegion(Rectangle screenBounds)
        {
            var frame = new Bitmap(screenBounds.Width, screenBounds.Height);
            using var g = Graphics.FromImage(frame);
            g.Clear(Color.White);
            return frame;
        }

        public CaptureMiss DescribeMiss(Rectangle screenBounds) => CaptureMiss.Waiting;
        public string DescribeActivity() => "";
        public string DescribeSteps() => "";

        public void Dispose()
        {
        }
    }

    // What the session logs while body runs, as "Level|message".
    private static async Task<string[]> SessionLog(Func<Task> body)
    {
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var config = NLog.LogManager.Configuration ?? new NLog.Config.LoggingConfiguration();
        var rule = new NLog.Config.LoggingRule(typeof(RealtimeTranslationSession).FullName, NLog.LogLevel.Debug, target);
        config.LoggingRules.Add(rule);
        NLog.LogManager.Configuration = config;
        try
        {
            await body();
        }
        finally
        {
            config.LoggingRules.Remove(rule);
            NLog.LogManager.Configuration = config;
        }

        return [.. target.Logs];
    }

    [Fact]
    public async Task Busy_WhileTheModelsLoad_IsDebug_NotTheSlotsWarning()
    {
        var store = Store();
        await store.DownloadAsync();
        var held = new Held();
        using var ocr = new OcrService(new MangaOcrEngine(store, AGpu, held.BeforeLoad));
        await ocr.KeepWarmForRealtime("JA", vertical: false).WaitAsync(Patience);
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        var log = await SessionLog(async () =>
        {
            session.Start([Block(0, RealtimeTextOrientation.Vertical)], "JA", "ZH-HANT",
                Models.TranslationProvider.Google, new StillPicture(), readAtOnce: true);
            held.WaitStarted();
            await Task.Delay(1000);
            session.Stop(releaseRecogniser: true);
            held.Finish();
            await session.ModelsLoading.WaitAsync(Patience);
        });

        Assert.Contains(log, line => line.StartsWith("Debug|") && line.Contains("models still loading"));
        Assert.DoesNotContain(log, line => line.Contains("recognition slots busy"));
    }

    [Fact]
    public async Task Busy_WithTheModelsLoaded_IsStillTheSlotsWarning()
    {
        using var ocr = new OcrService(new MangaOcrEngine(Store(), AGpu));
        await ocr.KeepWarmForRealtime("JA", vertical: false).WaitAsync(Patience);
        var engine = Recogniser(ocr);
        var slots = Enumerable.Range(0, OcrService.ConcurrentRecognitions).Select(_ => engine.TryTakeSlot()).ToList();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        try
        {
            var log = await SessionLog(async () =>
            {
                session.Start([Block(0, RealtimeTextOrientation.Horizontal)], "JA", "ZH-HANT",
                    Models.TranslationProvider.Google, new StillPicture(), readAtOnce: true);
                await Task.Delay(1000);
                session.Stop(releaseRecogniser: true);
            });

            Assert.Contains(log, line => line.StartsWith("Warn|") && line.Contains("recognition slots busy"));
            Assert.DoesNotContain(log, line => line.Contains("models still loading"));
        }
        finally
        {
            foreach (var slot in slots) slot?.Dispose();
        }
    }

    private static OnnxOcrEngine Recogniser(OcrService ocr) =>
        (OnnxOcrEngine)typeof(OcrService).GetField("_engine", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(ocr)!;

    private static async Task WaitUntil(Func<bool> condition, TimeSpan patience)
    {
        var until = DateTime.UtcNow + patience;
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
    }

    private sealed class Serve : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(NotAModel) });
    }
}
