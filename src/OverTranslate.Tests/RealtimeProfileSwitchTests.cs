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
        Assert.Contains(log, line => line.Contains("restarting at once, region(s) 0"));
        // The block's loop alone: the session itself is not started again.
        Assert.DoesNotContain(log, line => line.StartsWith("Info|Realtime session started"));
        Assert.Contains(log, line => line.StartsWith("Info|Realtime region 0") && line.Contains("profile=slow"));
        // The translations are still right: the languages and the screen are the same.
        Assert.Equal(generation, CacheGeneration(session));
    }

    private static RealtimeRegion Block(int id, ResolvedProfile profile) =>
        new(id, new Rectangle(id * 300, 0, 280, 200), profile, RealtimeTextOrientation.Horizontal);

    private static IReadOnlyList<RegionLoop> Loops(RealtimeTranslationSession session) =>
        (IReadOnlyList<RegionLoop>)typeof(RealtimeTranslationSession)
            .GetField("_loops", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;

    [Fact]
    public void Running_OnlyTheBlockWhoseModeChanged_StartsAgain()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([Block(0, ResolvedProfile.Standard), Block(1, ResolvedProfile.Standard)],
            Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());
        var before = Loops(session);

        string[] log = [];
        log = SessionLog(() => session.ChangeProfiles([Block(0, ResolvedProfile.Standard), Block(1, Slow)]));
        var after = Loops(session);
        session.Stop(releaseRecogniser: true);

        Assert.Contains(log, line => line.Contains("restarting at once, region(s) 1"));
        Assert.DoesNotContain(log, line => line.StartsWith("Info|Realtime region 0"));
        Assert.Contains(log, line => line.StartsWith("Info|Realtime region 1") && line.Contains("profile=slow"));
        // Block 1's loop is a new one; block 0 kept reading on the loop it had.
        Assert.Same(before[0], after[0]);
        Assert.NotSame(before[1], after[1]);
    }

    [Fact]
    public void Running_ManyChangesInARow_LeaveOneLoopPerBlock_AndEveryOldOneStops()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([Block(0, ResolvedProfile.Standard), Block(1, ResolvedProfile.Standard)],
            Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());

        var stopped = new List<RegionLoop>();
        for (int i = 0; i < 200; i++)
        {
            var before = Loops(session);
            var profile = i % 2 == 0 ? Slow : ResolvedProfile.Standard;
            Assert.True(session.ChangeProfiles([Block(0, profile), Block(1, ResolvedProfile.Standard)]));
            stopped.Add(before[0]);
        }
        var current = Loops(session);

        Assert.Equal(2, current.Count);
        Assert.True(AllEnd(stopped), "a loop told to stop was still running");
        Assert.False(current[0].Task.IsCompleted);
        Assert.False(current[1].Task.IsCompleted);

        session.Stop(releaseRecogniser: true);
        Assert.True(AllEnd(current));
    }

    // Ended one way or another: a loop cancelled before it began ends cancelled, not run.
    private static bool AllEnd(IEnumerable<RegionLoop> loops)
    {
        var all = Task.WhenAll(loops.Select(loop => loop.Task));
        Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10))).Wait();
        return all.IsCompleted;
    }

    [Fact]
    public void Running_OnlyTheAlignChanged_RestartsNothing()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([Block(0, ResolvedProfile.Interface)], Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());
        var before = Loops(session);

        var centred = ResolvedProfile.Resolve(new RealtimeProfile
        {
            Id = RealtimePresets.InterfaceId,
            BasedOn = RealtimePresets.InterfaceId,
            Align = RealtimeTextAligns.Center,
        });
        bool changed = session.ChangeProfiles([Block(0, centred)]);
        var after = Loops(session);
        session.Stop(releaseRecogniser: true);

        Assert.False(changed);
        Assert.Same(before[0], after[0]);
    }

    [Fact]
    public void Running_ADifferentSetOfBlocks_StartsEveryBlockAgain()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([Block(0, ResolvedProfile.Standard)], Source, "ZH-HANT", TranslationProvider.Google, new NoPicture());

        var log = SessionLog(() => session.ChangeProfiles([Block(0, ResolvedProfile.Standard), Block(1, Slow)]));
        var after = Loops(session);
        session.Stop(releaseRecogniser: true);

        Assert.Contains(log, line => line.Contains("restarting every region at once"));
        Assert.Equal(2, after.Count);
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
