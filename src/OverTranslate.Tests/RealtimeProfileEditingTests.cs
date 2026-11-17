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
        mode.LineSpacing = 4;
        mode.IgnoreSmallPercent = 40;

        Assert.Equal(2, RealtimeProfileEditing.ModifiedCount(mode, RealtimeProfileEditing.Advanced));
        Assert.Equal(3, RealtimeProfileEditing.ModifiedCount(mode, RealtimeProfileEditing.All));
    }

    [Fact]
    public void ConfidenceIsNotOnThePage()
    {
        Assert.DoesNotContain(ProfileParam.ConfidenceLevel, RealtimeProfileEditing.Advanced);
        Assert.DoesNotContain(ProfileParam.ConfidenceLevel, RealtimeProfileEditing.Shown);
        Assert.Equal(RealtimeProfileEditing.All.Length - 1, RealtimeProfileEditing.Shown.Length);
    }

    [Fact]
    public void TheModifiedCount_CountsTheValuesThePageShows_ThatReallyDiffer()
    {
        var mode = Custom("a", "Mine", RealtimePresets.SubtitleId);
        mode.MergeLevel = RealtimeMergeLevels.Passage;      // the preset's own: not a change
        mode.MergeUnpunctuated = false;                     // no effect at 整段, but the mode's own
        mode.SettleSeconds = 0.6;
        mode.ConfidenceLevel = 5;                           // not on the page

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
    public void UnpunctuatedMerging_IsOnlyInEffectOnTheAutomaticSteps(int level, bool inEffect)
    {
        var mode = Custom("a", "A");
        mode.MergeLevel = level;
        var filled = mode.Filled();

        Assert.Equal(inEffect, RealtimeProfileEditing.InEffect(filled, ProfileParam.MergeUnpunctuated));
        Assert.Equal(inEffect, RealtimeMergeLevels.MergeUnpunctuatedApplies(level));
        Assert.All(RealtimeProfileEditing.All.Where(param => param != ProfileParam.MergeUnpunctuated),
            param => Assert.True(RealtimeProfileEditing.InEffect(filled, param)));
    }

    [Fact]
    public void ResetAll_FollowsThePresetEverywhere_AndKeepsNameAndTemplate()
    {
        var mode = Custom("a", "Mine", RealtimePresets.InterfaceId);
        mode.MergeLevel = RealtimeMergeLevels.AutomaticMiddle;
        mode.SettleSeconds = 1.2;
        mode.ConfidenceLevel = 5;

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
        mode.ClearAfterEmptyReads = 4;

        RealtimeProfileEditing.Clear(mode, ProfileParam.MergeLevel);

        Assert.Null(mode.MergeLevel);
        Assert.Equal(4, mode.ClearAfterEmptyReads);
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

    // ── What 自動 stands for ──

    [Theory]
    [InlineData(RealtimeMergeLevels.AutomaticMiddle, 0.15)]
    [InlineData(RealtimeMergeLevels.Passage, 0.0)]
    [InlineData(RealtimeMergeLevels.Max, 0.0)]
    [InlineData(RealtimeMergeLevels.Items, 0.15)]
    [InlineData(RealtimeMergeLevels.Min, 0.15)]
    public void AutomaticSettle_RestsOnTheResolvedWait(int level, double expected)
    {
        var mode = Custom("a", "A");
        mode.MergeLevel = level;
        mode.SettleSeconds = 0.9;   // what the mode holds by hand does not move where 自動 rests
        Assert.Equal(expected, RealtimeProfileEditing.AutomaticSettleSeconds(mode.Filled()));
    }

    [Theory]
    [InlineData(RealtimeMergeLevels.AutomaticMiddle, 85)]
    [InlineData(RealtimeMergeLevels.Passage, 85)]
    [InlineData(RealtimeMergeLevels.Max, 85)]
    [InlineData(RealtimeMergeLevels.Items, 68)]
    [InlineData(RealtimeMergeLevels.Min, 68)]
    public void AutomaticDetector_RestsOnTheMeasuredSize(int level, int expected)
    {
        var mode = Custom("a", "A");
        mode.MergeLevel = level;
        Assert.Equal(expected, RealtimeProfileEditing.AutomaticDetectorPercent(mode.Filled()));
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
    public void TheTimingValuesApplyToVerticalText_TheRestDoNot()
    {
        var both = RealtimeProfileEditing.All.Where(param => !RealtimeProfileEditing.IsHorizontalOnly(param));
        Assert.Equal([ProfileParam.SettleSeconds, ProfileParam.ClearAfterEmptyReads], both);
    }
}
