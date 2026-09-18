using System.Drawing;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The realtime loop recognises whenever this reports a change, so these cover the two ways that
/// costs something: calling noise a change (recognition runs continuously for nothing) and calling a
/// change noise (the subtitle on screen never updates).
/// </summary>
public class FrameFingerprintTests
{
    [Fact]
    public void IdenticalContentDoesNotDiffer()
    {
        Assert.False(Cells(100).Differs(Cells(100)));
    }

    [Fact]
    public void DriftBelowTheToleranceIsNotAChange()
    {
        // Every cell moved, but only slightly — compression noise and antialiasing look like this,
        // and treating it as a change is what made recognition run at an 85% duty cycle.
        Assert.False(Cells(100).Differs(Cells(108)));
    }

    [Fact]
    public void DriftAboveTheToleranceEverywhereIsAChange()
    {
        // A real change of content: the lighting shifted, or the text was replaced.
        Assert.True(Cells(100).Differs(Cells(140)));
    }

    [Fact]
    public void OneCellChangingIsNotEnough()
    {
        // A glint, a caret, a single antialiased edge. Not worth a recognition pass.
        var before = new byte[100];
        Array.Fill(before, (byte)100);
        var after = (byte[])before.Clone();
        after[42] = 255;

        Assert.False(new FrameFingerprint(after).Differs(new FrameFingerprint(before)));
    }

    [Fact]
    public void EnoughCellsChangingIsAChange()
    {
        var before = new byte[100];
        Array.Fill(before, (byte)100);
        var after = (byte[])before.Clone();
        for (int i = 0; i < 10; i++) after[i] = 255;

        Assert.True(new FrameFingerprint(after).Differs(new FrameFingerprint(before)));
    }

    [Fact]
    public void ASmallShareOfTheWholeAreaIsStillAChangeWhereItLands()
    {
        // Five cells of a hundred is 5% of the area and under the bar, which is the right answer for
        // a strip of text — but the same five cells are a third of the two grid rows they sit in, and
        // over a whole watched region that is what a line of text looks like: small against the box
        // the user drew, unmistakable against the rows it falls in.
        var before = new byte[100];
        Array.Fill(before, (byte)100);
        var after = (byte[])before.Clone();
        for (int i = 0; i < 5; i++) after[i] = 255;

        Assert.False(new FrameFingerprint(after).Differs(new FrameFingerprint(before)));
        Assert.True(new FrameFingerprint(after).DiffersLocally(new FrameFingerprint(before)));
    }

    [Fact]
    public void OneCellChangingIsNotEnoughLocallyEither()
    {
        // The window is two rows rather than one precisely so a lone flickering cell cannot carry
        // it: a caret blinking in a dialogue box must not read as the next line arriving.
        var before = new byte[100];
        Array.Fill(before, (byte)100);
        var after = (byte[])before.Clone();
        after[42] = 255;

        Assert.False(new FrameFingerprint(after).DiffersLocally(new FrameFingerprint(before)));
    }

    [Fact]
    public void AFrameIsOnlyIdenticalWhenNothingMovedAnywhere()
    {
        // What the idle scan asks. A change under every bar above is still a change, and the only
        // picture that cannot be hiding one is the picture that did not move at all.
        var before = new byte[100];
        Array.Fill(before, (byte)100);
        var after = (byte[])before.Clone();
        after[42] = 255;

        Assert.True(new FrameFingerprint(before).IsIdenticalTo(new FrameFingerprint((byte[])before.Clone())));
        Assert.False(new FrameFingerprint(after).IsIdenticalTo(new FrameFingerprint(before)));
        Assert.False(new FrameFingerprint(before).IsIdenticalTo(null));
    }

    [Fact]
    public void NothingToCompareAgainstCountsAsChanged()
    {
        Assert.True(Cells(100).Differs(null));
        Assert.True(new FrameFingerprint(new byte[100]).Differs(new FrameFingerprint(new byte[50])));
    }

    [Fact]
    public void ChangeInsideAWatchedBandIsSeen()
    {
        List<Rectangle> bands = [new Rectangle(0, 16, 64, 12)];
        using var before = CreateFrame();
        using var after = CreateFrame();
        Fill(after, new Rectangle(10, 18, 40, 8), Color.Black);

        Assert.True(
            FrameFingerprint.Capture(after, bands).Differs(FrameFingerprint.Capture(before, bands)));
    }

    [Fact]
    public void ChangeOutsideEveryWatchedBandIsIgnored()
    {
        // The whole reason bands exist: a video playing around a subtitle must not read as the
        // subtitle having changed.
        List<Rectangle> bands = [new Rectangle(0, 16, 64, 12)];
        using var before = CreateFrame();
        using var after = CreateFrame();
        Fill(after, new Rectangle(0, 0, 64, 10), Color.Black);

        Assert.False(
            FrameFingerprint.Capture(after, bands).Differs(FrameFingerprint.Capture(before, bands)));

        // …and the same change is still plain to a whole-region view, so nothing is lost — it is
        // only the band view that ignores it.
        Assert.True(
            FrameFingerprint.Capture(after, null).Differs(FrameFingerprint.Capture(before, null)));
    }

    [Fact]
    public void BandsOutsideTheFrameAreClippedRatherThanThrowing()
    {
        // Bands are padded outwards from the recognised text, so the ones on a region's edge
        // legitimately hang off it.
        using var frame = CreateFrame();
        List<Rectangle> bands = [new Rectangle(-20, -20, 40, 40), new Rectangle(60, 28, 40, 40)];

        Assert.False(
            FrameFingerprint.Capture(frame, bands).Differs(FrameFingerprint.Capture(frame, bands)));
    }

    [Fact]
    public void ADrawnCopyStillLooksRightWhileNothingMoves()
    {
        // What the repaired background asks before repainting: the patch on screen was interpolated
        // from these pixels, and while they hold still it is still correct.
        using var frame = CreateFrame();

        Assert.True(
            FrameFingerprint.Capture(frame, null).StillLooksLike(FrameFingerprint.Capture(frame, null)));
    }

    [Fact]
    public void ADriftTooSmallToRecogniseIsStillTooBigToKeepPainted()
    {
        // The case the two questions answer differently, and the reason StillLooksLike exists. A
        // scene brightening by a few levels behind an unchanged line reads no new words — Differs is
        // tuned to say so — but a patch interpolated from the old shade now sits in the new one.
        Assert.False(Cells(100).StillLooksLike(Cells(106)));
        Assert.False(Cells(100).Differs(Cells(106)));
    }

    [Fact]
    public void NoiseBelowFourLevelsIsNotWorthRepaintingFor()
    {
        // Otherwise a still picture would repaint every tick on dithering alone, which is the cost
        // this check exists to avoid.
        Assert.True(Cells(100).StillLooksLike(Cells(103)));
    }

    [Fact]
    public void APatchSetWithNothingToCompareAgainstIsRepainted()
    {
        // A fresh arrangement of patches has never been painted, so it cannot be left alone.
        Assert.False(Cells(100).StillLooksLike(null));
        Assert.False(new FrameFingerprint(new byte[100]).StillLooksLike(new FrameFingerprint(new byte[50])));
    }

    private static FrameFingerprint Cells(byte value)
    {
        var cells = new byte[100];
        Array.Fill(cells, value);
        return new FrameFingerprint(cells);
    }

    // Deliberately not a flat fill: a uniform bitmap would summarise the same under almost any
    // sampling bug, so the fixture carries a pattern that varies in both directions.
    private static Bitmap CreateFrame(int width = 64, int height = 32)
    {
        var bitmap = new Bitmap(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(255, x * 3 % 256, y * 7 % 256, (x + y) % 256));

        return bitmap;
    }

    private static void Fill(Bitmap bitmap, Rectangle area, Color color)
    {
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
                bitmap.SetPixel(x, y, color);
    }
}
