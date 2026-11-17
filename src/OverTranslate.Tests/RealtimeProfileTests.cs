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
        Assert.Equal(RealtimeRegionState.EmptyPassesBeforeClearing, profile.ClearAfterEmptyReads);
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
        var custom = new RealtimeProfile { Id = "mine", BasedOn = RealtimePresets.InterfaceId, ClearAfterEmptyReads = 1 };

        var profile = ResolvedProfile.Resolve("mine", [new RealtimeProfile { Id = "other" }, custom]);

        Assert.Equal("mine", profile.Id);
        Assert.Equal(RealtimeBlockMode.Panel, profile.Layout);
        Assert.Equal(1, profile.ClearAfterEmptyReads);
    }

    [Fact]
    public void AnUnsetValueFollowsThePresetAndASetOneDoesNot()
    {
        var profile = ResolvedProfile.Resolve(new RealtimeProfile
        {
            Id = "mine",
            BasedOn = RealtimePresets.SubtitleId,
            SettleSeconds = 0.3,
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
            SettleSeconds = double.NaN,
        });

        Assert.Equal(RealtimeBlockMode.Panel, profile.Layout);
        Assert.Equal(RealtimeReadTiming.SettleThenRead, profile.Timing);
        Assert.Equal(1, profile.SettlePolls);
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

    // 原文消失後立即清除譯文 is a tick: 1 ticked, 2 not. The 3 and 4 of the slider it replaced, never
    // released, read as not ticked.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(4, 2)]
    [InlineData(9, 2)]
    public void EmptyReadsAreHeldToTheirRange(int stored, int resolved) =>
        Assert.Equal(resolved, ResolvedProfile.Resolve(
            new RealtimeProfile { Id = "mine", ClearAfterEmptyReads = stored }).ClearAfterEmptyReads);

    [Theory]
    [InlineData(-1, null)]
    [InlineData(20, 0.6)]
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
    [InlineData(0, 0.60)]
    [InlineData(59, 0.60)]
    [InlineData(60, 0.60)]
    [InlineData(73, 0.73)]
    [InlineData(80, ShortReadingDetection.ShortTextConfidenceFloor)]
    [InlineData(95, 0.95)]
    [InlineData(100, 0.95)]
    public void TheShortTextThresholdIsHeldToItsRange(int stored, double floor) =>
        Assert.Equal(floor, ResolvedProfile.Resolve(
            new RealtimeProfile { Id = "mine", ConfidencePercent = stored }).ConfidenceFloor);

    /// <summary>
    /// The presets' 80 is the measured floor itself, the very same double — so a mode set to 80, or a
    /// preset, judges every short reading exactly as 即時翻譯 did before the value was offered.
    /// </summary>
    [Fact]
    public void EightyPercentIsTheMeasuredFloorItself()
    {
        Assert.All(RealtimePresets.All, preset => Assert.Equal(RealtimePresets.ConfidencePercent, preset.ConfidencePercent));
        var eighty = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", ConfidencePercent = 80 });
        Assert.Equal(BitConverter.DoubleToInt64Bits(ShortReadingDetection.ShortTextConfidenceFloor),
            BitConverter.DoubleToInt64Bits(eighty.ConfidenceFloor));
        Assert.Equal(ResolvedProfile.Standard with { Id = "m" }, eighty);
        foreach (var preset in RealtimePresets.All)
            Assert.Equal(BitConverter.DoubleToInt64Bits(ShortReadingDetection.ShortTextConfidenceFloor),
                BitConverter.DoubleToInt64Bits(ResolvedProfile.Resolve(preset.Id, []).ConfidenceFloor));
    }

    /// <summary>
    /// Each percent up drops every short reading the one below it dropped, and perhaps more — never
    /// keeps one back — on the lines before grouping and on the groups after it. The same check over
    /// the capture corpus is OcrHarness --auto-probe confidence.
    /// </summary>
    [Fact]
    public void TheConfidenceThresholdOnlyEverDropsMore()
    {
        // Short and long, across the whole scale, and a run of line-end symbols beside a line whose
        // own score decides whether it may keep them.
        List<OcrTextBlock> Frame() =>
        [
            At("決定", 0, 0, 40, 20) with { Confidence = 0.62 },
            At("セーブ", 0, 30, 60, 20) with { Confidence = 0.79 },
            At("Skill", 0, 60, 50, 20) with { Confidence = 0.80 },
            At("あれ?", 0, 90, 40, 20) with { Confidence = 0.86 },
            At("何なんですか", 100, 120, 120, 20) with { Confidence = 0.91 },
            At("……！？", 222, 120, 40, 20) with { Confidence = 0.70 },
            At("Lv 15", 0, 160, 50, 20) with { Confidence = 0.949 },
            At("HP 300/300", 0, 190, 100, 20) with { Confidence = 0.61 },
            At("This is a long line of text", 0, 220, 300, 20) with { Confidence = 0.65 },
            At("ok", 0, 250, 20, 20) with { Confidence = 0.999 },
        ];

        HashSet<string>? droppedBefore = null;
        HashSet<string>? groupsKeptBefore = null;
        for (int percent = RealtimeProfile.MinConfidencePercent; percent <= RealtimeProfile.MaxConfidencePercent; percent++)
        {
            var profile = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", ConfidencePercent = percent, MergeLevel = RealtimeMergeLevels.Min });
            var frame = Frame();
            var kept = OcrService.RejectUnconvincingBlocks(frame, profile.ConfidenceFloor).Select(b => b.Text).ToHashSet();
            var dropped = frame.Select(b => b.Text).Where(text => !kept.Contains(text)).ToHashSet();

            var groups = OcrService.GroupRealtime(Frame(), 400, profile);
            var shown = RealtimeTranslationSession.RejectShortReadings(groups, 0, profile.ConfidenceFloor).Select(g => g.Text).ToHashSet();
            var groupsKept = groups.Select(g => g.Text).Where(shown.Contains).ToHashSet();

            if (droppedBefore is not null)
            {
                Assert.Superset(droppedBefore, dropped);
                Assert.Subset(groupsKeptBefore!, groupsKept);
            }
            droppedBefore = dropped;
            groupsKeptBefore = groupsKept;
        }
        // The top of the scale takes a short line read at 0.949, never a long one.
        Assert.Contains("Lv 15", droppedBefore!);
        Assert.DoesNotContain("This is a long line of text", droppedBefore!);
    }

    [Fact]
    public void MergingTendsOneWayAlongTheAutomaticSteps()
    {
        var steps = Enumerable.Range(RealtimeMergeLevels.AutomaticLowest, 5)
            .Select(level => ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", MergeLevel = level }).Auto)
            .ToList();

        for (int i = 1; i < steps.Count; i++)
        {
            Assert.True(steps[i].SparseZones >= steps[i - 1].SparseZones);
            Assert.True(steps[i].StandsOut <= steps[i - 1].StandsOut);
            Assert.True(steps[i].LetteringZones >= steps[i - 1].LetteringZones);
        }
        Assert.Same(AutoRules.Default, steps[RealtimeMergeLevels.AutomaticMiddle - RealtimeMergeLevels.AutomaticLowest]);
    }

    // The measured scale (see ResolvedProfile.RulesFor), on the steps of 合併程度 that were 合併傾向
    // 1, 2, 4 and 5. The loosest step still tells a busy screen from a sparse one: with every frame
    // sparse, nearly every label of a panel was drawn as a subtitle.
    [Theory]
    [InlineData(2, 2, 1.6, 2)]
    [InlineData(3, 3, 1.45, 3)]
    [InlineData(5, 8, 1.2, 6)]
    [InlineData(6, 12, 1.1, 8)]
    public void MergingStepsAreTheMeasuredOnes(int level, int sparse, double standsOut, int lettering)
    {
        var rules = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", MergeLevel = level }).Auto;

        Assert.Equal((sparse, standsOut, lettering), (rules.SparseZones, rules.StandsOut, rules.LetteringZones));
        Assert.Equal(AutoRules.Default with { SparseZones = sparse, StandsOut = standsOut, LetteringZones = lettering }, rules);
    }

    // 行距容許 is not offered: every step of 合併程度 groups on the measured row spacing, the
    // groupers' own constants and instance.
    [Fact]
    public void EveryMergeStepKeepsTheMeasuredRowSpacing()
    {
        for (int level = RealtimeMergeLevels.Min; level <= RealtimeMergeLevels.Max; level++)
        {
            var rules = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", MergeLevel = level }).Auto;

            Assert.Equal(DialogueTextGrouper.MaxStackGap, rules.MaxStackGap);
            Assert.Equal(RealtimeAutoLayout.PassageLeading, rules.PassageLeading);
            Assert.Same(GroupingProfile.Realtime, rules.PanelGrouping);
        }
    }

    // 沒有標點也合併 is not offered: every step keeps unpunctuated rows able to be a passage.
    [Fact]
    public void UnpunctuatedRowsMayBeAPassageOnEveryStep()
    {
        for (int level = RealtimeMergeLevels.Min; level <= RealtimeMergeLevels.Max; level++)
            Assert.True(ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", MergeLevel = level }).Auto.Lettering);
    }

    [Theory]
    [InlineData(-1.0, RealtimeBlockMode.Subtitle, true)]
    [InlineData(-1.0, RealtimeBlockMode.Panel, false)]
    [InlineData(0.0, RealtimeBlockMode.Panel, true)]
    [InlineData(0.3, RealtimeBlockMode.Subtitle, false)]
    [InlineData(1.5, RealtimeBlockMode.Subtitle, false)]
    public void TheTimingFollowsTheReadingUnlessAWaitIsSet(double seconds, RealtimeBlockMode readsAs, bool dialogue) =>
        Assert.Equal(dialogue, ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", SettleSeconds = seconds }).Dialogue(readsAs));

    [Theory]
    [InlineData(-1.0, (int)RealtimeReadTiming.Auto)]
    [InlineData(0.0, (int)RealtimeReadTiming.ReadThenConfirm)]
    [InlineData(0.04, (int)RealtimeReadTiming.ReadThenConfirm)]
    [InlineData(0.15, (int)RealtimeReadTiming.SettleThenRead)]
    [InlineData(1.5, (int)RealtimeReadTiming.SettleThenRead)]
    public void NoWaitReadsAtOnce_AnyWaitSettlesFirst(double seconds, int timing) =>
        Assert.Equal((RealtimeReadTiming)timing, ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", SettleSeconds = seconds }).Timing);

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
        Assert.Contains("merge=Items", line);
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

    // screen-subtitle-ko: one subtitle line over a scoreboard whose labels and numbers outnumber
    // it in characters. Counting every character made the scoreboard the main text.
    [Fact]
    public void ASubtitleOverAnInterfaceIsTheMainText()
    {
        var blocks = new List<OcrTextBlock>
        {
            Line("26/3/19", 0, 50), Line("27,003 841695", 60, 52), Line("TK / K / A", 120, 49),
            Line("21:54", 180, 50), Line("대상지정 불가", 240, 46), Line("아이작", 300, 47),
            Line("지금처럼 도파민은 없을 거야", 400, 110),
        };

        var kept = OcrService.IgnoreSmallText(blocks, 0.5);

        Assert.Equal(new[] { "지금처럼 도파민은 없을 거야" }, kept.Select(b => b.Text));
    }

    // region-comic-en-2: a balloon of large shouting over a smaller one that says more.
    [Fact]
    public void TheBalloonThatSaysMoreIsTheMainText()
    {
        var blocks = new List<OcrTextBlock>
        {
            Line("THEN WHY", 0, 135), Line("DON'T YOU GET", 140, 135), Line("OFF ME!!!", 280, 138),
            Line("BUT WHAT CAN", 500, 44), Line("I DO~ IT'S TOO HARD", 550, 43), Line("FOR ME TO WALK~!", 600, 44),
        };

        Assert.Equal(6, OcrService.IgnoreSmallText(blocks, 0.5).Count);
    }

    [Fact]
    public void WithoutProseEveryCharacterCounts()
    {
        var blocks = new List<OcrTextBlock>
        {
            Line("Save", 0, 40), Line("Load", 50, 40), Line("Options", 100, 40), Line("Quit", 150, 40),
            Line("MENU", 250, 90),
            Line("v1.2", 400, 14),
        };

        Assert.Equal(new[] { "Save", "Load", "Options", "Quit", "MENU" },
            OcrService.IgnoreSmallText(blocks, 0.5).Select(b => b.Text));
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

    // ── 合併程度 ──

    [Theory]
    [InlineData(RealtimePresets.StandardId, RealtimeMergeLevels.AutomaticMiddle)]
    [InlineData(RealtimePresets.SubtitleId, RealtimeMergeLevels.Passage)]
    [InlineData(RealtimePresets.InterfaceId, RealtimeMergeLevels.Items)]
    public void ThePresetsSitOnTheirStepsOfMerging(string id, int level) =>
        Assert.Equal(level, RealtimePresets.Create(id).MergeLevel);

    [Theory]
    [InlineData(-3, 0)]
    [InlineData(0, 0)]
    [InlineData(5, 5)]
    [InlineData(8, 8)]
    [InlineData(12, 8)]
    public void TheMergeLevelIsHeldToItsRange(int stored, int filled) =>
        Assert.Equal(filled, new RealtimeProfile { Id = "m", MergeLevel = stored }.Filled().MergeLevel);

    [Fact]
    public void AnUnsetMergeLevelFollowsThePreset()
    {
        foreach (var preset in RealtimePresets.All)
            Assert.Equal(preset.MergeLevel,
                new RealtimeProfile { Id = "m", BasedOn = preset.Id }.Filled().MergeLevel);
    }

    [Theory]
    [InlineData(0, (int)RealtimeMerge.Lines, RealtimeBlockMode.Panel)]
    [InlineData(1, (int)RealtimeMerge.Items, RealtimeBlockMode.Panel)]
    [InlineData(2, (int)RealtimeMerge.Auto, RealtimeBlockMode.Auto)]
    [InlineData(4, (int)RealtimeMerge.Auto, RealtimeBlockMode.Auto)]
    [InlineData(6, (int)RealtimeMerge.Auto, RealtimeBlockMode.Auto)]
    [InlineData(7, (int)RealtimeMerge.Passage, RealtimeBlockMode.Subtitle)]
    [InlineData(8, (int)RealtimeMerge.Merged, RealtimeBlockMode.Subtitle)]
    public void EachStepGroupsItsOwnWay_AndTheEndsReadAsTheirNeighbours(int level, int merge, RealtimeBlockMode layout)
    {
        var profile = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", MergeLevel = level });

        Assert.Equal((RealtimeMerge)merge, profile.Merge);
        Assert.Equal(layout, profile.Layout);
    }

    [Theory]
    [InlineData(RealtimeMergeLevels.Items, RealtimePresets.InterfaceId)]
    [InlineData(RealtimeMergeLevels.AutomaticMiddle, RealtimePresets.StandardId)]
    [InlineData(RealtimeMergeLevels.Passage, RealtimePresets.SubtitleId)]
    public void ACustomModeOnAPresetsStepIsThatPreset(int level, string preset)
    {
        // The wait and the detector size are values of their own now, pinned in
        // RealtimeTimingDetectorTests; here, everything else.
        var expected = ResolvedProfile.Resolve(preset, []) with { Id = "m" };
        foreach (var basedOn in RealtimePresets.All)
        {
            var mine = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", BasedOn = basedOn.Id, MergeLevel = level });
            Assert.Equal(expected, mine with
            {
                Timing = expected.Timing, SettlePolls = expected.SettlePolls, DetectorFraction = expected.DetectorFraction,
            });
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    public void TheStepsOffAutomatic_ReadWithTheMeasuredRules(int level) =>
        Assert.Same(AutoRules.Default, ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", MergeLevel = level }).Auto);

    // A wrapped sentence the detector framed in two pieces on its first line: 逐項 joins the pieces
    // and the lines; 不合併 the pieces only.
    private static List<OcrTextBlock> WrappedSentence() =>
    [
        At("The quick brown fox jumps", 10, 10, 500, 30),
        At("over the lazy", 518, 10, 260, 30),
        At("dog and runs far away into the woods.", 10, 46, 740, 30),
    ];

    [Fact]
    public void NotMerging_KeepsEveryLineApart_ButJoinsThePiecesOfOne()
    {
        var items = OcrService.GroupRealtime(WrappedSentence(), 400, ProfileAt(RealtimeMergeLevels.Items));
        var lines = OcrService.GroupRealtime(WrappedSentence(), 400, ProfileAt(RealtimeMergeLevels.Min));

        // The frame is one 逐項 joins, so the test says something.
        Assert.Single(items);
        Assert.Equal(
            ["The quick brown fox jumps over the lazy", "dog and runs far away into the woods."],
            lines.Select(group => group.Text));
        Assert.All(lines, group => Assert.True(group.Bounds.Height <= 30));
    }

    [Fact]
    public void NotMerging_JoinsOnARowWhatItemsJoins()
    {
        var items = OcrTextBlockGrouper.Group(WrappedSentence(), GroupingProfile.Realtime);
        var lines = OcrTextBlockGrouper.GroupLines(WrappedSentence(), GroupingProfile.Realtime);

        Assert.Equal(items[0].Text, string.Join(" ", lines.Select(line => line.Text)));
    }

    // ja-aa-nameplate-region: a dialogue box's 》 sits on its second line's row, which 整段 takes
    // for a row it cannot stack.
    private static List<OcrTextBlock> DialogueWithAnArrow() =>
    [
        At("オレのシゴトは、あの暗い墓場を", 378, 61, 967, 73),
        At("》", 1722, 115, 72, 79),
        At("暗い目で見守るコトなのさ。", 376, 144, 804, 76),
    ];

    [Fact]
    public void Merging_JoinsADialogueAcrossTheArrowBesideIt()
    {
        var passage = OcrService.GroupRealtime(DialogueWithAnArrow(), 292, ProfileAt(RealtimeMergeLevels.Passage));
        var merged = OcrService.GroupRealtime(DialogueWithAnArrow(), 292, ProfileAt(RealtimeMergeLevels.Max));

        Assert.Equal(3, passage.Count);
        Assert.Equal(["オレのシゴトは、あの暗い墓場を 暗い目で見守るコトなのさ。", "》"], merged.Select(group => group.Text));
        Assert.All(merged, group => Assert.Null(group.ReadAs));
    }

    [Fact]
    public void Merging_JoinsLinesOfDifferentSizes()
    {
        List<OcrTextBlock> Balloon() => [At("THEN WHY DON'T YOU", 10, 10, 600, 60), At("GET OFF ME", 60, 76, 400, 32)];

        Assert.Equal(2, OcrService.GroupRealtime(Balloon(), 400, ProfileAt(RealtimeMergeLevels.Passage)).Count);
        Assert.Equal("THEN WHY DON'T YOU GET OFF ME",
            Assert.Single(OcrService.GroupRealtime(Balloon(), 400, ProfileAt(RealtimeMergeLevels.Max))).Text);
    }

    [Fact]
    public void Merging_StillPartsWhatIsClearlyApart()
    {
        // A small name over the dialogue, not sticking out of it.
        List<OcrTextBlock> named = [At("Lua", 10, 10, 60, 20), At("Hey, did you know Ms. Freya here is a writer?", 10, 36, 700, 30)];
        // Labels with their values beside them: a table, not a sentence.
        List<OcrTextBlock> table =
        [
            At("Amenities from Civics:", 10, 10, 400, 30), At("+2", 600, 10, 40, 30),
            At("Amenities from Religion:", 10, 46, 420, 30), At("+1", 600, 46, 40, 30),
        ];
        // Too far apart, and side by side in two columns.
        List<OcrTextBlock> apart =
        [
            At("The quick brown fox jumps.", 10, 10, 500, 30), At("Over the lazy dog.", 10, 100, 360, 30),
            At("Another column of text", 800, 46, 400, 30),
        ];

        Assert.Equal(2, OcrService.GroupRealtime(named, 400, ProfileAt(RealtimeMergeLevels.Max)).Count);
        Assert.Equal(4, OcrService.GroupRealtime(table, 400, ProfileAt(RealtimeMergeLevels.Max)).Count);
        Assert.Equal(3, OcrService.GroupRealtime(apart, 400, ProfileAt(RealtimeMergeLevels.Max)).Count);
        Assert.Empty(OcrService.GroupRealtime([], 400, ProfileAt(RealtimeMergeLevels.Max)));
    }

    [Fact]
    public void TheStepsJoinMoreTowardsTheRight()
    {
        int Groups(int level) => OcrService.GroupRealtime(WrappedSentence(), 400, ProfileAt(level)).Count;

        Assert.True(Groups(RealtimeMergeLevels.Min) > Groups(RealtimeMergeLevels.Items));
        Assert.Equal(1, Groups(RealtimeMergeLevels.Max));
    }

    // ── What the page writes beside each value ──

    [Theory]
    [InlineData(RealtimeMergeLevels.Min, 0.15, 68)]
    [InlineData(RealtimeMergeLevels.Items, 0.15, 68)]
    [InlineData(RealtimeMergeLevels.AutomaticLowest, null, null)]
    [InlineData(RealtimeMergeLevels.AutomaticMiddle, null, null)]
    [InlineData(RealtimeMergeLevels.AutomaticHighest, null, null)]
    [InlineData(RealtimeMergeLevels.Passage, 0.0, 85)]
    [InlineData(RealtimeMergeLevels.Max, 0.0, 85)]
    public void AutomaticComesToWhatTheStepReadsAs(int level, double? settle, int? detector)
    {
        var actual = RealtimeActualValues.Of(new RealtimeProfile { Id = "m", MergeLevel = level });

        Assert.Equal(level, actual.MergeLevel);
        Assert.True(actual.SettleIsAutomatic);
        Assert.Equal(settle, actual.SettleSeconds);
        Assert.True(actual.DetectorIsAutomatic);
        Assert.Equal(detector, actual.DetectorPercent);
    }

    [Fact]
    public void ThePresetsActualValues()
    {
        Assert.Equal(new RealtimeActualValues(4, true, null, true, null),
            RealtimeActualValues.Of(RealtimePresets.StandardId, []));
        Assert.Equal(new RealtimeActualValues(7, false, 0.0, false, 85),
            RealtimeActualValues.Of(RealtimePresets.SubtitleId, null));
        Assert.Equal(new RealtimeActualValues(1, false, 0.15, false, 68),
            RealtimeActualValues.Of(RealtimePresets.InterfaceId, null));
    }

    [Fact]
    public void ASetWait_IsShownAsSetEvenOnTheAutomaticSteps()
    {
        var none = RealtimeActualValues.Of(new RealtimeProfile { Id = "m", SettleSeconds = 0 });
        var wait = RealtimeActualValues.Of(new RealtimeProfile { Id = "m", SettleSeconds = 0.3 });

        Assert.Equal((false, 0.0), (none.SettleIsAutomatic, none.SettleSeconds));
        Assert.Equal((false, 0.3), (wait.SettleIsAutomatic, wait.SettleSeconds));
        // The detector still follows what the block holds.
        Assert.Null(none.DetectorPercent);
    }

    [Fact]
    public void SetValuesAreShownAsSet_HeldToTheirRange()
    {
        var actual = RealtimeActualValues.Of(new RealtimeProfile
        {
            Id = "m", MergeLevel = 20, SettleSeconds = 0.45, DetectorPercent = 30,
        });

        Assert.Equal(RealtimeMergeLevels.Max, actual.MergeLevel);
        Assert.False(actual.SettleIsAutomatic);
        Assert.Equal(0.45, actual.SettleSeconds);
        Assert.False(actual.DetectorIsAutomatic);
        Assert.Equal(RealtimeProfile.MinDetectorPercent, actual.DetectorPercent);
    }

    [Fact]
    public void TheActualValuesOfAModeFoundById()
    {
        var mine = new RealtimeProfile { Id = "mine", BasedOn = RealtimePresets.SubtitleId, MergeLevel = RealtimeMergeLevels.Min };

        Assert.Equal(RealtimeMergeLevels.Min, RealtimeActualValues.Of("mine", [mine]).MergeLevel);
        Assert.Equal(RealtimeActualValues.Of(RealtimePresets.StandardId, null), RealtimeActualValues.Of("deleted", [mine]));
    }

    private static ResolvedProfile ProfileAt(int level) =>
        ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", MergeLevel = level });

    private static OcrTextBlock At(string text, double x, double top, double width, double height)
    {
        var bounds = new System.Windows.Rect(x, top, width, height);
        return new OcrTextBlock(text, bounds, Confidence: 0.99, LayoutBounds: bounds);
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
        for (int level = RealtimeMergeLevels.Min; level <= RealtimeMergeLevels.Max; level++)
            foreach (var end in new[] { RealtimeProfile.MinStep, RealtimeProfile.MaxStep })
            {
                variants.Add(new RealtimeProfile { Id = "m", MergeLevel = level });
                variants.Add(new RealtimeProfile
                {
                    Id = "m", MergeLevel = level,
                    ConfidencePercent = end == RealtimeProfile.MinStep ? RealtimeProfile.MinConfidencePercent : RealtimeProfile.MaxConfidencePercent,
                });
                variants.Add(new RealtimeProfile
                {
                    Id = "m", MergeLevel = level, IgnoreSmallPercent = end == RealtimeProfile.MinStep ? 0 : RealtimeProfile.MaxIgnoreSmallPercent,
                });
                variants.Add(new RealtimeProfile
                {
                    Id = "m", MergeLevel = level,
                    ConfidencePercent = end == RealtimeProfile.MinStep ? RealtimeProfile.MaxConfidencePercent : RealtimeProfile.MinConfidencePercent,
                    IgnoreSmallPercent = RealtimeProfile.MaxIgnoreSmallPercent,
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
