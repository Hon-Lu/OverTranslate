using System.Drawing;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;
using Rect = System.Windows.Rect;

namespace OverTranslate.Tests;

/// <summary>
/// 字幕・漫畫 and 遊戲介面・聊天室 carry their wait (畫面文字穩定後再讀取) and detector size
/// (辨識解析度) as figures — 不等待 and 85%, one poll and 68% — where they used to leave both to
/// 自動. These hold the figures to reading exactly as 自動 did on them: the same detector sizes in
/// the same order for every block, and the same decision at every poll of every kind of change.
/// </summary>
/// <remarks>
/// "As 自動 did" is a mode built the old way: the same preset with both values put back on 自動,
/// which resolves through the code paths the presets used to take.
/// </remarks>
public class RealtimeTimingDetectorTests
{
    private static ResolvedProfile Preset(string id) => ResolvedProfile.Resolve(id, []);

    // The preset as it was: every value its own but the wait and the detector size, left to 自動.
    private static ResolvedProfile AsItWas(string id) =>
        ResolvedProfile.Resolve(new RealtimeProfile
        {
            Id = id,
            BasedOn = id,
            SettleSeconds = RealtimePresets.Automatic,
            DetectorPercent = RealtimePresets.Automatic,
        });

    public static TheoryData<string> FixedPresets => new() { RealtimePresets.SubtitleId, RealtimePresets.InterfaceId };

    // ── The figures ──

    [Fact]
    public void ThePercentagesDivideBackToTheMeasuredFractionsExactly()
    {
        Assert.Equal(RealtimeDetectorSize.StripFraction, RealtimePresets.StripPercent / 100.0);
        Assert.Equal(RealtimeDetectorSize.PanelFraction, RealtimePresets.PanelPercent / 100.0);
        Assert.Equal(RealtimeDetectorSize.StripFraction, Preset(RealtimePresets.SubtitleId).DetectorFraction);
        Assert.Equal(RealtimeDetectorSize.PanelFraction, Preset(RealtimePresets.InterfaceId).DetectorFraction);
        Assert.Null(Preset(RealtimePresets.StandardId).DetectorFraction);
    }

    [Fact]
    public void TheWaitsAreWhatAutomaticCameTo()
    {
        Assert.Equal(0, Preset(RealtimePresets.SubtitleId).SettlePolls);
        Assert.Equal(RealtimeRegionState.MaxTextUnsettledPolls, Preset(RealtimePresets.InterfaceId).SettlePolls);
        Assert.Equal(RealtimePresets.ItemsSettleSeconds,
            RealtimeRegionState.MaxTextUnsettledPolls * RealtimeRegionState.PollInterval.TotalSeconds, 9);
        Assert.Null(Preset(RealtimePresets.StandardId).SettlePolls);
        Assert.Equal(RealtimeReadTiming.Auto, Preset(RealtimePresets.StandardId).Timing);
    }

    [Theory]
    [MemberData(nameof(FixedPresets))]
    public void ThePresetDiffersFromItsOldSelfInTheTwoValuesOnly(string id)
    {
        var now = Preset(id);
        var then = AsItWas(id);

        Assert.Equal(now, then with { Timing = now.Timing, SettlePolls = now.SettlePolls, DetectorFraction = now.DetectorFraction });
        Assert.Equal(then.Layout, now.Layout);
        Assert.Equal(RealtimeReadTiming.Auto, then.Timing);
        Assert.Null(then.SettlePolls);
        Assert.Null(then.DetectorFraction);
    }

    // ── 讀取時機: the two questions the session asks of a mode ──

    /// <summary>
    /// The session asks a block's mode two things about timing, both of what it reads as now
    /// (RealtimeRegionState.ReadsAs of its layout): whether to read it the passage way — which
    /// picks Examine's dialogue rule and Merge against MergePanel — and how many polls to wait,
    /// which Examine turns into its cap. Both have to come out the same.
    /// </summary>
    [Theory]
    [MemberData(nameof(FixedPresets))]
    public void TheSessionsTimingQuestionsAnswerAsBefore(string id)
    {
        var now = Preset(id);
        var then = AsItWas(id);
        // A fixed layout is what ReadsAs hands back whatever the block last read as.
        foreach (var lastRead in new[] { RealtimeBlockMode.Subtitle, RealtimeBlockMode.Panel })
        {
            var state = new RealtimeRegionState { AutoReadsAs = lastRead };
            var readsAs = state.ReadsAs(now.Layout);
            Assert.Equal(then.Dialogue(state.ReadsAs(then.Layout)), now.Dialogue(readsAs));

            // Examine's cap: textSettlePolls ?? (dialogue ? 0 : MaxTextUnsettledPolls).
            bool dialogue = now.Dialogue(readsAs);
            Assert.Equal(then.SettlePolls ?? (dialogue ? 0 : RealtimeRegionState.MaxTextUnsettledPolls),
                now.SettlePolls ?? (dialogue ? 0 : RealtimeRegionState.MaxTextUnsettledPolls));
            Assert.Equal(then.TextSettlePolls(dialogue), now.TextSettlePolls(dialogue));
        }
    }

    public static TheoryData<string, string> Scenarios => new()
    {
        { RealtimePresets.SubtitleId, "replaced" }, { RealtimePresets.InterfaceId, "replaced" },
        { RealtimePresets.SubtitleId, "fade-in" }, { RealtimePresets.InterfaceId, "fade-in" },
        { RealtimePresets.SubtitleId, "typed" }, { RealtimePresets.InterfaceId, "typed" },
        { RealtimePresets.SubtitleId, "flash" }, { RealtimePresets.InterfaceId, "flash" },
        { RealtimePresets.SubtitleId, "moving-background" }, { RealtimePresets.InterfaceId, "moving-background" },
        { RealtimePresets.SubtitleId, "gone" }, { RealtimePresets.InterfaceId, "gone" },
        { RealtimePresets.SubtitleId, "cycle" }, { RealtimePresets.InterfaceId, "cycle" },
    };

    /// <summary>
    /// Each kind of change, poll by poll, through the same calls the session makes: Examine with the
    /// mode's answers; on a read, the merge the mode picks and MarkRendered; on an empty read, the
    /// tracker reset and the empty MarkRendered. Every poll's decision — read or not, why, a second
    /// reading of the same picture, a clear — is the same for the preset and for its old self.
    /// </summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void EveryPollDecidesAsBefore(string id, string scenario)
    {
        var frames = Scenario(scenario);

        var now = Drive(Preset(id), frames);
        var then = Drive(AsItWas(id), frames);

        Assert.Equal(then, now);
    }

    /// <summary>The comparison above can tell the two ways of reading apart at all.</summary>
    [Fact]
    public void TheScenariosTellPassageFromItems()
    {
        int differ = new[] { "replaced", "fade-in", "typed", "flash", "moving-background", "gone", "cycle" }
            .Count(s => !Drive(Preset(RealtimePresets.SubtitleId), Scenario(s))
                .SequenceEqual(Drive(Preset(RealtimePresets.InterfaceId), Scenario(s))));
        Assert.True(differ >= 4, $"only {differ} scenarios tell 不等待 from one poll");
    }

    [Theory]
    [InlineData(0.0, -1.0, RealtimePresets.SubtitleId)]
    [InlineData(0.15, -1.0, RealtimePresets.InterfaceId)]
    public void AWaitOnAnyModeDecidesAsAutomaticDidOnItsPreset(double seconds, double automatic, string preset)
    {
        // The same figure set by hand on a custom mode of the same layout.
        var custom = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", BasedOn = preset, SettleSeconds = seconds });
        var auto = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", BasedOn = preset, SettleSeconds = automatic });
        foreach (var s in new[] { "replaced", "fade-in", "typed", "flash", "gone" })
            Assert.Equal(Drive(auto, Scenario(s)), Drive(custom, Scenario(s)));
    }

    // ── 辨識解析度 ──

    public static TheoryData<int, int> Sizes => new()
    {
        { 300, 120 }, { 799, 200 }, { 200, 799 }, { 800, 120 }, { 801, 300 }, { 1000, 1000 },
        { 1191, 309 }, { 1396, 756 }, { 1623, 206 }, { 1894, 269 }, { 1920, 184 }, { 1920, 1080 },
        { 2560, 1440 }, { 3840, 2160 }, { 640, 1200 }, { 377, 943 },
    };

    /// <summary>
    /// Every block size the session can hand the detector — under DownscaleMinSide, at it, wide and
    /// tall, both orientations — gives the same first size, the same fallbacks in the same order,
    /// and the same fallbacks while nothing is shown.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sizes))]
    public void TheDetectorChainIsTheSameAtEverySize(int width, int height)
    {
        foreach (var id in new[] { RealtimePresets.SubtitleId, RealtimePresets.InterfaceId })
            foreach (var orientation in new[] { RealtimeTextOrientation.Horizontal, RealtimeTextOrientation.Vertical })
                foreach (var lastRead in new[] { RealtimeBlockMode.Subtitle, RealtimeBlockMode.Panel })
                {
                    var now = Preset(id);
                    var then = AsItWas(id);
                    var state = new RealtimeRegionState(orientation) { AutoReadsAs = lastRead };

                    // As the session asks it: ReadRegionAsync.
                    var a = RealtimeDetectorSize.For(width, height, state.ReadsAs(now.Layout), orientation, now.DetectorFraction);
                    var b = RealtimeDetectorSize.For(width, height, state.ReadsAs(then.Layout), orientation, then.DetectorFraction);

                    Assert.Equal(b.Primary, a.Primary);
                    Assert.Equal(b.Fallbacks, a.Fallbacks);
                    Assert.Equal(RealtimeDetectorSize.WhileNothingIsShown(b.Fallbacks, width, height),
                        RealtimeDetectorSize.WhileNothingIsShown(a.Fallbacks, width, height));
                }
    }

    [Theory]
    [InlineData(1920, 184, RealtimeBlockMode.Subtitle, 1632, new[] { 1312, 1920 })]
    [InlineData(1920, 184, RealtimeBlockMode.Panel, 1312, new[] { 1632, 1920 })]
    [InlineData(700, 200, RealtimeBlockMode.Subtitle, 700, new int[0])]
    public void TheChainItself(int width, int height, RealtimeBlockMode layout, int primary, int[] fallbacks)
    {
        var profile = layout == RealtimeBlockMode.Subtitle ? Preset(RealtimePresets.SubtitleId) : Preset(RealtimePresets.InterfaceId);

        var (first, rest) = RealtimeDetectorSize.For(width, height, profile.Layout, RealtimeTextOrientation.Horizontal, profile.DetectorFraction);

        Assert.Equal(primary, first);
        Assert.Equal(fallbacks, rest);
    }

    [Fact]
    public void AVerticalBlockIgnoresTheFigure()
    {
        foreach (var id in new[] { RealtimePresets.SubtitleId, RealtimePresets.InterfaceId })
        {
            var p = Preset(id);
            var (primary, fallbacks) = RealtimeDetectorSize.For(3000, 2000, p.Layout, RealtimeTextOrientation.Vertical, p.DetectorFraction);
            Assert.Equal(RealtimeDetectorSize.ColumnsAreReadAtTheScreenshotSize, primary);
            Assert.Empty(fallbacks);
        }
    }

    [Fact]
    public void TheActualValuesShowTheFiguresAsSet()
    {
        var subtitle = RealtimeActualValues.Of(RealtimePresets.SubtitleId, null);
        var items = RealtimeActualValues.Of(RealtimePresets.InterfaceId, null);
        var then = RealtimeActualValues.Of(new RealtimeProfile
        {
            Id = "m", BasedOn = RealtimePresets.SubtitleId,
            SettleSeconds = RealtimePresets.Automatic, DetectorPercent = RealtimePresets.Automatic,
        });

        Assert.Equal((0.0, 85), (subtitle.SettleSeconds!.Value, subtitle.DetectorPercent!.Value));
        Assert.Equal((0.15, 68), (items.SettleSeconds!.Value, items.DetectorPercent!.Value));
        // What 自動 was shown as on the same preset, figure for figure.
        Assert.Equal((then.SettleSeconds, then.DetectorPercent), (subtitle.SettleSeconds, subtitle.DetectorPercent));
    }

    // ── The driver ──

    private sealed record Shot(int Background, string Text, int Shade, int Width);

    private static readonly Rectangle TextBand = new(20, 40, 360, 24);

    // One frame per poll. Text is a band of a shade over the background; a different text is a
    // different shade, so the fingerprint sees the change the way it sees a new line.
    private static List<Shot> Scenario(string name)
    {
        var shots = new List<Shot>();
        void Hold(int polls, int background, string text, int shade, int width = 360)
        {
            for (int i = 0; i < polls; i++) shots.Add(new Shot(background, text, shade, width));
        }

        switch (name)
        {
            case "replaced":
                Hold(6, 20, "first line", 230);
                Hold(6, 20, "second line", 120);
                Hold(6, 20, "third line", 200);
                break;
            case "fade-in":
                Hold(4, 20, "", 0);
                Hold(1, 20, "fading", 70);
                Hold(1, 20, "fading in", 140);
                Hold(1, 20, "fading in now", 200);
                Hold(10, 20, "fading in now", 240);
                break;
            case "typed":
                Hold(3, 20, "", 0);
                foreach (var (text, width) in new[] { ("T", 40), ("Ty", 80), ("Typ", 120), ("Type", 160), ("Typed", 200), ("Typed out", 300) })
                    Hold(1, 20, text, 220, width);
                Hold(10, 20, "Typed out", 220, 300);
                break;
            case "flash":
                Hold(5, 20, "", 0);
                Hold(1, 20, "blink", 230);
                Hold(12, 20, "", 0);
                break;
            case "moving-background":
                for (int i = 0; i < 20; i++) shots.Add(new Shot(20 + (i % 4) * 25, "steady words", 240, 360));
                break;
            case "gone":
                Hold(6, 20, "here a while", 230);
                Hold(16, 20, "", 0);
                break;
            case "cycle":
                for (int round = 0; round < 3; round++)
                {
                    Hold(2, 20, "", 0);
                    Hold(1, 20, "line " + round, 100);
                    Hold(4, 20, "line " + round, 230);
                }
                break;
            default:
                // "pause-N": a line, then the box held empty and still for N polls, then the next.
                var pause = int.Parse(name["pause-".Length..]);
                Hold(6, 20, "a line", 230);
                Hold(pause, 20, "", 0);
                Hold(8, 20, "the next line", 120);
                break;
        }
        return shots;
    }

    // ── 原文消失後立即清除譯文 unticked, over a still picture ──

    /// <summary>
    /// The pause between two lines of a dialogue box, held still for anything from two polls to
    /// eight seconds. Under the hold the translation is left up exactly as it was; past it, it is
    /// taken down once. Either way every poll decides what it did — and so the next line is read at
    /// the same poll after it appears, however long the pause before it.
    /// </summary>
    [Theory]
    [InlineData(RealtimePresets.StandardId)]
    [InlineData(RealtimePresets.SubtitleId)]
    [InlineData(RealtimePresets.InterfaceId)]
    public void TheNextLineAfterAStillPauseIsReadAtTheSamePollHoweverLongThePause(string id)
    {
        int? latency = null;
        foreach (var pause in new[] { 2, 4, RealtimeRegionState.LongestHoldPolls, RealtimeRegionState.LongestHoldPolls * 2, 53 })
        {
            var log = Drive(Preset(id), Scenario($"pause-{pause}"));
            var appears = 6 + pause;
            var read = log.FindIndex(appears, entry => entry.Contains("shown=the next line"));
            latency ??= read - appears;
            Assert.Equal(latency, read - appears);

            // Without the drop the log is the decisions alone, poll for poll the same as a pause
            // nobody took anything down in.
            var drops = log.Count(entry => entry.EndsWith(" drop"));
            Assert.Equal(pause > RealtimeRegionState.LongestHoldPolls + 1 ? 1 : 0, drops);
            if (drops > 0)
                Assert.True(log.FindIndex(entry => entry.EndsWith(" drop")) < appears, "taken down as the next line came");
        }
    }

    [Fact]
    public void ClearingAtOnceIsNotHeldAtAll()
    {
        var once = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", BasedOn = RealtimePresets.StandardId, ClearAfterEmptyReads = 1 });
        foreach (var scenario in new[] { "gone", "cycle", "pause-53" })
            Assert.DoesNotContain(Drive(once, Scenario(scenario)), entry => entry.EndsWith(" drop"));
    }

    private static Bitmap Draw(Shot shot)
    {
        var bitmap = new Bitmap(400, 100);
        using var canvas = Graphics.FromImage(bitmap);
        canvas.Clear(Color.FromArgb(shot.Background, shot.Background, shot.Background));
        if (shot.Text.Length > 0)
        {
            using var brush = new SolidBrush(Color.FromArgb(shot.Shade, shot.Shade, shot.Shade));
            canvas.FillRectangle(brush, TextBand.X, TextBand.Y, shot.Width, TextBand.Height);
        }
        return bitmap;
    }

    // Poll by poll, what the session would do with this mode, as a list of what was decided.
    private static List<string> Drive(ResolvedProfile profile, List<Shot> shots)
    {
        var state = new RealtimeRegionState(RealtimeTextOrientation.Horizontal, profile.ClearAfterEmptyReads);
        var log = new List<string>();
        for (int poll = 0; poll < shots.Count; poll++)
        {
            using var bitmap = Draw(shots[poll]);
            Func<IReadOnlyList<Rectangle>?, FrameFingerprint> capture = areas => FrameFingerprint.Capture(bitmap, areas);

            bool dialogue = profile.Dialogue(state.ReadsAs(profile.Layout));
            var reason = state.Examine(capture, dialogue, null, profile.SettlePolls);
            var entry = $"{poll} {reason} again={state.ReadingAgain} same={state.SamePictureAsRead}";
            // A still picture's held translation taken down, which the session asks after a poll
            // that read nothing.
            if (reason == RealtimeReadReason.Nothing && state.TakeHeldOverlayExpiry()) entry += " drop";
            if (reason != RealtimeReadReason.Nothing)
            {
                var shot = shots[poll];
                List<OcrTextBlock> read = shot.Text.Length == 0
                    ? []
                    : [new OcrTextBlock(shot.Text, new Rect(TextBand.X, TextBand.Y, shot.Width, TextBand.Height))
                    {
                        LayoutBounds = new Rect(TextBand.X, TextBand.Y, shot.Width, TextBand.Height),
                        Confidence = 0.95,
                    }];
                state.RememberRead(read);
                if (read.Count == 0)
                {
                    state.Dialogue.Reset();
                    state.MarkRendered([], capture, []);
                    entry += $" empty clear={state.ShouldClearOverlay}";
                }
                else
                {
                    var merged = profile.Dialogue(state.ReadsAs(profile.Layout))
                        ? state.Dialogue.Merge(state.RenderedLines, read)
                        : state.Dialogue.MergePanel(state.RenderedLines, read);
                    state.MarkRendered([new Rectangle(TextBand.X, TextBand.Y, shot.Width, TextBand.Height)], capture, merged.Lines);
                    entry += $" read changed={merged.Changed} shown={state.RenderedText}";
                }
            }
            log.Add(entry);
        }
        return log;
    }
}
