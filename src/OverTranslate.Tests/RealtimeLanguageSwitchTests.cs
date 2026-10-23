using System.Drawing;
using System.Reflection;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Services.Realtime.Capture;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Changing the language pair from the running capsule (#345): what the two lists offer, how the
/// target list is searched, and what the session does with a new pair running and paused.
/// </summary>
public class RealtimeLanguageSwitchTests
{
    // ── The lists ────────────────────────────────────────────────────────────

    [Fact]
    public void SourceMenu_ListsTheFiveReadableLanguages_WithoutAutomatic()
    {
        var codes = RealtimeLanguageMenuItems.For(RealtimeLanguageMenuKind.Source, "EN").Select(e => e.Code);

        Assert.Equal(["EN", "JA", "KO", "ZH-HANT", "ZH"], codes);
    }

    [Fact]
    public void SourceMenu_IsTheListThePageOffers()
    {
        var page = LanguageData.OcrSourceLanguages.Where(l => !LanguageData.IsAutomaticSource(l.Code));

        Assert.Equal(page.Select(l => l.Code), RealtimeLanguageMenuItems.SourceLanguages.Select(l => l.Code));
    }

    [Fact]
    public void TargetMenu_ListsEveryTargetLanguage_InThePickersOrder()
    {
        var codes = RealtimeLanguageMenuItems.For(RealtimeLanguageMenuKind.Target, "ZH-HANT").Select(e => e.Code);

        Assert.Equal(LanguageData.TargetLanguages.Select(l => l.Code), codes);
    }

    [Theory]
    [InlineData(true, "ja", "JA")]
    [InlineData(false, "zh-hant", "ZH-HANT")]
    [InlineData(false, "EN-US", "EN-US")]
    public void TheLanguageInUse_IsMarked_AndOnlyIt(bool source, string selected, string expected)
    {
        var kind = source ? RealtimeLanguageMenuKind.Source : RealtimeLanguageMenuKind.Target;
        var marked = RealtimeLanguageMenuItems.For(kind, selected).Where(e => e.IsSelected).Select(e => e.Code);

        Assert.Equal([expected], marked);
    }

    [Fact]
    public void ACodeNotInTheList_MarksNothing() =>
        Assert.DoesNotContain(RealtimeLanguageMenuItems.For(RealtimeLanguageMenuKind.Source, "AUTO"), e => e.IsSelected);

    // The search is the capture toolbar's: code, local name and English name all find a language.
    [Theory]
    [InlineData("ru")]
    [InlineData("俄")]
    [InlineData("russian")]
    [InlineData("RUS")]
    public void TargetSearch_FindsALanguageByCodeOrEitherName(string query)
    {
        var found = RealtimeLanguageMenuItems.For(RealtimeLanguageMenuKind.Target, "ZH-HANT")
            .Where(e => RealtimeLanguageMenuItems.Matches(e, query))
            .Select(e => e.Code);

        Assert.Contains("RU", found);
    }

    [Fact]
    public void TargetSearch_AgreesWithTheToolbarsPicker()
    {
        foreach (var query in new[] { "", "ch", "中文", "trad chinese", "pt", "qq" })
            foreach (var entry in RealtimeLanguageMenuItems.For(RealtimeLanguageMenuKind.Target, "EN-US"))
                Assert.Equal(
                    Controls.ComboBoxSearch.Matches(entry.Language, query),
                    RealtimeLanguageMenuItems.Matches(entry, query));
    }

    [Fact]
    public void TargetSearch_WithNothingThere_LeavesNoRow() =>
        Assert.DoesNotContain(
            RealtimeLanguageMenuItems.For(RealtimeLanguageMenuKind.Target, "EN-US"),
            e => RealtimeLanguageMenuItems.Matches(e, "zzz"));

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

    // The cache's own generation, read rather than exposed for a test: a pass is only drawn and only
    // remembered while the generation it started under is still the current one.
    private static int CacheGeneration(RealtimeTranslationSession session)
    {
        var cache = typeof(RealtimeTranslationSession)
            .GetField("_translationCache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(session)!;
        return (int)cache.GetType().GetProperty("Generation")!.GetValue(cache)!;
    }

    // Languages the recogniser has no model for: nothing is loaded, and nothing here needs it to be.
    private const string OldSource = "XX";
    private const string NewSource = "YY";

    [Fact]
    public void Running_ChangeStartsAgainOnTheNewPair_AtOnce()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        var log = SessionLog(() =>
        {
            session.Start([Block], OldSource, "ZH-HANT", TranslationProvider.Google, new NoPicture());
            session.ChangeLanguages(NewSource, "EN-US");
            session.Stop(releaseRecogniser: true);
        });

        Assert.False(session.IsPaused);
        Assert.Equal(NewSource, session.SourceLanguage);
        Assert.Equal("EN-US", session.TargetLanguage);
        Assert.Contains(log, line => line.Contains("restarting at once"));
        Assert.Contains(log, line => line.StartsWith("Info|Realtime session started") && line.Contains($"\"{NewSource}\"->\"EN-US\""));
    }

    [Fact]
    public void Paused_ChangeStaysPaused_AndResumeStartsOnTheNewPair()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());

        string[] whilePaused = [];
        var log = SessionLog(() =>
        {
            session.Start([Block], OldSource, "ZH-HANT", TranslationProvider.Google, new NoPicture());
            session.Pause();
            whilePaused = SessionLog(() => session.ChangeLanguages(NewSource, "JA"));
            Assert.True(session.IsPaused);

            session.Resume();
            session.Stop(releaseRecogniser: true);
        });

        // Nothing started while paused: the change only remembered the pair.
        Assert.DoesNotContain(whilePaused, line => line.Contains("Realtime session started"));
        Assert.Contains(whilePaused, line => line.Contains("still paused"));
        Assert.Contains(log, line => line.StartsWith("Info|Realtime session started") && line.Contains($"\"{NewSource}\"->\"JA\""));
    }

    [Fact]
    public void AChange_FencesWhatWasInFlight_RunningOrPaused()
    {
        using var ocr = new OcrService();
        var session = new RealtimeTranslationSession(ocr, new TranslationService());
        session.Start([Block], OldSource, "ZH-HANT", TranslationProvider.Google, new NoPicture());

        // A pass that posted before the change took this generation.
        var before = CacheGeneration(session);
        var running = session.ChangeLanguages(NewSource, "EN-US");
        Assert.NotEqual(before, running);
        Assert.Equal(running, CacheGeneration(session));

        session.Pause();
        var pausedBefore = CacheGeneration(session);
        var paused = session.ChangeLanguages(OldSource, "JA");
        Assert.NotEqual(pausedBefore, paused);
        Assert.Equal(paused, CacheGeneration(session));

        session.Stop(releaseRecogniser: true);
    }
}
