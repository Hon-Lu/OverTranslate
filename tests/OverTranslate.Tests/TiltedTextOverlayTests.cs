using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;
using Point = System.Windows.Point;

namespace OverTranslate.Tests;

/// <summary>
/// A group read off a tilted card is drawn along the card: turned or sheared as the card is, over
/// its own comment and not the next, erased inside its lines and nowhere else. Level text carries
/// nothing of it. See Ocr.TiltedText.
/// </summary>
/// <remarks>
/// The cards are TiltedLayoutTests' own, rebuilt from region-comic-en-3: one turned 9.4°, one in
/// perspective with its lines sloping 30° and its letters and margin upright.
/// </remarks>
public class TiltedTextOverlayTests
{
    [Fact]
    public void ATurnedCardIsDrawnRotatedAndACardInPerspectiveSheared()
    {
        var rotated = Assert.Single(
            OcrService.GroupRealtime(TurnedParagraph(), 1298, RealtimeBlockMode.Subtitle),
            group => group.Text.Contains("SINGLE MISTRESS"));
        var turned = Assert.IsType<TiltedText>(rotated.Tilt);
        Assert.False(turned.Sheared);
        Assert.InRange(turned.Degrees, -9.9, -8.9);
        Assert.Equal(5, turned.LineQuads.Count);
        // As long as the card's longest line, which is what the translation has to fit across.
        Assert.InRange(turned.Box.Width, 625, 640);

        var groups = OcrService.GroupRealtime(SheardCard(), 1296, RealtimeBlockMode.Subtitle);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group =>
        {
            var tilt = Assert.IsType<TiltedText>(group.Tilt);
            Assert.True(tilt.Sheared);
            Assert.InRange(tilt.Degrees, 29.5, 30.5);
            Assert.Equal(3, tilt.LineQuads.Count);
        });
    }

    /// <summary>
    /// The detector's box for a line can overshoot the text. On the 30° card it reached past the
    /// card's edge onto the page, and the translation went with it; the card's margin is where most
    /// of its lines start.
    /// </summary>
    [Fact]
    public void TheBoxStartsAtTheCardsMarginRatherThanAtALineThatOvershootsIt()
    {
        var groups = OcrService.GroupRealtime(SheardCard(overshoot: 14), 1296, RealtimeBlockMode.Subtitle);

        Assert.All(groups, group => Assert.InRange(group.Tilt!.Box.Left, Margin - 1, Margin + 1));
        // The erase stops at the margin too, so it does not reach what lies beyond the card's edge.
        var first = groups[0].Tilt!;
        Assert.All(first.EraseQuads, quad => Assert.All(quad, corner =>
            Assert.True(first.ToLevel(corner).X >= Margin - 0.01, $"erase reaches {first.ToLevel(corner).X:0.#}")));
    }

    /// <summary>
    /// The card's comments sit 0.55 of a line's thickness apart, and their upright boxes overlap
    /// one another. Drawn along the card, each band covers its own comment's lines and none of the
    /// next one's.
    /// </summary>
    [Fact]
    public void EachCommentsBandCoversItsOwnLinesAndNotTheNextComments()
    {
        var groups = OcrService.GroupRealtime(SheardCard(), 1296, RealtimeBlockMode.Subtitle);
        var lines = groups.Select(Translated).ToList();

        var bands = OnStaThread(() =>
        {
            var window = Window(lines);
            var scrim = (Canvas)window.FindName("ScrimCanvas");
            var text = (Canvas)window.FindName("TextCanvas");
            Assert.Equal(2, scrim.Children.Count);
            Assert.Equal(2, text.Children.Count);

            return scrim.Children.Cast<Border>().Zip(text.Children.Cast<Border>(), (band, written) =>
            {
                // The text is laid out on the band and turned with it.
                var skew = Assert.IsType<SkewTransform>(band.RenderTransform);
                Assert.Equal(0, skew.AngleX);
                Assert.InRange(skew.AngleY, 29.5, 30.5);
                Assert.Equal(skew.Value, written.RenderTransform.Value);
                return Placed(band);
            }).ToList();
        });

        for (int own = 0; own < 2; own++)
        {
            foreach (var quad in groups[own].Tilt!.LineQuads)
                Assert.True(TiltedText.Inside(bands[own], Middle(quad).X, Middle(quad).Y), "its own line is left showing");
            foreach (var quad in groups[1 - own].Tilt!.LineQuads)
                Assert.False(TiltedText.Inside(bands[own], Middle(quad).X, Middle(quad).Y), "the next comment is covered");
        }
    }

    /// <summary>
    /// The 9.4° card's paragraph is five lines and its translation three. As over a level line, the
    /// band covers the source it stands in for, every line of it, however short the translation.
    /// </summary>
    [Fact]
    public void TheBandCoversEverySourceLineWhenTheTranslationIsShorter()
    {
        var group = Assert.Single(
            OcrService.GroupRealtime(TurnedParagraph(), 1298, RealtimeBlockMode.Subtitle),
            group => group.Text.Contains("SINGLE MISTRESS"));
        var line = new TranslatedBlock(group.Text, "不，不走。離開的會是你未來的妻子。", group.Bounds, group.Lines, group.RenderGlyphHeight)
        {
            FontGlyphHeight = group.FontGlyphHeight,
            Tilt = group.Tilt,
        };

        var band = OnStaThread(() =>
        {
            var window = Window([line]);
            var scrim = (Canvas)window.FindName("ScrimCanvas");
            return Placed(Assert.IsType<Border>(Assert.Single(scrim.Children)));
        });

        foreach (var quad in group.Tilt!.LineQuads)
        {
            // The middle of the line, and the middle of each of its ends.
            foreach (var point in new[] { Middle(quad), Middle([quad[0], quad[3]]), Middle([quad[1], quad[2]]) })
                Assert.True(TiltedText.Inside(band, point.X, point.Y), $"({point.X:0},{point.Y:0}) is left showing");
        }
    }

    /// <summary>
    /// The lone line on region-comic-en-3's second card — "...HEY, WHERE DO YOU LIVE?", 9.6 times as
    /// long as thick at 7.4°, under a username too short to give an angle. It is no cluster, so its
    /// box and its group are what they were; it is drawn turned along itself, and its band reaches
    /// both of its ends.
    /// </summary>
    [Fact]
    public void ALoneLongTiltedLineIsDrawnAlongItsSlopeAndGroupedAsBefore()
    {
        var line = TiltedLayoutTests.Rotated("...HEY, WHERE DO YOU LIVE?", 295, 990, 500, 52, 7.4);
        var blocks = new List<OcrTextBlock>
        {
            TiltedLayoutTests.Upright("PNEC**", 43, 917, 144, 63),
            line,
        };

        var groups = OcrService.GroupRealtime(blocks, 1298, RealtimeBlockMode.Subtitle);
        Assert.Equal(2, groups.Count);
        Assert.Null(groups[0].Tilt);
        var lone = groups[1];
        Assert.Equal(line.Bounds, lone.Bounds);
        Assert.Equal(line.LayoutBounds, lone.LayoutBounds);
        Assert.Null(lone.TiltedLines);
        var tilt = Assert.IsType<TiltedText>(lone.Tilt);
        Assert.False(tilt.Sheared);
        Assert.Equal(7.4, tilt.Degrees, 3);
        Assert.Equal(500, tilt.Box.Width, 3);
        Assert.Equal(52, tilt.Box.Height, 3);

        var translated = new TranslatedBlock(lone.Text, "嘿，你住哪裡？", lone.Bounds, lone.Lines, lone.RenderGlyphHeight)
        {
            FontGlyphHeight = lone.FontGlyphHeight,
            Tilt = lone.Tilt,
        };
        var band = OnStaThread(() =>
        {
            var scrim = (Canvas)Window([translated]).FindName("ScrimCanvas");
            var border = Assert.IsType<Border>(Assert.Single(scrim.Children));
            var turn = Assert.IsType<RotateTransform>(border.RenderTransform);
            Assert.Equal(7.4, turn.Angle, 3);
            return Placed(border);
        });
        var quad = Assert.Single(tilt.LineQuads);
        foreach (var point in new[] { Middle(quad), Middle([quad[0], quad[3]]), Middle([quad[1], quad[2]]) })
            Assert.True(TiltedText.Inside(band, point.X, point.Y), $"({point.X:0},{point.Y:0}) is left showing");
    }

    /// <summary>
    /// A line on its own has nothing to agree with, so it has to be sure by itself: at least 6° and
    /// five times as long as thick. Level text's boxes come out tilted by up to 3.4° on long lines and
    /// 9.7° on short ones, and drawn tilted they would be the most visible mistake this can make.
    /// </summary>
    [Theory]
    [InlineData(6.0, 5.0, true)]
    [InlineData(-7.4, 9.6, true)]
    [InlineData(45, 9.6, true)]
    [InlineData(5.0, 9.6, false)]
    [InlineData(5.9, 9.6, false)]
    [InlineData(-5.9, 9.6, false)]
    [InlineData(7.4, 4.9, false)]
    [InlineData(20, 4.9, false)]
    public void ALoneLineIsDrawnTiltedOnlyWhenItIsLongAndTiltedEnough(double degrees, double ratio, bool tilted)
    {
        const double thickness = 40;
        var blocks = new List<OcrTextBlock>
        {
            TiltedLayoutTests.Rotated("A LINE ON ITS OWN", 400, 400, thickness * ratio, thickness, degrees),
        };

        var group = Assert.Single(OcrService.GroupRealtime(blocks, 1296, RealtimeBlockMode.Subtitle));
        Assert.Equal(tilted, group.Tilt is not null);
        Assert.Equal(blocks[0].LayoutBounds, group.LayoutBounds);
    }

    /// <summary>
    /// A bright patch in the upright box but off the line — on the 30° card, the page beside the
    /// card and the card's lit bevel — is something the mask finds standing out. Inside the line's
    /// quadrilaterals only, nothing outside them is touched.
    /// </summary>
    [Fact]
    public void TheEraseStaysInsideTheLinesQuadrilaterals()
    {
        var centre = new Point(180, 150);
        const double degrees = 30, length = 200, thickness = 24;
        var along = new Vector(Math.Cos(degrees * Math.PI / 180), Math.Sin(degrees * Math.PI / 180));
        var across = new Vector(-along.Y, along.X);
        Point[] quad =
        [
            centre - along * (length / 2) - across * (thickness / 2),
            centre + along * (length / 2) - across * (thickness / 2),
            centre + along * (length / 2) + across * (thickness / 2),
            centre - along * (length / 2) + across * (thickness / 2),
        ];
        var upright = TiltedText.Enclosing(quad);

        using var frame = new System.Drawing.Bitmap(360, 300);
        using (var graphics = System.Drawing.Graphics.FromImage(frame))
        {
            graphics.Clear(System.Drawing.Color.FromArgb(40, 40, 48));
            // Upright letters along the slope.
            for (double t = -88; t <= 88; t += 16)
            {
                var letter = centre + along * t;
                graphics.FillRectangle(System.Drawing.Brushes.White, (float)letter.X - 2, (float)letter.Y - 7, 4, 14);
            }
            // Bright, inside the upright box, off the line.
            graphics.FillRectangle(System.Drawing.Brushes.White, 200, 95, 70, 30);
        }

        var tilt = new TiltedText(degrees, true, centre, new Rect(centre.X - 86, centre.Y - 12, 172, 24), [quad]);
        TranslatedBlock Block(TiltedText? shape) =>
            new("A LINE", "一行", upright, [upright], RenderGlyphHeight: 120) { Tilt = shape };

        using var level = RealtimeCpuBackground.Repair(frame, [Block(null)]);
        using var tilted = RealtimeCpuBackground.Repair(frame, [Block(tilt)]);

        var erase = tilt.EraseQuads;
        bool OffTheLine(int x, int y) => !erase.Any(polygon => TiltedText.Inside(polygon, x + .5, y + .5));
        int changedOff = 0, levelChangedOff = 0;
        for (int y = 0; y < frame.Height; y++)
        for (int x = 0; x < frame.Width; x++)
        {
            if (!OffTheLine(x, y)) continue;
            var original = frame.GetPixel(x, y).ToArgb();
            if (tilted.GetPixel(x, y).ToArgb() != original) changedOff++;
            if (level.GetPixel(x, y).ToArgb() != original) levelChangedOff++;
        }

        Assert.Equal(0, changedOff);
        // The fixture is one the upright box does get wrong.
        Assert.True(levelChangedOff > 0, "the upright box erased nothing off the line either");
        // And the letters are still erased.
        var middle = centre + along * 8;
        Assert.True(tilted.GetPixel((int)middle.X, (int)middle.Y).R < 120);
    }

    /// <summary>Level text carries no tilt, and is drawn without a transform.</summary>
    [Fact]
    public void LevelTextIsDrawnAsBefore()
    {
        var blocks = new List<OcrTextBlock>
        {
            TiltedLayoutTests.Rotated("IF YOU'D SEEN HIM CRAWLING FORWARD AFTER", 450, 150, 495, 36, 0.3),
            TiltedLayoutTests.Rotated("GETTING COMPLETELY WRECKED AT THE END,", 440, 181, 472, 33, 0.2),
        };
        var groups = OcrService.GroupRealtime(blocks, 1296, RealtimeBlockMode.Subtitle);
        Assert.All(groups, group => Assert.Null(group.Tilt));
        Assert.All(groups, group => Assert.Null(group.TiltedLines));

        OnStaThread(() =>
        {
            var window = Window([.. groups.Select(Translated)]);
            foreach (var element in ((Canvas)window.FindName("ScrimCanvas")).Children.Cast<UIElement>()
                         .Concat(((Canvas)window.FindName("TextCanvas")).Children.Cast<UIElement>()))
                Assert.True(element.RenderTransform.Value.IsIdentity);
            return 0;
        });
    }

    private const double Margin = 80;

    private static List<OcrTextBlock> TurnedParagraph()
    {
        string[] text =
        [
            "BLING0_0*** NOPE, NOT LEAVING. THE ONE",
            "LEAVING WILL BE YOUR FUTURE WIFE. NO MATTER",
            "HOW NICE SHE IS, SHE WON'T ENDURE THAT AND",
            "SHE'LL RUN AWAY. SO EVEN AS A NOBLE, YOU",
            "WON'T BE ABLE TO KEEP A SINGLE MISTRESS.",
        ];
        var lengths = new[] { 545.0, 630, 594, 563, 565 };
        var turn = -9.4 * Math.PI / 180;
        var origin = new Point(125, 440);
        return [.. Enumerable.Range(0, 5).Select(i =>
        {
            double x = lengths[i] / 2, y = i * 33;
            return TiltedLayoutTests.Rotated(text[i],
                origin.X + x * Math.Cos(turn) - y * Math.Sin(turn),
                origin.Y + x * Math.Sin(turn) + y * Math.Cos(turn),
                lengths[i], 41, -9.4);
        })];
    }

    /// <param name="overshoot">How far left of the margin the first comment's first line starts.</param>
    private static List<OcrTextBlock> SheardCard(double overshoot = 0)
    {
        const double slope = 30, thickness = 32;
        var pitch = 0.55 * thickness / Math.Cos(slope * Math.PI / 180);
        var gap = 1.15 * thickness / Math.Cos(slope * Math.PI / 180);

        var y = 100.0;
        var blocks = new List<OcrTextBlock>();
        void Add(string text, double length, double advance, double left = Margin)
        {
            y += advance;
            blocks.Add(TiltedLayoutTests.Sloped(text, left, y, length, thickness, slope));
        }

        Add("arolf52'5", 126, 0);
        Add("IF YOU'D SEEN HIM IN PERSON EVEN", 363, pitch * 1.2, Margin - overshoot);
        Add("ONCE, YOU'D NEVER SAY THAT LOL", 340, pitch);
        Add("Ykbell", 150, gap);
        Add("WHEN HE SCREAMED WHILE STEPPING ON THAT", 387, pitch * 1.2);
        Add("ABYSS GOBLIN TRAP, IT GOT MY BLOOD PUMPING", 399, pitch);
        return blocks;
    }

    private static TranslatedBlock Translated(OcrTextBlock group) =>
        new(group.Text, "這是一段翻譯出來的留言，長度跟原文差不多。", group.Bounds, group.Lines, group.RenderGlyphHeight)
        {
            FontGlyphHeight = group.FontGlyphHeight,
            Tilt = group.Tilt,
        };

    /// <summary>The block window over a 1296px page, built for real with both 進階選項 off.</summary>
    private static RealtimeBlockWindow Window(IReadOnlyList<TranslatedBlock> lines)
    {
        var window = new RealtimeBlockWindow(
            0, new System.Drawing.Rectangle(0, 0, 900, 1296), _ => null, "EN", "ZH-TW",
            RealtimeSubtitleColors.DefaultText,
            RealtimeSubtitleColors.DefaultScrim,
            RealtimeSubtitleColors.DefaultScrimOpacity);
        typeof(RealtimeBlockWindow)
            .GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, true);
        window.SetLines(lines);
        return window;
    }

    /// <summary>An element's four corners where its transform puts them on the canvas.</summary>
    private static Point[] Placed(FrameworkElement element)
    {
        var at = new Vector(Canvas.GetLeft(element), Canvas.GetTop(element));
        var matrix = element.RenderTransform.Value;
        return
        [
            matrix.Transform(new Point(0, 0)) + at,
            matrix.Transform(new Point(element.Width, 0)) + at,
            matrix.Transform(new Point(element.Width, element.Height)) + at,
            matrix.Transform(new Point(0, element.Height)) + at,
        ];
    }

    private static Point Middle(Point[] quad) =>
        new(quad.Average(point => point.X), quad.Average(point => point.Y));

    private static T OnStaThread<T>(Func<T> work)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception exception) { failure = ExceptionDispatchInfo.Capture(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }
}
