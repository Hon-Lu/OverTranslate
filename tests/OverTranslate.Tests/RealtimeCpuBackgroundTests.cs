using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeCpuBackgroundTests
{
    [Fact]
    public void Repair_RemovesStrokesWithoutChangingSourceOrUntranslatedRegion()
    {
        // Odd width exercises 24-bit bitmap row padding and BGR channel order.
        using var frame = new Bitmap(213, 143);
        var background = Color.FromArgb(20, 40, 60);
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(background);
            graphics.FillRectangle(Brushes.White, 42, 35, 3, 18);
            graphics.FillRectangle(Brushes.White, 152, 95, 3, 18);
        }
        using var result = RealtimeCpuBackground.Repair(frame,
        [
            new TranslatedBlock("I", "字", new(38, 32, 14, 25)),
            new TranslatedBlock("I", " ", new(148, 92, 14, 25)),
        ]);
        Assert.Equal(frame.Size, result.Size);
        Assert.Equal(Color.White.ToArgb(), frame.GetPixel(43, 40).ToArgb());
        Assert.InRange(result.GetPixel(43, 40).R, 15, 30);
        Assert.InRange(result.GetPixel(43, 40).B, 55, 70);
        Assert.Equal(background.ToArgb(), result.GetPixel(212, 142).ToArgb());
        Assert.Equal(Color.White.ToArgb(), result.GetPixel(153, 100).ToArgb());
    }

    /// <summary>
    /// A burnt-in subtitle is a bright body inside a dark outline that fades into the picture, and
    /// the fade is what used to be left behind — as a dotted dark contour tracing the words, which
    /// reads as the erase having failed even though every stroke is gone.
    /// </summary>
    /// <remarks>
    /// Strokes rather than one block, and a Latin line's proportions — a recognition box around twice
    /// the height of its glyphs, with the glyph height carried separately — because both decide what
    /// the mask does. Which polarity is taken for the text is settled by whichever of the two hats
    /// answers louder over the whole box, so a fixture holding more outline than glyph is a fixture
    /// about dark text; and everything the mask decides is decided inside that box, so how far past a
    /// stroke it can follow a fade is how much room the box leaves around it.
    ///
    /// The background is flat, which makes anything left over unambiguous: the correct answer for
    /// every erased pixel is the background itself.
    /// </remarks>
    [Fact]
    public void Repair_ErasesTheFadeAroundAnOutlinedGlyphAndNotOnlyTheStroke()
    {
        var background = Color.FromArgb(60, 90, 120);
        const int left = 40, top = 40, stroke = 8, gap = 28, height = 40, strokes = 5;
        const int outline = 3, fade = 5, band = outline + fade;
        int[] columns = [.. Enumerable.Range(0, strokes).Select(index => left + index * (stroke + gap))];

        using var frame = new Bitmap(320, 160);
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(background);
            for (int step = fade; step >= 1; step--)
            {
                // Each ring is one step closer to the outline's black than the one outside it.
                double toward = (double)(fade - step + 1) / (fade + 1);
                using var brush = new SolidBrush(Color.FromArgb(
                    (byte)(background.R * (1 - toward)),
                    (byte)(background.G * (1 - toward)),
                    (byte)(background.B * (1 - toward))));
                int grow = outline + step;
                foreach (int x in columns)
                    graphics.FillRectangle(brush, x - grow, top - grow, stroke + grow * 2, height + grow * 2);
            }
            foreach (int x in columns)
            {
                graphics.FillRectangle(Brushes.Black,
                    x - outline, top - outline, stroke + outline * 2, height + outline * 2);
                graphics.FillRectangle(Brushes.White, x, top, stroke, height);
            }
        }

        using var result = RealtimeCpuBackground.Repair(frame,
        [
            new TranslatedBlock("III", "字",
                new(columns[0], top - height / 2, columns[^1] + stroke - columns[0], height * 2),
                RenderGlyphHeight: height),
        ]);

        // Above and below each stroke, which is where the leftover contour shows. Not diagonally out
        // from a corner: a rectangle has a right angle there and a letter does not, so the fade
        // reaches further out than any real glyph's would and no mask should be asked to cover it.
        foreach (int x in columns)
        {
            for (int y = top - band; y < top + height + band; y++)
            {
                var pixel = result.GetPixel(x + stroke / 2, y);
                int apart = Math.Abs(pixel.R - background.R)
                    + Math.Abs(pixel.G - background.G)
                    + Math.Abs(pixel.B - background.B);
                Assert.True(apart <= 24,
                    $"({x + stroke / 2},{y}) is {pixel} against a background of {background}");
            }
        }
    }

    /// <summary>
    /// The punctuation left on screen after everything around it was erased. A line of bright text
    /// on a dark scene votes itself dark — the closing behind the black hat fills the gaps between
    /// glyphs, so the dark response answers for the band of picture between the words and out-shouts
    /// the strokes — and the glyphs are then only picked up as the "outline" around that body,
    /// within reach of one of those filled gaps. A stroke standing on its own has no filled gap
    /// beside it, so nothing finds it: in Japanese that is the exclamation mark.
    /// </summary>
    /// <remarks>
    /// The lone stroke is what is asserted, not the group: the group was always erased, which is
    /// exactly why the leftover reads as a bug rather than as the repair not having run.
    /// </remarks>
    [Fact]
    public void Repair_ErasesALoneStrokeTheDarkResponseCannotSee()
    {
        // A dark scene with bright text on it, which is the shape that decides the polarity vote.
        // The gaps between the strokes are narrower than the kernel, so closing fills them and the
        // dark response answers for the whole band between the glyphs — louder, over the box, than
        // the bright response answers for the glyphs themselves. The box votes "dark text" although
        // every stroke in it is white, and the body mask is then the background between them.
        var background = Color.FromArgb(18, 18, 26);
        const int top = 40, height = 40, stroke = 3, gap = 14;
        int[] columns = [.. Enumerable.Range(0, 8).Select(index => 40 + index * (stroke + gap))];
        // Far enough from the group that closing fills nothing around it, so the dark response has
        // nothing to say here and the outline search has no seed to start from: a reach of five, a
        // tail of six, and sixty pixels of flat picture. This is the exclamation mark.
        const int alone = 260;

        using var frame = new Bitmap(360, 120);
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(background);
            foreach (int x in columns) graphics.FillRectangle(Brushes.White, x, top, stroke, height);
            graphics.FillRectangle(Brushes.White, alone, top, stroke, height);
        }

        using var result = RealtimeCpuBackground.Repair(frame,
            [new TranslatedBlock("!", "！", new(30, top - 4, 240, height + 8), RenderGlyphHeight: height)]);

        for (int y = top; y < top + height; y++)
        {
            var pixel = result.GetPixel(alone + 1, y);
            int apart = Math.Abs(pixel.R - background.R)
                + Math.Abs(pixel.G - background.G)
                + Math.Abs(pixel.B - background.B);
            Assert.True(apart <= 24, $"({alone + 1},{y}) is {pixel} against a background of {background}");
        }
    }

    [Fact]
    public void Repair_EmptyTranslationReturnsIndependentUnchangedFrame()
    {
        using var frame = new Bitmap(7, 5);
        frame.SetPixel(6, 4, Color.Red);
        using var result = RealtimeCpuBackground.Repair(frame, []);
        Assert.NotSame(frame, result);
        Assert.Equal(frame.GetPixel(6, 4), result.GetPixel(6, 4));
    }

    [Fact]
    public void Repair_HonorsCancellationBeforeBitmapConversion()
    {
        using var frame = new Bitmap(7, 5);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RealtimeCpuBackground.Repair(frame, [], cancellation.Token));
    }
}
