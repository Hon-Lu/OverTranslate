using System.Drawing;
using System.Reflection;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Modes changed on the page while a session runs: what the settings keep, and what the session
/// does with blocks whose mode changed, running and paused.
/// </summary>
public class RealtimeProfileSwitchTests
{
    // ── The settings ─────────────────────────────────────────────────────────

    [Fact]
    public void StoredModesAreCopies()
    {
        var settings = new RealtimeSettings();
        var mine = RealtimeProfile.CreateCustom(RealtimePresets.SubtitleId, "Mine");

        Assert.True(settings.SetProfiles([mine]));
        mine.Name = "Renamed elsewhere";

        Assert.Equal("Mine", settings.Profiles.Single().Name);
        Assert.NotSame(mine, settings.Profiles.Single());
    }

    [Fact]
    public void TheSameModesAgainAreNoChange()
    {
        var settings = new RealtimeSettings();
        var mine = RealtimeProfile.CreateCustom(RealtimePresets.SubtitleId, "Mine");
        settings.SetProfiles([mine]);

        Assert.False(settings.SetProfiles([mine.Clone()]));

        var changed = mine.Clone();
        changed.ClearAfterEmptyReads = 1;
        Assert.True(settings.SetProfiles([changed]));

        // Putting a value back is setting it to null again.
        changed.ClearAfterEmptyReads = null;
        Assert.True(settings.SetProfiles([changed]));
        Assert.Null(settings.Profiles.Single().ClearAfterEmptyReads);
    }

    [Fact]
    public void DeletingTheModeNewBlocksStartOn_StartsThemOnStandard()
    {
        var settings = new RealtimeSettings();
        var mine = RealtimeProfile.CreateCustom(RealtimePresets.InterfaceId, "Mine");
        settings.SetProfiles([mine]);
        settings.NewBlockProfile = mine.Id;

        Assert.True(settings.SetProfiles([]));

        Assert.Equal(RealtimePresets.StandardId, settings.NewBlockProfile);
        Assert.Same(ResolvedProfile.Standard, settings.ResolveProfile(mine.Id));
    }

    [Fact]
    public void NoMoreThanTenModesAreKept()
    {
        var settings = new RealtimeSettings();

        settings.SetProfiles(Enumerable.Range(0, 12).Select(i => RealtimeProfile.CreateCustom(null, $"M{i}")));

        Assert.Equal(RealtimeSettings.MaxCustomProfiles, settings.Profiles.Count);
        Assert.Equal("M9", settings.Profiles[^1].Name);
    }

    // ── The session ──────────────────────────────────────────────────────────

    private static readonly Rectangle Bounds = new(0, 0, 280, 200);

    private static RealtimeRegion Block(ResolvedProfile profile) =>
        new(0, Bounds, profile, RealtimeTextOrientation.Horizontal);

    private static readonly ResolvedProfile Slow = ResolvedProfile.Resolve(new RealtimeProfile
    {
        Id = "slow",
        BasedOn = RealtimePresets.SubtitleId,
        SettleSeconds = 0.6,
        ClearAfterEmptyReads = 1,
    });

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

    // What the session logs while body runs, as "Level|message".
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

    private static int CacheGeneration(RealtimeTranslationSession session)
    {
        var cache = typeof(RealtimeTranslationSession)
            .GetField("_translationCache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;
        return (int)cache.GetType().GetProperty("Generation")!.GetValue(cache)!;
    }

    // A language the recogniser has no model for: nothing is loaded, and nothing here needs it to be.
    private const string Source = "XX";

    [Fact]
    public void EveryBlocksModeIsLogged_AsTheSessionStarts()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        var log = SessionLog(() =>
        {
            session.Start([Block(Slow)], Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());
            session.Stop(releaseRecogniser: true);
        });

        Assert.Contains(log, line => line.StartsWith("Info|Realtime region 0") && line.Contains("profile=slow") &&
            line.Contains("settle=4") && line.Contains("clearAfter=1"));
    }

    [Fact]
    public void Running_AChangedModeStartsAgainOnIt_AtOnce_WithoutAFence()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([Block(ResolvedProfile.Standard)], Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());
        var generation = CacheGeneration(session);

        bool changed = false;
        var log = SessionLog(() => changed = session.ChangeProfiles([Block(Slow)]));
        session.Stop(releaseRecogniser: true);

        Assert.True(changed);
        Assert.Contains(log, line => line.Contains("restarting at once"));
        Assert.Contains(log, line => line.StartsWith("Info|Realtime session started"));
        Assert.Contains(log, line => line.StartsWith("Info|Realtime region 0") && line.Contains("profile=slow"));
        // The translations are still right: the languages and the screen are the same.
        Assert.Equal(generation, CacheGeneration(session));
    }

    [Fact]
    public void ModesThatResolveAlike_AreNoChange()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([Block(ResolvedProfile.Interface)], Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());

        bool changed = true;
        var log = SessionLog(() => changed = session.ChangeProfiles(
            [new RealtimeRegion(0, Bounds, RealtimeBlockMode.Panel, RealtimeTextOrientation.Horizontal)]));
        session.Stop(releaseRecogniser: true);

        Assert.False(changed);
        Assert.DoesNotContain(log, line => line.Contains("Realtime session started"));
    }

    [Fact]
    public void Paused_AChangedModeIsRemembered_AndResumeStartsOnIt()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        string[] whilePaused = [];
        var log = SessionLog(() =>
        {
            session.Start([Block(ResolvedProfile.Standard)], Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());
            session.Pause();
            whilePaused = SessionLog(() => session.ChangeProfiles([Block(Slow)]));
            Assert.True(session.IsPaused);

            session.Resume();
            session.Stop(releaseRecogniser: true);
        });

        Assert.DoesNotContain(whilePaused, line => line.Contains("Realtime session started"));
        Assert.Contains(whilePaused, line => line.Contains("still paused"));
        Assert.Equal(2, log.Count(line => line.StartsWith("Info|Realtime session started")));
        Assert.Contains(log, line => line.StartsWith("Info|Realtime region 0") && line.Contains("profile=slow"));
    }
}
