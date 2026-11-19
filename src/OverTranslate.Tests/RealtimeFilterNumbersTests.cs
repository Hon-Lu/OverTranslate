using System.Drawing;
using System.Reflection;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using OverTranslate.Views.Realtime;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// 過濾數字與縮寫, the mode's tick over <see cref="RealtimeHiddenText.NothingToTranslate"/>: on in
/// every preset, followed by custom modes that set nothing, and off exactly as before it existed —
/// every line asked for and drawn.
/// </summary>
public class RealtimeFilterNumbersTests
{
    // ── The mode ─────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPresetFilters()
    {
        foreach (var preset in RealtimePresets.All)
        {
            Assert.True(preset.FilterNumbersAndAbbreviations);
            Assert.True(ResolvedProfile.Resolve(preset.Id, []).FilterNumbersAndAbbreviations);
        }
    }

    [Theory]
    [InlineData(RealtimePresets.StandardId)]
    [InlineData(RealtimePresets.SubtitleId)]
    [InlineData(RealtimePresets.InterfaceId)]
    public void ACustomModeThatSetsNothingFollowsItsPreset(string basedOn)
    {
        var mine = RealtimeProfile.CreateCustom(basedOn, "Mine");

        Assert.Null(mine.FilterNumbersAndAbbreviations);
        Assert.True(mine.Filled().FilterNumbersAndAbbreviations);
        Assert.True(ResolvedProfile.Resolve(mine).FilterNumbersAndAbbreviations);
    }

    [Fact]
    public void ACustomModeTurnedOffResolvesOff()
    {
        var mine = new RealtimeProfile { Id = "mine", BasedOn = RealtimePresets.InterfaceId, FilterNumbersAndAbbreviations = false };

        var resolved = ResolvedProfile.Resolve(mine);

        Assert.False(resolved.FilterNumbersAndAbbreviations);
        Assert.Equal(ResolvedProfile.Interface with { Id = "mine", FilterNumbersAndAbbreviations = false }, resolved);
        // A change of mode the session acts on: it reads again, which is what sends the held-back lines.
        Assert.False(resolved.ReadsLike(ResolvedProfile.Interface with { Id = "mine" }));
    }

    [Fact]
    public void AFileWrittenBeforeTheTickFilters()
    {
        var settings = SettingsService.Parse(
            """
            {"Realtime":{"Profiles":[{"Id":"a","Name":"A","BasedOn":"subtitle","ClearAfterEmptyReads":1}]}}
            """);

        Assert.Null(settings.Realtime.Profiles.Single().FilterNumbersAndAbbreviations);
        Assert.True(settings.Realtime.ResolveProfile("a").FilterNumbersAndAbbreviations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTickIsReadBackAsWritten(bool value)
    {
        var written = new AppSettings();
        written.Realtime.SetProfiles(
            [new RealtimeProfile { Id = "a", Name = "A", BasedOn = RealtimePresets.StandardId, FilterNumbersAndAbbreviations = value }]);

        var read = SettingsService.Parse(SettingsService.Serialize(written));

        Assert.Equal(value, read.Realtime.Profiles.Single().FilterNumbersAndAbbreviations);
        Assert.Equal(value, read.Realtime.ResolveProfile("a").FilterNumbersAndAbbreviations);
    }

    // ── The page ─────────────────────────────────────────────────────────────

    [Fact]
    public void ItIsATick_OnTheMainCard_ForBothDirections()
    {
        Assert.True(RealtimeProfileEditing.IsTick(ProfileParam.FilterNumbersAndAbbreviations));
        Assert.DoesNotContain(ProfileParam.FilterNumbersAndAbbreviations, RealtimeProfileEditing.Advanced);
        Assert.Contains(ProfileParam.FilterNumbersAndAbbreviations, RealtimeProfileEditing.Shown);
        Assert.False(RealtimeProfileEditing.IsHorizontalOnly(ProfileParam.FilterNumbersAndAbbreviations));
        // Listed where the page shows it: after 譯文對齊, before the timing card.
        var all = RealtimeProfileEditing.All.ToList();
        Assert.Equal(all.IndexOf(ProfileParam.Align) + 1, all.IndexOf(ProfileParam.FilterNumbersAndAbbreviations));
    }

    [Fact]
    public void TurnedOff_IsOneChange_AndPutBackFollowsThePresetAgain()
    {
        var mode = RealtimeProfile.CreateCustom(RealtimePresets.InterfaceId, "Mine");
        mode.FilterNumbersAndAbbreviations = false;

        var tidy = RealtimeProfileEditing.WithoutTemplateValues(mode);
        Assert.False(tidy.FilterNumbersAndAbbreviations);
        Assert.True(RealtimeProfileEditing.IsModified(tidy, ProfileParam.FilterNumbersAndAbbreviations));
        Assert.Equal(1, RealtimeProfileEditing.ModifiedCount(tidy));

        // Ticked again: what the preset holds, so stored as following it.
        tidy.FilterNumbersAndAbbreviations = true;
        Assert.Null(RealtimeProfileEditing.WithoutTemplateValues(tidy).FilterNumbersAndAbbreviations);

        // 還原 on the row, and 還原為範本.
        tidy.FilterNumbersAndAbbreviations = false;
        var one = tidy.Clone();
        RealtimeProfileEditing.Clear(one, ProfileParam.FilterNumbersAndAbbreviations);
        Assert.Null(one.FilterNumbersAndAbbreviations);
        Assert.Equal(0, RealtimeProfileEditing.ModifiedCount(one));
        Assert.Null(RealtimeProfileEditing.ResetAll(tidy).FilterNumbersAndAbbreviations);
    }

    [Fact]
    public void APresetCountsNothingModified()
    {
        foreach (var preset in RealtimePresets.All)
            Assert.Equal(0, RealtimeProfileEditing.ModifiedCount(preset));
    }

    // ── The session ──────────────────────────────────────────────────────────

    private static OcrTextBlock Line(string text) => new(text, new Rect(10, 10, 80, 20), Confidence: 0.95);

    [Theory]
    [InlineData("Lv 15")]
    [InlineData("12:30")]
    [InlineData("HP")]
    [InlineData("★")]
    public void Off_ALineWithNothingToTranslateIsAskedForAndDrawn(string text)
    {
        Assert.True(RealtimeTranslationSession.LeftOut(Line(text), filter: true));
        Assert.False(RealtimeTranslationSession.LeftOut(Line(text), filter: false));

        // Before an answer: waiting on one, as any line is, so the pump asks again.
        var waiting = RealtimeTranslationSession.ForOverlay(Line(text), translation: null, filter: false);
        Assert.False(waiting.Hidden);
        Assert.True(waiting.Untranslated);

        // With one: drawn like any other line.
        var drawn = RealtimeTranslationSession.ForOverlay(Line(text), "譯文", filter: false);
        Assert.False(drawn.Hidden);
        Assert.False(drawn.Untranslated);
        Assert.Equal("譯文", drawn.TranslatedText);
    }

    [Fact]
    public void On_ALineWithNothingToTranslateIsHiddenWhateverIsCached()
    {
        // Translated while the tick was off, then ticked again: hidden at once all the same.
        var line = RealtimeTranslationSession.ForOverlay(Line("Lv 15"), "等級 15", filter: true);

        Assert.True(line.Hidden);
        Assert.False(line.Untranslated);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ALineWithWordsIsTheSameEitherWay(bool filter)
    {
        Assert.False(RealtimeTranslationSession.LeftOut(Line("QSAVE"), filter));
        Assert.False(RealtimeTranslationSession.ForOverlay(Line("KAI Lv 12"), "KAI 等級 12", filter).Hidden);
    }

    private static readonly Rectangle Bounds = new(0, 0, 280, 200);

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

    private static int CacheGeneration(RealtimeTranslationSession session)
    {
        var cache = typeof(RealtimeTranslationSession)
            .GetField("_translationCache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;
        return (int)cache.GetType().GetProperty("Generation")!.GetValue(cache)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Running_TheTickTurnedEitherWayReadsEveryBlockAgainAtOnce_KeepingTheTranslations(bool turnedOn)
    {
        var before = ResolvedProfile.Standard with { Id = "mine", FilterNumbersAndAbbreviations = !turnedOn };
        var after = before with { FilterNumbersAndAbbreviations = turnedOn };
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([new RealtimeRegion(0, Bounds, before, RealtimeTextOrientation.Horizontal)],
            "XX", "ZH-HANT", TranslationProvider.Google, new NoPicture());
        var generation = CacheGeneration(session);

        bool changed = false;
        var log = SessionLog(() => changed = session.ChangeProfiles(
            [new RealtimeRegion(0, Bounds, after, RealtimeTextOrientation.Horizontal)]));
        session.Stop(releaseRecogniser: true);

        Assert.True(changed);
        Assert.Contains(log, line => line.Contains("restarting at once"));
        Assert.Contains(log, line => line.StartsWith("Info|Realtime region 0") &&
            line.EndsWith("filter=" + (turnedOn ? "on" : "off")));
        Assert.Equal(generation, CacheGeneration(session));
    }
}
