using System.Drawing;
using System.Reflection;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Changing the translation service from the running capsule: what is written where, and what the
/// session does with a new service running and paused.
/// </summary>
public class RealtimeProviderSwitchTests
{
    // ── The settings ─────────────────────────────────────────────────────────

    [Fact]
    public void APick_IsWrittenToRealtime_AndLeavesTheSharedServiceAlone()
    {
        var settings = new AppSettings { Provider = TranslationProvider.DeepL };
        settings.Realtime.Provider = TranslationProvider.Google;

        RealtimeProviderSwitch.Remember(settings, TranslationProvider.LocalAI);

        Assert.Equal(TranslationProvider.LocalAI, settings.Realtime.Provider);
        Assert.Equal(TranslationProvider.DeepL, settings.Provider);
    }

    [Fact]
    public void ARetiredOption_IsWrittenAsTheOneItBecame()
    {
        var settings = new AppSettings();

        RealtimeProviderSwitch.Remember(settings, TranslationProvider.Google2);

        Assert.Equal(TranslationProvider.Google, settings.Realtime.Provider);
    }

    [Theory]
    [InlineData(TranslationProvider.Google, TranslationProvider.Bing, true)]
    [InlineData(TranslationProvider.Bing, TranslationProvider.Bing, false)]
    [InlineData(TranslationProvider.Google2, TranslationProvider.Google, false)]
    [InlineData(TranslationProvider.Google, TranslationProvider.GoogleChrome, true)]
    public void OnlyAnotherService_IsAChange(TranslationProvider current, TranslationProvider picked, bool change) =>
        Assert.Equal(change, RealtimeProviderSwitch.IsChange(current, picked));

    // ── The session ──────────────────────────────────────────────────────────

    private static readonly RealtimeRegion Block =
        new(0, new Rectangle(0, 0, 280, 200), RealtimeBlockMode.Panel, RealtimeTextOrientation.Horizontal);

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

    private static string[] SessionLog(Action body)
    {
        var target = new NLog.Targets.MemoryTarget { Layout = "${level}|${message}" };
        var config = NLog.LogManager.Configuration ?? new NLog.Config.LoggingConfiguration();
        var rule = new NLog.Config.LoggingRule(typeof(RealtimeTranslationSession).FullName, NLog.LogLevel.Info, target);
        config.LoggingRules.Add(rule);
        NLog.LogManager.Configuration = config;
        try
        {
            body();
        }
        finally
        {
            config.LoggingRules.Remove(rule);
            NLog.LogManager.Configuration = config;
        }

        return [.. target.Logs];
    }

    private static object Field(RealtimeTranslationSession session, string name) =>
        typeof(RealtimeTranslationSession)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;

    private static int CacheGeneration(RealtimeTranslationSession session)
    {
        var cache = Field(session, "_translationCache");
        return (int)cache.GetType().GetProperty("Generation")!.GetValue(cache)!;
    }

    private static RealtimeTranslationHistory History(RealtimeTranslationSession session) =>
        (RealtimeTranslationHistory)Field(session, "_history");

    // Languages the recogniser has no model for: nothing is loaded, and nothing here needs it to be.
    // Services with nothing to warm up, so no request leaves the machine.
    private const string Source = "XX";

    [Fact]
    public void Running_ChangeStartsAgainOnTheNewService_AtOnce()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        var log = SessionLog(() =>
        {
            session.Start([Block], Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());
            session.ChangeProvider(TranslationProvider.Yandex);
            session.Stop(releaseRecogniser: true);
        });

        Assert.False(session.IsPaused);
        Assert.Equal(TranslationProvider.Yandex, session.Provider);
        Assert.Equal(Source, session.SourceLanguage);
        Assert.Equal("ZH-HANT", session.TargetLanguage);
        Assert.Contains(log, line => line.Contains("provider changed") && line.Contains("restarting at once"));
        Assert.Contains(log, line => line.StartsWith("Info|Realtime session started") && line.Contains("provider=Yandex"));
    }

    [Fact]
    public void Paused_ChangeStaysPaused_AndResumeStartsOnTheNewService()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        string[] whilePaused = [];
        var log = SessionLog(() =>
        {
            session.Start([Block], Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());
            session.Pause();
            whilePaused = SessionLog(() => session.ChangeProvider(TranslationProvider.Bing));
            Assert.True(session.IsPaused);

            session.Resume();
            session.Stop(releaseRecogniser: true);
        });

        Assert.DoesNotContain(whilePaused, line => line.Contains("Realtime session started"));
        Assert.Contains(whilePaused, line => line.Contains("still paused"));
        Assert.Contains(log, line => line.StartsWith("Info|Realtime session started") && line.Contains("provider=Bing"));
    }

    [Fact]
    public void AChange_FencesWhatWasInFlight_RunningOrPaused()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([Block], Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());

        var before = CacheGeneration(session);
        var running = session.ChangeProvider(TranslationProvider.Bing);
        Assert.NotEqual(before, running);
        Assert.Equal(running, CacheGeneration(session));

        session.Pause();
        var pausedBefore = CacheGeneration(session);
        var paused = session.ChangeProvider(TranslationProvider.Google);
        Assert.NotEqual(pausedBefore, paused);
        Assert.Equal(paused, CacheGeneration(session));

        session.Stop(releaseRecogniser: true);
    }

    // Another engine's translations are not examples for this one.
    [Fact]
    public void AChange_ForgetsTheContextTheOldServiceWasGiven()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([Block], Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());
        History(session).Add([new TranslationPair("こんにちは", "你好")]);
        Assert.NotEmpty(History(session).Lines);

        session.ChangeProvider(TranslationProvider.Bing);

        Assert.Empty(History(session).Lines);
        session.Stop(releaseRecogniser: true);
    }

    // A failure from the old service has nothing to say about the new one.
    [Fact]
    public void Running_AChangeClearsTheFailureOnShow()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([Block], Source, "ZH-HANT", TranslationProvider.DeepL, new NoPicture());
        var failures = (RealtimeFailureBoard)Field(session, "_failures");
        failures.Report(0, RealtimeFailureStage.Translate, "API key missing");
        Assert.NotNull(session.CurrentFailure);

        session.ChangeProvider(TranslationProvider.Google);

        Assert.Null(session.CurrentFailure);
        session.Stop(releaseRecogniser: true);
    }
}
