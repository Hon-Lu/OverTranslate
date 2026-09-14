using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeNaturalBackgroundTests
{
    [Fact]
    public void CreatePatch_ReplacesSourceRectButKeepsSurroundingPixels()
    {
        using var frame = new Bitmap(48, 24);
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
            frame.SetPixel(x, y, Color.FromArgb(255, 20 + y * 3, 30 + y * 2, 45 + y));

        var source = new System.Windows.Rect(14, 8, 20, 7);
        using (var g = Graphics.FromImage(frame))
            g.FillRectangle(Brushes.White, 14, 8, 20, 7);

        var patchBounds = new Rectangle(6, 3, 36, 17);
        using var patch = RealtimeNaturalBackground.CreatePatch(frame, patchBounds, [source]);

        Assert.NotNull(patch);
        Assert.Equal(frame.GetPixel(7, 4), patch!.GetPixel(1, 1));

        // The centre was solid white in the source. Natural repair must replace it with local
        // background rather than simply copying the source screenshot into the overlay.
        var repaired = patch.GetPixel(18, 9);
        Assert.True(repaired.R < 180);
        Assert.True(repaired.G < 180);
        Assert.True(repaired.B < 180);
    }

    [Fact]
    public void CreatePatch_CleansDetachedMarksAboveTightOcrBox()
    {
        using var frame = new Bitmap(100, 50);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(255, 18, 78, 62));
            // Main glyph body is inside the OCR rectangle. The two small marks imitate Japanese
            // dakuten that OCR often leaves outside a tight line box.
            g.FillRectangle(Brushes.White, 25, 20, 50, 14);
            g.FillRectangle(Brushes.White, 34, 13, 3, 5);
            g.FillRectangle(Brushes.White, 40, 13, 3, 5);
        }

        var source = new System.Windows.Rect(25, 20, 50, 14);
        using var patch = RealtimeNaturalBackground.CreatePatch(
            frame, new Rectangle(10, 5, 80, 40), [source]);

        Assert.NotNull(patch);
        // Global (35,14) becomes local (25,9). It was white before repair and should now be close
        // to the green background rather than surviving as a bright fragment.
        var repairedMark = patch!.GetPixel(25, 9);
        Assert.True(repairedMark.R < 100);
        Assert.True(repairedMark.G < 140);
        Assert.True(repairedMark.B < 120);
    }

    /// <summary>
    /// A Latin line arrives as a box roughly three times the height of the text in it, with the
    /// glyph height carried separately, so the repair must erase the glyphs and leave the rest of
    /// the box as the picture it is.
    /// </summary>
    [Fact]
    public void CreatePatch_ErasesTheGlyphsRatherThanTheWholeLooseLineBox()
    {
        using var frame = new Bitmap(160, 120);
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                frame.SetPixel(x, y, Color.FromArgb(255, 10 + y, 200 - y, 60 + y % 7 * 6));

        using (var g = Graphics.FromImage(frame))
        {
            g.FillRectangle(Brushes.White, 40, 50, 80, 20);
            // The tail of a g, which the glyph height does not account for.
            g.FillRectangle(Brushes.White, 55, 70, 5, 7);
        }

        var looseBox = new System.Windows.Rect(40, 30, 80, 60);
        var patchBounds = new Rectangle(20, 10, 120, 100);

        using var patch = RealtimeNaturalBackground.CreatePatch(
            frame, patchBounds, [RealtimeNaturalBackground.GlyphBounds(looseBox, 20)]);

        Assert.NotNull(patch);

        // The glyphs are gone, tail included — left behind, it is read as the picture below the
        // line and drawn the height of the fill as a bright column.
        var repaired = patch!.GetPixel(60 - 20, 60 - 10);
        Assert.True(repaired.R < 200);

        var repairedTail = patch.GetPixel(57 - 20, 74 - 10);
        Assert.True(repairedTail.R < 200);

        // The picture the box merely surrounded is still the picture, not part of the fill.
        Assert.Equal(frame.GetPixel(60, 35), patch.GetPixel(60 - 20, 35 - 10));
        Assert.Equal(frame.GetPixel(60, 85), patch.GetPixel(60 - 20, 85 - 10));
    }

    /// <summary>
    /// Two patches over the same line — one holding it whole, one cut across it — must repair it the
    /// same way, or the one painted second leaves its own version behind as a visible rectangle.
    /// </summary>
    [Fact]
    public void CreatePatch_RepairsALineTheSameWayWhicheverPatchItFallsIn()
    {
        using var frame = new Bitmap(200, 200);
        for (int y = 0; y < frame.Height; y++)
            for (int x = 0; x < frame.Width; x++)
                frame.SetPixel(x, y, Color.FromArgb(255, 30 + y, 90 + x % 5 * 9, 150 - y / 2));

        using (var g = Graphics.FromImage(frame))
            g.FillRectangle(Brushes.White, 50, 95, 100, 16);

        var line = new System.Windows.Rect(50, 95, 100, 16);

        // The second patch stops halfway down the line, which is what a neighbouring block's patch
        // does to it.
        using var whole = RealtimeNaturalBackground.CreatePatch(frame, new Rectangle(40, 60, 120, 90), [line]);
        using var cut = RealtimeNaturalBackground.CreatePatch(frame, new Rectangle(40, 103, 120, 60), [line]);

        Assert.NotNull(whole);
        Assert.NotNull(cut);

        // The rows they share, read out of each: same picture or the seam is visible.
        for (int y = 103; y < 150; y++)
            for (int x = 45; x < 155; x++)
                Assert.Equal(whole!.GetPixel(x - 40, y - 60), cut!.GetPixel(x - 40, y - 103));
    }

    /// <summary>
    /// Two stacked English subtitle lines, at the boxes they were measured at: each patch is a copy
    /// of the picture and reaches over its neighbour, so a patch that erased only its own line put
    /// half of the other one back.
    /// </summary>
    [Fact]
    public void EraseTargets_CoverEveryTranslatedBlockRatherThanOnePatchesOwn()
    {
        var upper = new TranslatedBlock(
            "like an explosive force", "像是一股爆炸力",
            new System.Windows.Rect(300, 765, 569, 70), RenderGlyphHeight: 37);
        var lower = new TranslatedBlock(
            "hurtling into the sky!", "衝向天空！",
            new System.Windows.Rect(310, 827, 542, 74), RenderGlyphHeight: 37);

        var targets = RealtimeNaturalBackground.EraseTargets([upper, lower]);

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, rect => rect.Top < 790 && rect.Bottom > 810);   // the upper line
        Assert.Contains(targets, rect => rect.Top < 852 && rect.Bottom > 872);   // the lower one
    }

    /// <summary>
    /// A block with no translation drawn over it is still on screen to be read, so a neighbouring
    /// patch must not rub out the part of it that happens to fall inside.
    /// </summary>
    [Fact]
    public void EraseTargets_LeaveBlocksNothingWasDrawnOver()
    {
        var translated = new TranslatedBlock(
            "like an explosive force", "像是一股爆炸力",
            new System.Windows.Rect(300, 765, 569, 70), RenderGlyphHeight: 37);
        var untranslated = new TranslatedBlock(
            "hurtling into the sky!", "  ",
            new System.Windows.Rect(310, 827, 542, 74), RenderGlyphHeight: 37);

        var targets = RealtimeNaturalBackground.EraseTargets([translated, untranslated]);

        Assert.Single(targets);
        // The one that is left is the translated line's, inside its own box.
        Assert.True(targets[0].Top >= 765 && targets[0].Bottom <= 835);
    }

    [Fact]
    public void SampleTextColor_PrefersContrastingSourceGlyphColour()
    {
        using var frame = new Bitmap(80, 40);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(255, 24, 28, 34));
            g.FillRectangle(Brushes.White, 20, 12, 40, 12);
        }

        var sampled = RealtimeNaturalBackground.SampleTextColor(
            frame,
            new System.Windows.Rect(20, 12, 40, 12),
            System.Windows.Media.Colors.Lime);

        Assert.True(sampled.R > 220);
        Assert.True(sampled.G > 220);
        Assert.True(sampled.B > 220);
    }

    [Fact]
    public void SampleTextColor_KeepsMajorityColourWhenAFewWordsAreHighlighted()
    {
        using var frame = new Bitmap(80, 40);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(255, 24, 28, 34));
            g.FillRectangle(Brushes.White, 20, 12, 30, 12);
            using var highlight = new SolidBrush(Color.FromArgb(255, 255, 220, 0));
            g.FillRectangle(highlight, 50, 12, 10, 12);
        }

        var sampled = RealtimeNaturalBackground.SampleTextColor(
            frame,
            new System.Windows.Rect(20, 12, 40, 12),
            System.Windows.Media.Colors.Lime);

        // Averaged, the yellow pulled blue down to about 190 and the line came back cream.
        Assert.True(sampled.R > 220);
        Assert.True(sampled.G > 220);
        Assert.True(sampled.B > 220);
    }
}
