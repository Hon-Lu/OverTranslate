using OverTranslate.Models;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The rules the 模式 card and the edit layer's list follow, held apart from their controls: the
/// automatic names, what counts as changed, what a deletion leaves selected, and what the list
/// offers.
/// </summary>
public class RealtimeProfileEditingTests
{
    private static string NewName(int n) => $"Custom {n}";

    private static string PresetName(string id) => id switch
    {
        RealtimePresets.SubtitleId => "Subtitles",
        RealtimePresets.InterfaceId => "Interface",
        _ => "Standard",
    };

    private static RealtimeProfile Custom(string id, string name, string basedOn = RealtimePresets.StandardId) =>
        new() { Id = id, Name = name, BasedOn = basedOn };

    // ── Automatic numbering ──

    [Fact]
    public void NextNumber_StartsAtOne()
    {
        Assert.Equal(1, RealtimeProfileEditing.NextNumber([], NewName));
    }

    [Fact]
    public void NextNumber_TakesTheSmallestFreeNumber()
    {
        Assert.Equal(2, RealtimeProfileEditing.NextNumber(["Custom 1", "Custom 3"], NewName));
    }

    [Fact]
    public void NextNumber_IgnoresCaseAndSurroundingSpace_AndOtherNames()
    {
        Assert.Equal(3, RealtimeProfileEditing.NextNumber([" custom 1 ", "CUSTOM 2", "Visual novel"], NewName));
    }

    // ── A mode split off a preset ──

    private static string Fork(params string[] names) =>
        RealtimeProfileEditing.ForkName("Subtitles", names, name => $"{name} (custom)", (name, n) => $"{name} (custom {n})");

    [Fact]
    public void ForkName_IsThePresetsName_MarkedAsCustom()
    {
        Assert.Equal("Subtitles (custom)", Fork("Standard", "Subtitles", "Interface"));
    }

    [Fact]
    public void ForkName_NumbersFromTwo_TakingTheSmallestFree()
    {
        Assert.Equal("Subtitles (custom 2)", Fork("Subtitles (custom)"));
        Assert.Equal("Subtitles (custom 3)", Fork("subtitles (CUSTOM)", "Subtitles (custom 2)", "Subtitles (custom 4)"));
    }

    [Fact]
    public void ForkName_KeepsToTheNameLimit()
    {
        var name = RealtimeProfileEditing.ForkName(new string('x', 40), [], n => $"{n} (custom)", (n, i) => $"{n} (custom {i})");
        Assert.Equal(RealtimeProfileEditing.MaxNameLength, name.Length);
    }

    [Fact]
    public void Follow_MovesOnlyTheBlocksOnThePreset()
    {
        var blocks = new[]
        {
            new Services.Realtime.RealtimeBlockPlacement(new System.Drawing.Rectangle(0, 0, 10, 10), RealtimePresets.SubtitleId),
            new Services.Realtime.RealtimeBlockPlacement(new System.Drawing.Rectangle(0, 20, 10, 10), RealtimePresets.StandardId),
            new Services.Realtime.RealtimeBlockPlacement(new System.Drawing.Rectangle(0, 40, 10, 10), RealtimePresets.SubtitleId,
                Services.Realtime.RealtimeTextOrientation.Vertical),
        };

        var moved = RealtimeProfileEditing.Follow(blocks, RealtimePresets.SubtitleId, "mine");

        Assert.Equal(["mine", RealtimePresets.StandardId, "mine"], moved.Select(block => block.ProfileId));
        Assert.Equal(blocks.Select(block => block.Bounds), moved.Select(block => block.Bounds));
        Assert.Equal(Services.Realtime.RealtimeTextOrientation.Vertical, moved[2].Orientation);
    }

    [Fact]
    public void AChangeOnASplitMode_IsKeptAsADifferenceFromItsPreset()
    {
        var split = RealtimeProfile.CreateCustom(RealtimePresets.SubtitleId, "Subtitles (custom)");
        split.MergeLevel = RealtimeMergeLevels.AutomaticMiddle;
        var stored = RealtimeProfileEditing.WithoutTemplateValues(split);

        Assert.Equal(RealtimeMergeLevels.AutomaticMiddle, stored.MergeLevel);
        Assert.True(RealtimeProfileEditing.IsModified(stored, ProfileParam.MergeLevel));
        Assert.Equal(1, RealtimeProfileEditing.ModifiedCount(stored));
    }

    // ── Names ──

    [Theory]
    [InlineData("  Visual novel  ", "Visual novel")]
    [InlineData("line\r\nbreak", "line  break")]
    [InlineData("tab\there", "tab here")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void CleanName_IsOneTrimmedLine(string? typed, string expected)
    {
        Assert.Equal(expected, RealtimeProfileEditing.CleanName(typed));
    }

    [Fact]
    public void CleanName_IsCutToTheLimit()
    {
        var name = RealtimeProfileEditing.CleanName(new string('x', 40));
        Assert.Equal(RealtimeProfileEditing.MaxNameLength, name.Length);
    }

    [Fact]
    public void NameTaken_CountsPresetNamesAndOtherCustomModes_ButNotItself()
    {
        var choices = RealtimeProfileEditing.Choices([Custom("a", "Mine"), Custom("b", "Other")], PresetName, NewName);

        Assert.True(RealtimeProfileEditing.NameTaken("standard", "a", choices));
        Assert.True(RealtimeProfileEditing.NameTaken(" other ", "a", choices));
        Assert.False(RealtimeProfileEditing.NameTaken("MINE", "a", choices));
        Assert.False(RealtimeProfileEditing.NameTaken("New", "a", choices));
    }

    // ── What counts as changed ──

    [Fact]
    public void AValueEqualToThePreset_IsStoredAsFollowingIt()
    {
        var mode = Custom("a", "Mine", RealtimePresets.SubtitleId);
        mode.MergeLevel = RealtimeMergeLevels.Passage;  // what 字幕・漫畫 holds
        mode.ClearAfterEmptyReads = 2;                 // what every preset holds
        mode.SettleSeconds = 0.004;                     // 不等待, written oddly
        mode.DetectorPercent = 70;                      // really different

        var tidy = RealtimeProfileEditing.WithoutTemplateValues(mode);

        Assert.Null(tidy.MergeLevel);
        Assert.Null(tidy.ClearAfterEmptyReads);
        Assert.Null(tidy.SettleSeconds);
        Assert.Equal(70, tidy.DetectorPercent);
        Assert.Equal(1, RealtimeProfileEditing.ModifiedCount(mode, RealtimeProfileEditing.All));
    }

    [Fact]
    public void WithoutTemplateValues_LeavesTheStoredModeAlone()
    {
        var mode = Custom("a", "Mine");
        mode.MergeLevel = RealtimeMergeLevels.AutomaticMiddle;

        RealtimeProfileEditing.WithoutTemplateValues(mode);

        Assert.Equal(RealtimeMergeLevels.AutomaticMiddle, mode.MergeLevel);
    }

    [Fact]
    public void SecondsCompareAtTheHundredth()
    {
        var mode = Custom("a", "Mine");
        mode.SettleSeconds = 0.3;
        var same = Custom("b", "Same");
        same.SettleSeconds = 0.30000000004;

        Assert.Equal(
            RealtimeProfileEditing.Value(mode.Filled(), ProfileParam.SettleSeconds),
            RealtimeProfileEditing.Value(same.Filled(), ProfileParam.SettleSeconds));
    }

    [Fact]
    public void AdvancedCount_OnlyCountsTheAdvancedValues()
    {
        var mode = Custom("a", "Mine");
        mode.MergeLevel = RealtimeMergeLevels.Items;
        mode.IgnoreSmallPercent = 40;

        Assert.Equal(1, RealtimeProfileEditing.ModifiedCount(mode, RealtimeProfileEditing.Advanced));
        Assert.Equal(2, RealtimeProfileEditing.ModifiedCount(mode, RealtimeProfileEditing.All));
    }

    [Fact]
    public void TheShortTextThresholdIsUnderAdvanced_AfterIgnoringSmallText()
    {
        Assert.Equal([ProfileParam.IgnoreSmallPercent, ProfileParam.ConfidencePercent], RealtimeProfileEditing.Advanced);
        Assert.Equal(RealtimeProfileEditing.All, RealtimeProfileEditing.Shown);
        Assert.True(RealtimeProfileEditing.IsHorizontalOnly(ProfileParam.ConfidencePercent));
    }

    [Fact]
    public void TheModifiedCount_CountsTheValuesThePageShows_ThatReallyDiffer()
    {
        var mode = Custom("a", "Mine", RealtimePresets.SubtitleId);
        mode.MergeLevel = RealtimeMergeLevels.Passage;      // the preset's own: not a change
        mode.SettleSeconds = 0.6;
        mode.ConfidencePercent = 90;
        mode.IgnoreSmallPercent = 0;                        // the preset's own: not a change

        Assert.Equal(2, RealtimeProfileEditing.ModifiedCount(mode));
        Assert.Equal(0, RealtimeProfileEditing.ModifiedCount(Custom("b", "Untouched")));
        Assert.Equal(0, RealtimeProfileEditing.ModifiedCount(RealtimePresets.Create(RealtimePresets.InterfaceId)));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    public void OnlyTheMiddleStepsDecideGroupByGroup(int level, bool automatic) =>
        Assert.Equal(automatic, RealtimeMergeLevels.IsAutomatic(level));

    [Fact]
    public void ResetAll_FollowsThePresetEverywhere_AndKeepsNameAndTemplate()
    {
        var mode = Custom("a", "Mine", RealtimePresets.InterfaceId);
        mode.MergeLevel = RealtimeMergeLevels.AutomaticMiddle;
        mode.SettleSeconds = 1.2;
        mode.ConfidencePercent = 95;

        var reset = RealtimeProfileEditing.ResetAll(mode);

        Assert.All(RealtimeProfileEditing.All, param => Assert.False(RealtimeProfileEditing.IsSet(reset, param)));
        Assert.Equal("Mine", reset.Name);
        Assert.Equal(RealtimePresets.InterfaceId, reset.BasedOn);
    }

    [Fact]
    public void ClearPutsBackOneValueOnly()
    {
        var mode = Custom("a", "Mine");
        mode.MergeLevel = RealtimeMergeLevels.Items;
        mode.ClearAfterEmptyReads = 1;

        RealtimeProfileEditing.Clear(mode, ProfileParam.MergeLevel);

        Assert.Null(mode.MergeLevel);
        Assert.Equal(1, mode.ClearAfterEmptyReads);
    }

    // ── Deleting ──

    [Fact]
    public void AfterDelete_TheNextCustomModeIsShown()
    {
        var list = new[] { Custom("a", "A"), Custom("b", "B"), Custom("c", "C") };
        Assert.Equal("c", RealtimeProfileEditing.SelectionAfterDelete(list, "b"));
    }

    [Fact]
    public void AfterDeletingTheLast_ThePreviousIsShown()
    {
        var list = new[] { Custom("a", "A"), Custom("b", "B") };
        Assert.Equal("a", RealtimeProfileEditing.SelectionAfterDelete(list, "b"));
    }

    [Fact]
    public void AfterDeletingTheOnlyOne_ItsPresetIsShown()
    {
        var list = new[] { Custom("a", "A", RealtimePresets.SubtitleId) };
        Assert.Equal(RealtimePresets.SubtitleId, RealtimeProfileEditing.SelectionAfterDelete(list, "a"));
    }

    // ── The list ──

    [Fact]
    public void Choices_ArePresetsThenCustomModesInOrder()
    {
        var choices = RealtimeProfileEditing.Choices(
            [Custom("x", "Visual novel", RealtimePresets.SubtitleId), Custom("y", "Chat", "nonsense")], PresetName, NewName);

        Assert.Equal(
            [RealtimePresets.StandardId, RealtimePresets.SubtitleId, RealtimePresets.InterfaceId, "x", "y"],
            choices.Select(choice => choice.Id));
        Assert.Equal(["Standard", "Subtitles", "Interface", "Visual novel", "Chat"], choices.Select(choice => choice.Name));
        Assert.All(choices.Take(3), choice => Assert.True(choice.IsPreset));
        Assert.All(choices.Skip(3), choice => Assert.False(choice.IsPreset));
        Assert.Equal(RealtimePresets.SubtitleId, choices[3].BasedOn);
        Assert.Equal(RealtimePresets.StandardId, choices[4].BasedOn);
    }

    [Fact]
    public void Choices_WithNoCustomModes_AreTheThreePresets()
    {
        Assert.Equal(3, RealtimeProfileEditing.Choices([], PresetName, NewName).Count);
    }

    [Fact]
    public void Choices_NameAModeStoredWithoutOne_WithTheNextFreeNumber()
    {
        var choices = RealtimeProfileEditing.Choices(
            [Custom("a", "Custom 1"), Custom("b", ""), Custom("c", "  ")], PresetName, NewName);

        Assert.Equal(["Custom 1", "Custom 2", "Custom 3"], choices.Skip(3).Select(choice => choice.Name));
    }

    [Fact]
    public void Choices_AreComparedByValue_SoAnUnchangedListIsNotRebuilt()
    {
        var a = RealtimeProfileEditing.Choices([Custom("a", "A")], PresetName, NewName);
        var b = RealtimeProfileEditing.Choices([Custom("a", "A")], PresetName, NewName);
        Assert.True(a.SequenceEqual(b));
    }

    // ── 自動｜手動 and the figure for 手動, kept apart ──

    // What the page writes for each move: the switch alone, or a slider position on 手動 (switch
    // and figure together) — then tidied, as every write from the page is.
    private static RealtimeProfile Switch(RealtimeProfile mode, bool automatic)
    {
        var copy = mode.Clone();
        copy.SettleAutomatic = automatic;
        copy.DetectorAutomatic = automatic;
        return RealtimeProfileEditing.WithoutTemplateValues(copy);
    }

    private static RealtimeProfile Slide(RealtimeProfile mode, double seconds, int percent)
    {
        var copy = mode.Clone();
        copy.SettleAutomatic = false;
        copy.SettleSeconds = seconds;
        copy.DetectorAutomatic = false;
        copy.DetectorPercent = percent;
        return RealtimeProfileEditing.WithoutTemplateValues(copy);
    }

    private static (bool, double, bool, int) Halves(RealtimeProfile mode)
    {
        var filled = mode.Filled();
        return (filled.SettleAutomatic!.Value, filled.SettleSeconds!.Value, filled.DetectorAutomatic!.Value, filled.DetectorPercent!.Value);
    }

    [Theory]
    [InlineData(RealtimePresets.StandardId)]
    [InlineData(RealtimePresets.SubtitleId)]
    [InlineData(RealtimePresets.InterfaceId)]
    public void TheFigureForManual_SurvivesGoingToAutomaticAndBack(string basedOn)
    {
        var manual = Slide(Switch(Custom("a", "A", basedOn), automatic: false), 0.6, 92);
        Assert.Equal((false, 0.6, false, 92), Halves(manual));

        var automatic = Switch(manual, automatic: true);
        Assert.Equal((true, 0.6, true, 92), Halves(automatic));

        var again = Switch(automatic, automatic: false);
        Assert.Equal((false, 0.6, false, 92), Halves(again));
        Assert.Equal((false, 0.6, false, 92), Halves(Switch(Switch(again, true), false)));
    }

    /// <summary>
    /// Never moved: 手動 starts at the preset's figure where the preset has one, and at 0.15 seconds
    /// and 85% on 標準, which is 自動.
    /// </summary>
    [Theory]
    [InlineData(RealtimePresets.StandardId, 0.15, 85)]
    [InlineData(RealtimePresets.SubtitleId, 0.0, 85)]
    [InlineData(RealtimePresets.InterfaceId, 0.15, 68)]
    public void NeverMoved_ManualStartsAtThePresetsFigure(string basedOn, double seconds, int percent)
    {
        var manual = Switch(Custom("a", "A", basedOn), automatic: false);
        Assert.Equal((false, seconds, false, percent), Halves(manual));

        var resolved = OverTranslate.Services.Realtime.ResolvedProfile.Resolve(manual);
        Assert.Equal(percent / 100.0, resolved.DetectorFraction);
        Assert.Equal((int)Math.Round(seconds / 0.15), resolved.SettlePolls);
    }

    [Fact]
    public void TheFigureRemembered_IsNotRunWhileOnAutomatic()
    {
        var automatic = Switch(Slide(Custom("a", "A", RealtimePresets.SubtitleId), 0.9, 70), automatic: true);
        var resolved = OverTranslate.Services.Realtime.ResolvedProfile.Resolve(automatic);

        Assert.Null(resolved.SettlePolls);
        Assert.Null(resolved.DetectorFraction);
        Assert.Equal(OverTranslate.Services.Realtime.RealtimeReadTiming.Auto, resolved.Timing);
    }

    [Fact]
    public void BothHalves_RoundTripThroughTheFile()
    {
        var settings = new AppSettings();
        var mode = Switch(Slide(Custom("a", "A"), 0.6, 92), automatic: true);
        settings.Realtime.Profiles = [mode];

        var json = OverTranslate.Services.SettingsService.Serialize(settings);
        var read = OverTranslate.Services.SettingsService.Parse(json).Realtime.Profiles.Single();

        Assert.Contains("\"SettleAutomatic\": true", json);
        Assert.Contains("\"SettleSeconds\": 0.6", json);
        Assert.Equal((true, 0.6, true, 92), Halves(read));
        Assert.Equal((false, 0.6, false, 92), Halves(Switch(read, automatic: false)));
    }

    /// <summary>
    /// A file written while -1 meant 自動 reads as 自動 with no figure of the user's own; a figure
    /// alone, as such a file kept 手動, is still 手動.
    /// </summary>
    [Theory]
    [InlineData(RealtimePresets.SubtitleId, 0.0, 85)]
    [InlineData(RealtimePresets.InterfaceId, 0.15, 68)]
    [InlineData(RealtimePresets.StandardId, 0.15, 85)]
    public void TheOldMinusOne_IsAutomaticWithNoFigureSet(string basedOn, double seconds, int percent)
    {
        var old = OverTranslate.Services.SettingsService.Parse(
            "{\"Realtime\":{\"Profiles\":[{\"Id\":\"a\",\"Name\":\"A\",\"BasedOn\":\"" + basedOn +
            "\",\"SettleSeconds\":-1,\"DetectorPercent\":-1}]}}").Realtime.Profiles.Single();

        Assert.Equal((true, seconds, true, percent), Halves(old));
        Assert.Equal((true, seconds, true, percent), Halves(RealtimeProfileEditing.WithoutTemplateValues(old)));
        Assert.Equal((false, seconds, false, percent), Halves(Switch(old, automatic: false)));

        var figureAlone = Custom("b", "B", basedOn);
        figureAlone.SettleSeconds = 0.6;
        figureAlone.DetectorPercent = 92;
        Assert.Equal((false, 0.6, false, 92), Halves(figureAlone));
        Assert.Equal((false, 0.6, false, 92), Halves(RealtimeProfileEditing.WithoutTemplateValues(figureAlone)));
    }

    /// <summary>
    /// Putting the figure back to the preset's never turns a figure alone — an older file's 手動 —
    /// into following a preset that is 自動.
    /// </summary>
    [Fact]
    public void AFigureAloneAtThePresetsValue_StaysManual()
    {
        var old = Custom("a", "A");
        old.SettleSeconds = 0.15;
        old.DetectorPercent = 85;

        var tidy = RealtimeProfileEditing.WithoutTemplateValues(old);

        Assert.Equal((false, 0.15, false, 85), Halves(tidy));
        Assert.Null(tidy.SettleSeconds);
        Assert.False(tidy.SettleAutomatic);
    }

    [Fact]
    public void Modified_IsTheSwitch_OrTheFigureOnManual()
    {
        var standard = Custom("a", "A");
        Assert.False(RealtimeProfileEditing.IsModified(standard, ProfileParam.SettleSeconds));

        // 標準 is 自動: 手動 differs even at the figure 手動 starts on.
        var manual = Switch(standard, automatic: false);
        Assert.True(RealtimeProfileEditing.IsModified(manual, ProfileParam.SettleSeconds));
        Assert.True(RealtimeProfileEditing.IsModified(manual, ProfileParam.DetectorPercent));

        // A figure remembered under 自動 is not a change.
        var remembered = Switch(Slide(standard, 0.6, 92), automatic: true);
        Assert.False(RealtimeProfileEditing.IsModified(remembered, ProfileParam.SettleSeconds));
        Assert.False(RealtimeProfileEditing.IsModified(remembered, ProfileParam.DetectorPercent));
        Assert.Equal(0, RealtimeProfileEditing.ModifiedCount(remembered));
        Assert.True(RealtimeProfileEditing.IsSet(remembered, ProfileParam.SettleSeconds));

        // 字幕・漫畫 is 手動 at 不等待 and 85%: the same figures are no change, others are, and 自動 is.
        var subtitle = Custom("b", "B", RealtimePresets.SubtitleId);
        Assert.False(RealtimeProfileEditing.IsModified(Slide(subtitle, 0, 85), ProfileParam.SettleSeconds));
        Assert.False(RealtimeProfileEditing.IsModified(Slide(subtitle, 0, 85), ProfileParam.DetectorPercent));
        Assert.Equal(0, RealtimeProfileEditing.ModifiedCount(Slide(subtitle, 0, 85)));
        Assert.True(RealtimeProfileEditing.IsModified(Slide(subtitle, 0.3, 85), ProfileParam.SettleSeconds));
        Assert.False(RealtimeProfileEditing.IsModified(Slide(subtitle, 0.3, 85), ProfileParam.DetectorPercent));
        Assert.Equal(2, RealtimeProfileEditing.ModifiedCount(Switch(subtitle, automatic: true)));
        Assert.Equal(2, RealtimeProfileEditing.ModifiedCount(Switch(Slide(subtitle, 0.3, 90), automatic: true)));
    }

    [Fact]
    public void PuttingBack_ClearsBothHalves()
    {
        var mode = Switch(Slide(Custom("a", "A", RealtimePresets.SubtitleId), 0.6, 92), automatic: true);

        var one = mode.Clone();
        RealtimeProfileEditing.Clear(one, ProfileParam.SettleSeconds);
        Assert.Null(one.SettleAutomatic);
        Assert.Null(one.SettleSeconds);
        Assert.Equal(true, one.DetectorAutomatic);

        var all = RealtimeProfileEditing.ResetAll(mode);
        Assert.Equal((false, 0.0, false, 85), Halves(all));
        Assert.False(RealtimeProfileEditing.IsSet(all, ProfileParam.SettleSeconds));
        Assert.False(RealtimeProfileEditing.IsSet(all, ProfileParam.DetectorPercent));
    }

    [Theory]
    [InlineData(0.15, "0.15")]
    [InlineData(0.3, "0.3")]
    [InlineData(1.5, "1.5")]
    [InlineData(0.30000000004, "0.3")]
    public void Seconds_AreWrittenWithoutTrailingZeros(double seconds, string expected)
    {
        Assert.Equal(expected, RealtimeProfileEditing.Seconds(seconds));
    }

    [Fact]
    public void TheTimingValuesAndTheFilterApplyToVerticalText_TheRestDoNot()
    {
        var both = RealtimeProfileEditing.All.Where(param => !RealtimeProfileEditing.IsHorizontalOnly(param));
        Assert.Equal([ProfileParam.FilterNumbersAndAbbreviations, ProfileParam.SettleSeconds, ProfileParam.ClearAfterEmptyReads], both);
    }
}
