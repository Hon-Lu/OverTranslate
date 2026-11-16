using System.Drawing;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The modes as the pipeline runs them. The presets come first: each of them has to hand the
/// pipeline exactly what it ran on before modes existed, constant for constant and instance for
/// instance — that is what lets OcrHarness --auto-probe replay prove the refactor changed nothing.
/// </summary>
public class RealtimeProfileTests
{
    public static TheoryData<string, RealtimeBlockMode> Presets => new()
    {
        { RealtimePresets.StandardId, RealtimeBlockMode.Auto },
        { RealtimePresets.SubtitleId, RealtimeBlockMode.Subtitle },
        { RealtimePresets.InterfaceId, RealtimeBlockMode.Panel },
    };

    [Theory]
    [MemberData(nameof(Presets))]
    public void APresetReadsWithTheMeasuredRulesThemselves(string id, RealtimeBlockMode layout)
    {
        var profile = ResolvedProfile.Resolve(id, []);

        Assert.Equal(id, profile.Id);
        Assert.Equal(layout, profile.Layout);
        Assert.Equal(RealtimeReadTiming.Auto, profile.Timing);
        Assert.Null(profile.SettlePolls);
        Assert.Equal(RealtimeRegionState.EmptyPassesBeforeClearing, profile.ClearAfterEmptyReads);
        Assert.Null(profile.DetectorFraction);
        Assert.Same(AutoRules.Default, profile.Auto);
        Assert.Same(GroupingProfile.Realtime, profile.PanelGrouping);
        Assert.Equal(0, profile.IgnoreSmallFraction);
        Assert.Equal(ShortReadingDetection.ShortTextConfidenceFloor, profile.ConfidenceFloor);
    }

    [Fact]
    public void TheDefaultRulesAreTheConstantsOfTheGroupers()
    {
        var rules = AutoRules.Default;

        Assert.Equal(RealtimeAutoLayout.SparseZones, rules.SparseZones);
        Assert.Equal(RealtimeAutoLayout.StandsOut, rules.StandsOut);
        Assert.Equal(RealtimeAutoLayout.LetteringZones, rules.LetteringZones);
        Assert.Equal(RealtimeAutoLayout.LetteringAspect, rules.LetteringAspect);
        Assert.Equal(RealtimeAutoLayout.PassageLeading, rules.PassageLeading);
        Assert.True(rules.Lettering);
        Assert.Equal(DialogueTextGrouper.MaxStackGap, rules.MaxStackGap);
        Assert.Same(GroupingProfile.Realtime, rules.PanelGrouping);
    }

    [Fact]
    public void AStoredCopyOfAPresetResolvesToThePresetItself()
    {
        foreach (var preset in RealtimePresets.All)
        {
            var custom = preset.Clone();
            custom.Id = "copy";
            custom.BasedOn = preset.Id;

            Assert.Equal(ResolvedProfile.Resolve(preset.Id, []) with { Id = "copy" }, ResolvedProfile.Resolve(custom));
        }
    }

    [Fact]
    public void APresetFilledIsItself()
    {
        foreach (var preset in RealtimePresets.All)
            Assert.Equal(
                System.Text.Json.JsonSerializer.Serialize(preset),
                System.Text.Json.JsonSerializer.Serialize(preset.Filled()));
    }

    [Fact]
    public void ANewCustomModeFollowsItsPresetInEveryValue()
    {
        var mine = RealtimeProfile.CreateCustom(RealtimePresets.SubtitleId, "Mine");

        Assert.Matches("^[0-9a-f]{32}$", mine.Id);
        Assert.Equal("Mine", mine.Name);
        Assert.Equal(ResolvedProfile.Subtitle with { Id = mine.Id }, ResolvedProfile.Resolve(mine));
        Assert.All(
            typeof(RealtimeProfile).GetProperties().Where(property =>
                property.Name is not (nameof(RealtimeProfile.Id) or nameof(RealtimeProfile.Name) or nameof(RealtimeProfile.BasedOn))),
            property => Assert.Null(property.GetValue(mine)));
        Assert.Equal(RealtimePresets.StandardId, RealtimeProfile.CreateCustom("custom", "x").BasedOn);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void TheOldLayoutsNameThePresets(string id, RealtimeBlockMode layout) =>
        Assert.Same(ResolvedProfile.Resolve(id, []), ResolvedProfile.ForLayout(layout));

    [Fact]
    public void AModeThatCannotBeFoundIsStandard()
    {
        Assert.Same(ResolvedProfile.Standard, ResolvedProfile.Resolve("deleted", []));
        Assert.Same(ResolvedProfile.Standard, ResolvedProfile.Resolve(null, null));
        Assert.Same(ResolvedProfile.Standard, ResolvedProfile.Resolve("", [new RealtimeProfile { Id = "x" }]));
    }

    [Fact]
    public void ACustomModeIsFoundByItsId()
    {
        var custom = new RealtimeProfile { Id = "mine", BasedOn = RealtimePresets.InterfaceId, ClearAfterEmptyReads = 4 };

        var profile = ResolvedProfile.Resolve("mine", [new RealtimeProfile { Id = "other" }, custom]);

        Assert.Equal("mine", profile.Id);
        Assert.Equal(RealtimeBlockMode.Panel, profile.Layout);
        Assert.Equal(4, profile.ClearAfterEmptyReads);
    }

    [Fact]
    public void AnUnsetValueFollowsThePresetAndASetOneDoesNot()
    {
        var profile = ResolvedProfile.Resolve(new RealtimeProfile
        {
            Id = "mine",
            BasedOn = RealtimePresets.SubtitleId,
            ReadTiming = RealtimePresets.TimingSettle,
        });

        Assert.Equal(RealtimeBlockMode.Subtitle, profile.Layout);
        Assert.Equal(RealtimeReadTiming.SettleThenRead, profile.Timing);
        Assert.Same(AutoRules.Default, profile.Auto);
    }

    [Fact]
    public void AValueThisBuildCannotReadFollowsThePreset()
    {
        var profile = ResolvedProfile.Resolve(new RealtimeProfile
        {
            Id = "mine",
            BasedOn = RealtimePresets.InterfaceId,
            Layout = "columns",
            ReadTiming = "whenever",
            SettleSeconds = double.NaN,
        });

        Assert.Equal(RealtimeBlockMode.Panel, profile.Layout);
        Assert.Equal(RealtimeReadTiming.Auto, profile.Timing);
        Assert.Null(profile.SettlePolls);
    }

    [Fact]
    public void AModeMadeFromAnythingButAPresetFollowsStandard()
    {
        var profile = ResolvedProfile.Resolve(new RealtimeProfile { Id = "mine", BasedOn = "another-custom" });

        Assert.Equal(ResolvedProfile.Standard with { Id = "mine" }, profile);
    }

    [Theory]
    [InlineData(-1.0, null)]
    [InlineData(0.0, 0)]
    [InlineData(0.15, 1)]
    [InlineData(0.3, 2)]
    [InlineData(1.5, 10)]
    [InlineData(9.0, 10)]
    public void SettlingIsCountedInPollsOfTheRealInterval(double seconds, int? polls)
    {
        var profile = ResolvedProfile.Resolve(new RealtimeProfile { Id = "mine", SettleSeconds = seconds });

        Assert.Equal(polls, profile.SettlePolls);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(9, 4)]
    public void EmptyReadsAreHeldToTheirRange(int stored, int resolved) =>
        Assert.Equal(resolved, ResolvedProfile.Resolve(
            new RealtimeProfile { Id = "mine", ClearAfterEmptyReads = stored }).ClearAfterEmptyReads);

    [Theory]
    [InlineData(-1, null)]
    [InlineData(20, 0.5)]
    [InlineData(75, 0.75)]
    [InlineData(100, 1.0)]
    [InlineData(150, 1.0)]
    public void TheDetectorPercentIsHeldToItsRange(int stored, double? fraction) =>
        Assert.Equal(fraction, ResolvedProfile.Resolve(
            new RealtimeProfile { Id = "mine", DetectorPercent = stored }).DetectorFraction);

    [Theory]
    [InlineData(-5, 0.0)]
    [InlineData(0, 0.0)]
    [InlineData(60, 0.6)]
    [InlineData(95, 0.8)]
    public void IgnoringSmallTextIsHeldToItsRange(int stored, double fraction) =>
        Assert.Equal(fraction, ResolvedProfile.Resolve(
            new RealtimeProfile { Id = "mine", IgnoreSmallPercent = stored }).IgnoreSmallFraction, 6);

    [Theory]
    [InlineData(0, 0.50)]
    [InlineData(1, 0.50)]
    [InlineData(3, ShortReadingDetection.ShortTextConfidenceFloor)]
    [InlineData(5, 0.95)]
    [InlineData(9, 0.95)]
    public void TheConfidenceStepsAreHeldToTheScale(int stored, double floor) =>
        Assert.Equal(floor, ResolvedProfile.Resolve(
            new RealtimeProfile { Id = "mine", ConfidenceLevel = stored }).ConfidenceFloor);

    [Fact]
    public void MergingTendsOneWayAlongItsScale()
    {
        var steps = Enumerable.Range(RealtimeProfile.MinStep, RealtimeProfile.MaxStep)
            .Select(step => ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", MergeTendency = step }).Auto)
            .ToList();

        for (int i = 1; i < steps.Count; i++)
        {
            Assert.True(steps[i].SparseZones >= steps[i - 1].SparseZones);
            Assert.True(steps[i].StandsOut <= steps[i - 1].StandsOut);
            Assert.True(steps[i].LetteringZones >= steps[i - 1].LetteringZones);
        }
        Assert.Same(AutoRules.Default, steps[RealtimeProfile.MiddleStep - 1]);
    }

    [Fact]
    public void LineSpacingWidensEveryGrouperTheSameWay()
    {
        var steps = Enumerable.Range(RealtimeProfile.MinStep, RealtimeProfile.MaxStep)
            .Select(step => ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", LineSpacing = step }).Auto)
            .ToList();

        for (int i = 1; i < steps.Count; i++)
        {
            Assert.True(steps[i].MaxStackGap > steps[i - 1].MaxStackGap);
            Assert.True(steps[i].PassageLeading > steps[i - 1].PassageLeading);
            Assert.True(steps[i].PanelGrouping.SolidLineAdvanceWhenWrapped >
                steps[i - 1].PanelGrouping.SolidLineAdvanceWhenWrapped);
            // Only the leading moves: the panel grouper is still the live path's.
            Assert.Equal(GroupingProfile.Realtime with
            {
                SolidLineAdvanceWhenWrapped = steps[i].PanelGrouping.SolidLineAdvanceWhenWrapped,
            }, steps[i].PanelGrouping);
        }
    }

    [Fact]
    public void TurningUnpunctuatedMergingOffChangesNothingElse()
    {
        var profile = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", MergeUnpunctuated = false });

        Assert.Equal(AutoRules.Default with { Lettering = false }, profile.Auto);
    }

    [Theory]
    [InlineData(RealtimePresets.TimingAuto, RealtimeBlockMode.Subtitle, true)]
    [InlineData(RealtimePresets.TimingAuto, RealtimeBlockMode.Panel, false)]
    [InlineData(RealtimePresets.TimingConfirm, RealtimeBlockMode.Panel, true)]
    [InlineData(RealtimePresets.TimingSettle, RealtimeBlockMode.Subtitle, false)]
    public void TheTimingFollowsTheReadingUnlessItIsSet(string timing, RealtimeBlockMode readsAs, bool dialogue) =>
        Assert.Equal(dialogue, ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", ReadTiming = timing }).Dialogue(readsAs));

    [Fact]
    public void AutomaticSettlingIsTheRuleEachTimingHad()
    {
        Assert.Equal(0, ResolvedProfile.Standard.TextSettlePolls(dialogue: true));
        Assert.Equal(RealtimeRegionState.MaxTextUnsettledPolls, ResolvedProfile.Standard.TextSettlePolls(dialogue: false));
        Assert.Equal(4, (ResolvedProfile.Standard with { SettlePolls = 4 }).TextSettlePolls(dialogue: true));
    }

    [Fact]
    public void TheLogLineCarriesNoTextAndEveryValue()
    {
        var line = ResolvedProfile.Interface.Describe();

        Assert.Contains("profile=interface", line);
        Assert.Contains("layout=Panel", line);
        Assert.Contains("confidence=0.8", line);
    }

    [Fact]
    public void ARegionNamedByLayoutIsOnThatPreset()
    {
        var region = new RealtimeRegion(0, new Rectangle(0, 0, 10, 10), RealtimeBlockMode.Panel);

        Assert.Same(ResolvedProfile.Interface, region.Profile);
        Assert.Same(ResolvedProfile.Subtitle, new RealtimeRegion(0, new Rectangle(0, 0, 10, 10)).Profile);
    }

    // ── 辨識解析度 ──

    [Fact]
    public void TheUsersFractionStartsTheChain()
    {
        var (primary, fallbacks) = RealtimeDetectorSize.For(2000, 300, RealtimeBlockMode.Subtitle, fraction: 0.5);

        Assert.Equal(1024, primary);
        // Furthest of the two measured fractions first, then the native size.
        Assert.Equal(new[] { 1728, 2000 }, fallbacks);
    }

    [Fact]
    public void AFullFractionReadsTheNativeSize()
    {
        var (primary, fallbacks) = RealtimeDetectorSize.For(2000, 300, RealtimeBlockMode.Panel, fraction: 1.0);

        Assert.Equal(2000, primary);
        Assert.Equal(new[] { 1376 }, fallbacks);
    }

    [Theory]
    [InlineData(RealtimeBlockMode.Subtitle)]
    [InlineData(RealtimeBlockMode.Panel)]
    public void NoFractionIsTheMeasuredRule(RealtimeBlockMode mode) =>
        SameSizes(
            RealtimeDetectorSize.For(2000, 300, mode),
            RealtimeDetectorSize.For(2000, 300, mode, fraction: null));

    [Fact]
    public void ASmallOrVerticalBlockIgnoresTheFraction()
    {
        SameSizes(RealtimeDetectorSize.For(700, 200, RealtimeBlockMode.Panel),
            RealtimeDetectorSize.For(700, 200, RealtimeBlockMode.Panel, fraction: 0.5));
        SameSizes(RealtimeDetectorSize.For(2000, 1200, RealtimeBlockMode.Panel, RealtimeTextOrientation.Vertical),
            RealtimeDetectorSize.For(2000, 1200, RealtimeBlockMode.Panel, RealtimeTextOrientation.Vertical, 0.5));
    }

    private static void SameSizes(
        (int Primary, IReadOnlyList<int> Fallbacks) expected, (int Primary, IReadOnlyList<int> Fallbacks) actual)
    {
        Assert.Equal(expected.Primary, actual.Primary);
        Assert.Equal(expected.Fallbacks, actual.Fallbacks);
    }

    // ── 忽略小字 ──

    [Fact]
    public void NotIgnoringSmallTextHandsBackTheSameList()
    {
        var blocks = new List<OcrTextBlock> { Line("台詞です", 0, 40), Line("ふりがな", 50, 12) };

        Assert.Same(blocks, OcrService.IgnoreSmallText(blocks, 0));
    }

    [Fact]
    public void ReadingsOverKanjiAreSmallText()
    {
        var blocks = new List<OcrTextBlock>
        {
            Line("きょう", 0, 12),
            Line("今日はいい天気ですね", 14, 40),
            Line("明日も晴れるでしょう", 60, 40),
        };

        var kept = OcrService.IgnoreSmallText(blocks, 0.6);

        Assert.Equal(new[] { "今日はいい天気ですね", "明日も晴れるでしょう" }, kept.Select(b => b.Text));
    }

    [Fact]
    public void OneHeadingDoesNotMakeTheDialogueSmall()
    {
        var blocks = new List<OcrTextBlock>
        {
            Line("第一章", 0, 90),
            Line("今日はいい天気ですね", 100, 40),
            Line("明日も晴れるでしょう", 150, 40),
        };

        Assert.Equal(3, OcrService.IgnoreSmallText(blocks, 0.8).Count);
    }

    [Fact]
    public void ThePresetsGroupAsTheLayoutsDid()
    {
        var blocks = new List<OcrTextBlock>
        {
            Line("The quick brown fox", 0, 30),
            Line("jumps over the lazy dog.", 36, 30),
            Line("Options", 200, 30),
        };

        foreach (var mode in new[] { RealtimeBlockMode.Auto, RealtimeBlockMode.Subtitle, RealtimeBlockMode.Panel })
            Assert.Equal(
                OcrService.GroupRealtime([.. blocks], 400, mode).Select(b => (b.Text, b.Bounds)),
                OcrService.GroupRealtime([.. blocks], 400, ResolvedProfile.ForLayout(mode)).Select(b => (b.Text, b.Bounds)));
    }

    /// <summary>
    /// Every value at both ends of its range, alone and all together, groups every kind of frame
    /// without throwing. The same sweep over the whole capture corpus is OcrHarness --auto-probe
    /// extremes.
    /// </summary>
    [Fact]
    public void EveryValueAtEitherEnd_GroupsAnyFrame()
    {
        var variants = new List<RealtimeProfile>();
        foreach (var layout in new[] { RealtimePresets.LayoutAuto, RealtimePresets.LayoutPassage, RealtimePresets.LayoutItems })
            foreach (var end in new[] { RealtimeProfile.MinStep, RealtimeProfile.MaxStep })
            {
                variants.Add(new RealtimeProfile { Id = "m", Layout = layout, MergeTendency = end });
                variants.Add(new RealtimeProfile { Id = "m", Layout = layout, LineSpacing = end });
                variants.Add(new RealtimeProfile { Id = "m", Layout = layout, ConfidenceLevel = end });
                variants.Add(new RealtimeProfile { Id = "m", Layout = layout, MergeUnpunctuated = end == RealtimeProfile.MinStep });
                variants.Add(new RealtimeProfile
                {
                    Id = "m", Layout = layout, IgnoreSmallPercent = end == RealtimeProfile.MinStep ? 0 : RealtimeProfile.MaxIgnoreSmallPercent,
                });
                variants.Add(new RealtimeProfile
                {
                    Id = "m", Layout = layout, MergeTendency = end, LineSpacing = end,
                    ConfidenceLevel = RealtimeProfile.MaxStep + RealtimeProfile.MinStep - end,
                    IgnoreSmallPercent = RealtimeProfile.MaxIgnoreSmallPercent, MergeUnpunctuated = false,
                });
            }

        List<OcrTextBlock>[] frames =
        [
            [],
            [Line("Hi", 0, 30)],
            [Line("ふりがな", 0, 10), Line("今日はいい天気ですね。", 12, 40), Line("明日も", 56, 40), Line("NEW GAME", 140, 24),
             Line("CONTINUE", 170, 24), Line("OPTIONS", 200, 24), Line("Lv.12 HP 300/300", 260, 18) with { Confidence = 0.6 },
             Line("#", 300, 380)],
        ];

        foreach (var stored in variants)
        {
            var profile = ResolvedProfile.Resolve(stored);
            foreach (var frame in frames)
                OcrService.GroupRealtime([.. frame], 400, profile);
        }
    }

    // ── 譯文保留, 等文字停下 ──

    [Fact]
    public void OneEmptyReadingClearsABlockSetToOne()
    {
        var state = new RealtimeRegionState(clearAfterEmptyReads: 1);
        var frame = new Frame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        state.MarkRendered([], frame.Capture, "");

        Assert.True(state.ShouldClearOverlay);
    }

    [Fact]
    public void FourEmptyReadingsAreWaitedForByABlockSetToFour()
    {
        var state = new RealtimeRegionState(clearAfterEmptyReads: 4);
        var frame = new Frame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        for (int i = 0; i < 3; i++) state.MarkRendered([], frame.Capture, "");
        Assert.False(state.ShouldClearOverlay);

        state.MarkRendered([], frame.Capture, "");
        Assert.True(state.ShouldClearOverlay);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(true, 3)]
    [InlineData(false, 3)]
    public void TextThatKeepsChangingIsReadOnceTheWaitIsOver(bool dialogue, int settle)
    {
        var state = new RealtimeRegionState();
        var frame = new Frame();
        state.MarkRendered(OneLine, frame.Capture, "hello");

        int polls = 0;
        do
        {
            frame.ChangeText();
            polls++;
        }
        while (!state.Observe(frame.Capture, dialogue, null, settle));

        Assert.Equal(settle + 1, polls);
    }

    private static readonly List<Rectangle> OneLine = [new Rectangle(10, 40, 300, 20)];

    private static OcrTextBlock Line(string text, double top, double height)
    {
        var bounds = new System.Windows.Rect(10, top, text.Length * height, height);
        return new OcrTextBlock(text, bounds, Confidence: 0.99, LayoutBounds: bounds);
    }

    private sealed class Frame
    {
        private byte _text = 200;

        public void ChangeText() => _text -= 40;

        public FrameFingerprint Capture(IReadOnlyList<Rectangle>? bands)
        {
            var cells = new byte[100];
            Array.Fill(cells, bands is null ? (byte)20 : _text);
            return new FrameFingerprint(cells);
        }
    }
}
