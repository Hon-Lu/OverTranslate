using System.Reflection;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The grouping thresholds come from the capture mode the user chose, and from nothing else.
/// </summary>
/// <remarks>
/// This is the guard against the mode quietly becoming a second source language. The layout-metric
/// split was spent removing one input that steered grouping without saying so; a mode is allowed to
/// steer it, but only through this one table, and only as far as the two fields on the profile.
/// </remarks>
public class GroupingProfileContractTests
{
    [Fact]
    public void EachCaptureMode_MapsToItsOwnProfile()
    {
        Assert.Same(GroupingProfile.Interface, GroupingProfile.For(CaptureLayoutMode.Interface));
        Assert.Same(GroupingProfile.General, GroupingProfile.For(CaptureLayoutMode.General));

        // Not one profile under two names. The two hold the same figures until the steps that
        // relax them land, so this is about identity rather than value: what must hold throughout
        // is that each mode owns its own instance, so that moving one never moves the other.
        Assert.NotSame(
            GroupingProfile.For(CaptureLayoutMode.Interface),
            GroupingProfile.For(CaptureLayoutMode.General));
    }

    /// <summary>
    /// The live-screen profile is reachable from no capture mode.
    /// </summary>
    /// <remarks>
    /// Realtime has no toolbar and no mode to honour. Sharing the screenshot side's profile would
    /// hold for exactly as long as neither number moved, and then tie two paths together that were
    /// deliberately separated — silently, in whichever step next touched a threshold.
    /// </remarks>
    [Fact]
    public void TheRealtimeProfile_IsNotWhatAnyCaptureModeSelects()
    {
        foreach (var mode in Enum.GetValues<CaptureLayoutMode>())
            Assert.NotSame(GroupingProfile.Realtime, GroupingProfile.For(mode));
    }

    /// <summary>
    /// An unset <see cref="CaptureLayoutMode"/> is 一般, which means the enum's first member is.
    /// </summary>
    /// <remarks>
    /// Nothing in the app relies on this today — the settings property names its default, and the
    /// toolbar reads the settings — but the member order is what a field, a struct, or a
    /// <c>default</c> in a signature added later would land on, and landing on 介面 would be the v1
    /// behaviour coming back through a door nobody was watching. The order is the claim; this is
    /// what makes it one.
    /// </remarks>
    [Fact]
    public void AnUnsetCaptureMode_IsTheDefaultOne()
    {
        Assert.Equal(CaptureLayoutMode.General, default(CaptureLayoutMode));
        Assert.Same(GroupingProfile.General, GroupingProfile.For(default));
    }

    /// <summary>
    /// Every capture mode has an entry, including one added later.
    /// </summary>
    [Fact]
    public void EveryCaptureMode_HasAProfile()
    {
        foreach (var mode in Enum.GetValues<CaptureLayoutMode>())
            Assert.NotNull(GroupingProfile.For(mode));
    }

    /// <summary>
    /// The vertical profile is reachable from no capture mode either.
    /// </summary>
    /// <remarks>
    /// Vertical text is turned 270° before it is read, so its grouping pass is comparing column
    /// against column — not the geometry any capture mode's thresholds were measured on. Pointing
    /// the relaxed profile at it was measured and it joined speech balloons rather than the lines
    /// inside them, which is what waiving a length test does when the "lines" are whole balloons.
    /// </remarks>
    [Fact]
    public void TheVerticalProfile_IsNotWhatAnyCaptureModeSelects()
    {
        foreach (var mode in Enum.GetValues<CaptureLayoutMode>())
            Assert.NotSame(GroupingProfile.Vertical, GroupingProfile.For(mode));
    }

    /// <summary>
    /// The vertical profile holds its own figures, written out rather than referred to.
    /// </summary>
    /// <remarks>
    /// The same guard <see cref="TheRealtimeProfile_HoldsItsOwnNumber_EvenWhenItMatchesTheScreenshotSide"/>
    /// is: tightening the interface mode is a step of its own, and borrowing that profile here would
    /// carry vertical text along with it silently, on figures never measured on vertical material.
    /// The literals are not a claim that these must never change — they are there so that changing
    /// them needs somebody to come and answer whether vertical changes too.
    /// </remarks>
    [Fact]
    public void TheVerticalProfile_HoldsItsOwnNumbers_EvenWhenTheyMatchTheInterfaceMode()
    {
        Assert.Equal(0.88, GroupingProfile.Vertical.TightlySetMinTextSizeRatio);
        Assert.False(GroupingProfile.Vertical.WaiveLengthTestWhenSetSolid);
        Assert.Equal(1.20, GroupingProfile.Vertical.SolidLineAdvanceWhenWrapped);
        Assert.NotSame(GroupingProfile.Interface, GroupingProfile.Vertical);
    }

    /// <summary>
    /// The profile carries three thresholds, and a fourth one arriving has to be a decision rather
    /// than a habit.
    /// </summary>
    /// <remarks>
    /// <para>Not a type-system lock — a sealed constructor would only produce a test-only way
    /// around it. This is here so that adding a field fails a test whose name says what the
    /// reviewer has to be shown: a measured case from the image corpus, and a design change, per
    /// profile field.</para>
    ///
    /// <para>The third field was added that way and it is worth recording what the case was, since
    /// this test is where the next person will look. The relaxed leading was first built as a
    /// constant applying to every mode, and measured that way it moved the interface mode almost as
    /// much as the general one: 15 of the 16 wrong joins it buys happened under
    /// <see cref="GroupingProfile.Interface"/> as well. Keeping the figure on the profile is what
    /// leaves the unrelaxed control unrelaxed, which is what the test below is for.</para>
    ///
    /// <para>The rest of that argument no longer holds, and it is recorded here rather than quietly
    /// dropped. The 16 were accepted on the grounds that a user meeting them can switch modes; the
    /// shipped app has no such switch, because <see cref="OverTranslate.Services.Ocr.CaptureLayoutPolicy"/>
    /// collapses every capture to <see cref="GroupingProfile.General"/> and the interface mode is
    /// archived. A profile with no caller cannot be a refuge, so this test now guards a contract
    /// about the shape of the type, not a live product choice. Read that type before treating any
    /// interface-mode measurement as something a user meets.</para>
    /// </remarks>
    /// <para>The fourth arrived the same way: <see cref="GroupingProfile.RefuseSpeakerLineStarts"/>,
    /// measured on the chat-room captures, where the live panel path joined 11 pairs of separate
    /// messages across five hand-marked images. It is content evidence rather than a threshold, and
    /// only the live panel profile takes it.</para>
    [Fact]
    public void TheProfile_CarriesFourFields_AndAFifthIsADesignChange()
    {
        var fields = typeof(GroupingProfile)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        Assert.Equal(
            [nameof(GroupingProfile.TightlySetMinTextSizeRatio),
             nameof(GroupingProfile.WaiveLengthTestWhenSetSolid),
             nameof(GroupingProfile.SolidLineAdvanceWhenWrapped),
             nameof(GroupingProfile.RefuseSpeakerLineStarts)],
            fields);
    }

    [Fact]
    public void OnlyTheRealtimeProfile_RefusesSpeakerLineStarts()
    {
        Assert.True(GroupingProfile.Realtime.RefuseSpeakerLineStarts);
        Assert.False(GroupingProfile.General.RefuseSpeakerLineStarts);
        Assert.False(GroupingProfile.Interface.RefuseSpeakerLineStarts);
        Assert.False(GroupingProfile.Vertical.RefuseSpeakerLineStarts);
    }

    /// <summary>
    /// One mode relaxes the set-solid leading, and it is the one the user can switch away from.
    /// </summary>
    /// <remarks>
    /// The relaxation is the only threshold on this record that changes which pairs are set solid
    /// rather than what is asked of a pair already set solid, and it is paid for in wrong joins —
    /// news headlines and timeline entries strung together. What makes that price acceptable is
    /// that the interface mode is an escape from it, so the interface mode not taking the
    /// relaxation is the term of the trade, not an implementation detail.
    /// </remarks>
    [Fact]
    public void OnlyTheGeneralMode_RelaxesTheSetSolidLeading()
    {
        Assert.Equal(1.45, GroupingProfile.General.SolidLineAdvanceWhenWrapped);

        Assert.Equal(
            OcrTextBlockGrouper.SolidLineAdvance,
            GroupingProfile.Interface.SolidLineAdvanceWhenWrapped);
        Assert.Equal(
            OcrTextBlockGrouper.SolidLineAdvance,
            GroupingProfile.Realtime.SolidLineAdvanceWhenWrapped);
        Assert.Equal(
            OcrTextBlockGrouper.SolidLineAdvance,
            GroupingProfile.Vertical.SolidLineAdvanceWhenWrapped);
    }

    /// <summary>
    /// The live-screen profile holds its own figure, written out rather than referred to.
    /// </summary>
    /// <remarks>
    /// The three profiles quote one constant today, so lowering that constant — which the design
    /// allows as a shared improvement to the screenshot side — would move the live-screen path with
    /// it, silently, while every identity assertion above stayed green. The literal here is not a
    /// claim that this number must never change: it is there so that changing it needs someone to
    /// come and answer whether realtime changes too.
    /// </remarks>
    [Fact]
    public void TheRealtimeProfile_HoldsItsOwnNumber_EvenWhenItMatchesTheScreenshotSide()
    {
        Assert.Equal(0.88, GroupingProfile.Realtime.TightlySetMinTextSizeRatio);
        Assert.False(GroupingProfile.Realtime.WaiveLengthTestWhenSetSolid);
        Assert.Equal(1.20, GroupingProfile.Realtime.SolidLineAdvanceWhenWrapped);
    }

    /// <summary>
    /// The standard profile is today's behaviour, stated as a number rather than as a promise.
    /// </summary>
    [Fact]
    public void TheStandardProfile_HoldsTheThresholdsInUseBeforeModesExisted()
    {
        Assert.Equal(
            OcrTextBlockGrouper.MinTextSizeRatio,
            GroupingProfile.Interface.TightlySetMinTextSizeRatio);
        Assert.False(GroupingProfile.Interface.WaiveLengthTestWhenSetSolid);
        Assert.Equal(
            OcrTextBlockGrouper.SolidLineAdvance,
            GroupingProfile.Interface.SolidLineAdvanceWhenWrapped);
    }
}
