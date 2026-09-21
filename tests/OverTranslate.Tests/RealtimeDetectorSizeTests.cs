using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

public class RealtimeDetectorSizeTests(ITestOutputHelper output)
{
    [Fact]
    public void ASubtitleStripIsReadNearFullScale()
    {
        // Halving was the old answer and it read a fifth of the control corpus wrongly — words cut
        // in half, leading words dropped. See RealtimeDetectorSize for the sweep. #81
        var (primary, _) = RealtimeDetectorSize.For(1226, 196, RealtimeBlockMode.Subtitle);

        Assert.Equal(1056, primary); // 1226 * 0.85, rounded up onto the detector's grid
    }

    [Fact]
    public void AnInterfacePanelIsReadAtTheLargerFraction()
    {
        // The text is a small part of a panel's height, so halving takes it below what the detector
        // finds. Measured on this shape, 0.5 read 3 of 9 boxes and 0.68 read 7.
        var (primary, _) = RealtimeDetectorSize.For(1380, 750, RealtimeBlockMode.Panel);

        Assert.Equal(960, primary); // 1380 * 0.68
    }

    /// <summary>
    /// A region of columns is read at the size the screenshot flow reads one, whichever mode the
    /// user put it in.
    /// </summary>
    /// <remarks>
    /// Columns of Japanese sit about a glyph apart, so a downscale that leaves a row of glyphs
    /// readable merges a column with the reading beside it. Measured against what the screenshot
    /// flow reads on the same pages, 0.68 loses 17 of 196 groups on 15 comic spreads and 25 of 192
    /// on the same spreads at 70%; the screenshot size loses 4 and 5.
    /// </remarks>
    [Theory]
    [InlineData(RealtimeBlockMode.Panel)]
    [InlineData(RealtimeBlockMode.Subtitle)]
    public void A_region_of_columns_is_read_at_the_screenshot_size(RealtimeBlockMode mode)
    {
        var (primary, fallbacks) = RealtimeDetectorSize.For(
            1828, 1300, mode, RealtimeTextOrientation.Vertical);

        Assert.Equal(1828, primary);
        Assert.NotEqual(RealtimeDetectorSize.For(1828, 1300, mode).Primary, primary);
        // Everything smaller was measured and is strictly worse, so there is no other size to try.
        Assert.Empty(fallbacks);
    }

    /// <summary>Bounded by what the screenshot flow itself would ask for.</summary>
    /// <remarks>
    /// One source in the corpus is 8598px on its longest side. Uncapped it costs 27.6s a pass and
    /// finds one group more than the cap does at 2.8s.
    /// </remarks>
    [Fact]
    public void An_oversized_region_of_columns_is_capped_where_the_screenshot_flow_caps()
    {
        var (primary, _) = RealtimeDetectorSize.For(
            8598, 6000, RealtimeBlockMode.Panel, RealtimeTextOrientation.Vertical);

        Assert.Equal(RealtimeDetectorSize.ColumnsAreReadAtTheScreenshotSize, primary);
    }

    /// <summary>Writing that runs across is what every fraction here was measured on, and is unmoved.</summary>
    [Fact]
    public void A_region_written_across_still_gets_its_fraction()
    {
        var (primary, fallbacks) = RealtimeDetectorSize.For(
            1226, 196, RealtimeBlockMode.Subtitle, RealtimeTextOrientation.Horizontal);

        Assert.Equal(RealtimeDetectorSize.For(1226, 196, RealtimeBlockMode.Subtitle).Primary, primary);
        Assert.NotEmpty(fallbacks);
    }

    [Theory]
    // The same rectangle, read both ways. These are the shapes the removed StripAspectRatio rule
    // classified — a strip at 6.4 and a panel at 2.1 — and the point of issue #35 is that the shape
    // no longer decides anything: the user does, and either answer is available for either shape.
    [InlineData(1432, 224)]
    [InlineData(1380, 657)]
    public void TheModeDecidesTheScaleAndTheShapeNoLongerDoes(int width, int height)
    {
        var native = Math.Max(width, height);

        var strip = RealtimeDetectorSize.For(width, height, RealtimeBlockMode.Subtitle).Primary;
        var panel = RealtimeDetectorSize.For(width, height, RealtimeBlockMode.Panel).Primary;

        // Stated as the gap between the two answers rather than as a bound on each: what this test
        // is about is that the same rectangle gets two different sizes depending on what the user
        // says is in it, and that stays true however the two fractions are later retuned.
        Assert.NotEqual(strip, panel);
        Assert.Equal(RoundToStride(native * RealtimeDetectorSize.StripFraction), strip);
        Assert.Equal(RoundToStride(native * RealtimeDetectorSize.PanelFraction), panel);
    }

    private static int RoundToStride(double size) => Math.Max(320, ((int)size + 31) / 32 * 32);

    [Fact]
    public void TheUsersDraggingCannotChangeTheScaleAnyMore()
    {
        // What the user actually varies: the same subtitle framed tightly and framed loosely. Under
        // the aspect-ratio guess these landed on opposite sides of 4.0 and got two different scales
        // from the same content — the complaint issue #35 opens with.
        var tight = RealtimeDetectorSize.For(1432, 180, RealtimeBlockMode.Subtitle);   // ratio 8.0
        var loose = RealtimeDetectorSize.For(1432, 480, RealtimeBlockMode.Subtitle);   // ratio 3.0

        Assert.Equal(tight.Primary, loose.Primary);
    }

    [Fact]
    public void BothFractionsStayInsideTheRangeTheSweepSupports()
    {
        // This used to read 0.37–0.78, the window PP-OCRv5_mobile_det read those subtitle frames in
        // before collapsing above 0.84. That detector is gone and its collapse went with it — see
        // the note in RealtimeDetectorSize, and SubtitleFramesTheDetectorCollapsedAreReadAtTheChosenSize
        // below, which reads the very frames it collapsed on and is the guard that actually holds.
        //
        // What replaces it is the floor the control sweep measured: below 0.6 accuracy falls away
        // (0.5 read 78% of the corpus correctly, 0.4 read 69%), and native is worse than 0.85, so
        // neither fraction should sit outside that band. #81
        Assert.InRange(RealtimeDetectorSize.StripFraction, 0.60, 0.95);
        Assert.InRange(RealtimeDetectorSize.PanelFraction, 0.60, 0.95);
    }

    [Fact]
    public void TheOtherModesFractionIsTriedFirstThenNativeSize()
    {
        // The two fail on opposite content, so the fraction the mode turned down is the best next
        // answer when the mode was not the whole story. Native goes last: the only size that can
        // read text too small to survive any downscale.
        var (_, fallbacks) = RealtimeDetectorSize.For(1415, 169, RealtimeBlockMode.Subtitle);

        Assert.Equal([992, 1415], fallbacks); // 1415 * 0.68, then native
    }

    [Fact]
    public void APanelFallsBackToWhateverTheStripFractionCurrentlyIs()
    {
        // The direction issue #84 was opened about. A panel's first fallback is not a number of its
        // own — it is StripFraction, so #81 moved it from 0.50 to 0.85 while measuring only
        // subtitles. Stated against the constant rather than against 1568 so that this keeps saying
        // what it means if the fraction moves again; what it pins is the coupling, which is the part
        // that changes panel behaviour without anyone editing anything panel-shaped.
        //
        // Swept over 24 panel screens for #84, nothing regressed — and nothing could have, because
        // the primary size read all 24, so no fallback ever ran. See NativeFraction's remarks: the
        // chain is unharmed but unmeasured, not confirmed good.
        var native = 1849;
        var (_, fallbacks) = RealtimeDetectorSize.For(native, 1041, RealtimeBlockMode.Panel);

        Assert.Equal([RoundToStride(native * RealtimeDetectorSize.StripFraction), native], fallbacks);
    }

    [Fact]
    public void ASizeThatWouldRepeatTheFirstAttemptIsNotTriedAgain()
    {
        var (primary, fallbacks) = RealtimeDetectorSize.For(1226, 196, RealtimeBlockMode.Subtitle);

        Assert.DoesNotContain(primary, fallbacks);
    }

    [Fact]
    public void ASmallRegionIsReadAtNativeSizeWithNothingToFallBackTo()
    {
        // 60px subtitles do not fit in a region this size, so any downscale could only take small
        // text further out of range. See DownscaleMinSide.
        var (primary, fallbacks) = RealtimeDetectorSize.For(400, 120, RealtimeBlockMode.Subtitle);

        Assert.Equal(400, primary);
        Assert.Empty(fallbacks);
    }

    [Fact]
    public void TheLongestSideDecidesTheSize()
    {
        // The size comes from the long side whichever way round the block is — a tall, narrow panel
        // is measured on its height.
        var (primary, _) = RealtimeDetectorSize.For(300, 1000, RealtimeBlockMode.Panel);
        Assert.Equal(704, primary); // 1000 * 0.68, rounded up onto the detector's grid
    }

    [ScreenshotTheory("subtitle-over-light-floor-1226x196.png", "subtitle-lost-entirely-1226x196.png")]
    // Two frames captured from a live session, both of which the realtime loop read as empty and
    // cleared the overlay for. The text in each is large, white, outlined and unmistakable; the
    // detector collapsed it into a single box spanning the whole strip. See RealtimeDetectorSize
    // for the sweep these came from.
    [InlineData("subtitle-over-light-floor-1226x196.png", "marina-san, are you okay?")]
    [InlineData("subtitle-lost-entirely-1226x196.png", "minato-san")]
    public async Task SubtitleFramesTheDetectorCollapsedAreReadAtTheChosenSize(string fixture, string expected)
    {
        using var engine = new OcrService();
        using var frame = ExternalScreenshot.Load(fixture);

        var (primary, _) = RealtimeDetectorSize.For(frame.Width, frame.Height, RealtimeBlockMode.Subtitle);
        var blocks = await engine.TryRecognizeAsync(frame, "EN", primary);

        var text = string.Join(" ", blocks?.Select(b => b.Text) ?? []).ToLowerInvariant();
        output.WriteLine($"{fixture} @ detect={primary} -> {text}");

        Assert.Contains(expected, text);
    }

    [Fact]
    public void ABlankRegionStopsPayingForTheNativeRetry()
    {
        // The expensive one goes; the other mode's fraction, which rescued 17 of the 23
        // blank-state rescues on record against native's 6, stays.
        var (_, fallbacks) = RealtimeDetectorSize.For(1699, 242, RealtimeBlockMode.Subtitle);
        var whileBlank = RealtimeDetectorSize.WhileNothingIsShown(fallbacks, 1699, 242);

        Assert.Contains(1699, fallbacks);
        Assert.DoesNotContain(1699, whileBlank);
        Assert.Equal(fallbacks.Count - 1, whileBlank.Count);
    }

    [Fact]
    public void ARegionTooSmallToDownscaleHasNothingToDrop()
    {
        // Below DownscaleMinSide there are no fallbacks at all, so there is nothing here to save
        // and nothing to lose either.
        var (_, fallbacks) = RealtimeDetectorSize.For(400, 120, RealtimeBlockMode.Subtitle);

        Assert.Empty(RealtimeDetectorSize.WhileNothingIsShown(fallbacks, 400, 120));
    }

    [ScreenshotTheory("subtitle-over-light-floor-1226x196.png", "subtitle-lost-entirely-1226x196.png")]
    [InlineData("subtitle-over-light-floor-1226x196.png", "okay")]
    [InlineData("subtitle-lost-entirely-1226x196.png", "minato-san")]
    public async Task TheSameFramesAlsoReadAtTheScreenshotSizeNow(string fixture, string expected)
    {
        // These two frames used to come back empty here, and the test that came before this one
        // pinned that failure on purpose — so that swapping the detector would trip it rather than
        // let the halving outlive its reason. PP-OCRv6_det_tiny tripped it: both frames now read
        // correctly at the size the screenshot flow uses, which closes the hole issue #22 recorded
        // in that flow (it has no fallback size to fall back to, so a subtitle framed there was
        // simply lost).
        //
        // Halving was still what a strip was read at when this was written, on the reasoning that
        // the new detector read 9 of those same 15 frames at 0.50 and 9 at native, for 89ms against
        // 186ms — same result, half the cost. That reasoning was unsound: all 15 were frames the
        // primary size had FAILED to read, and a fraction is never asked there about the frames it
        // reads WRONGLY rather than not at all. Issue #81 swept the control group instead and found
        // 0.50 reading a fifth of it wrong, so a strip is now read at 0.85. See RealtimeDetectorSize.
        //
        // What this test asserts is unaffected either way: it is about the screenshot size, which
        // has never downscaled and still does not.
        using var engine = new OcrService();
        using var frame = ExternalScreenshot.Load(fixture);

        var blocks = await engine.TryRecognizeAsync(frame, "EN", 2048);
        var text = string.Join(" ", blocks?.Select(b => b.Text) ?? []).ToLowerInvariant();
        output.WriteLine($"{fixture} @ detect=2048 -> \"{text}\"");

        Assert.Contains(expected, text);
    }
}
