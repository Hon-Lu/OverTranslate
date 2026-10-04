using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Overlay;
using OverTranslate.Views.Realtime;
using Xunit;
using Point = System.Windows.Point;

namespace OverTranslate.Tests;

/// <summary>
/// A group of vertical columns is drawn tilted only when its columns agree that it is, and then
/// only how it is drawn and erased changes: what it says, where it is and the size of its letters
/// stay as a straight group's. See Ocr.TiltedColumns.
/// </summary>
/// <remarks>
/// The tilted groups are vertical-image-ja4 (1.png) as the probe printed them: two narration
/// columns at −6.2° and −6.3°, 260 and 374 long, 50 and 52 thick.
/// </remarks>
public class TiltedColumnsTests
{
    private static OcrLineGeometry Geometry(double deviation, double length, double thickness)
    {
        double angle = 90 + deviation;
        if (angle > 90) angle -= 180;
        return new OcrLineGeometry(length, thickness, angle);
    }

    [Theory]
    // Two columns agreeing, both past the floor: the length-weighted angle.
    [InlineData(-6.2, 260, 50, -6.3, 374, 52, -6.259)]
    [InlineData(12.7, 304, 51, 10.3, 207, 56, 11.728)]
    // Straight pages measure up to 2.5°: not tilted.
    [InlineData(-2.4, 117, 29, -2.2, 133, 57, null)]
    [InlineData(2.4, 239, 57, 3.5, 181, 65, null)]
    // One column under the floor is enough to say no.
    [InlineData(-3.2, 326, 57, -4.2, 302, 65, null)]
    // Leaning opposite ways is not one turn.
    [InlineData(5, 300, 50, -5, 300, 50, null)]
    // Too far apart to be one card.
    [InlineData(4.5, 300, 50, 8, 300, 50, null)]
    public void TwoColumnsAreTiltedWhenTheyAgree(
        double first, double firstLength, double firstThickness,
        double second, double secondLength, double secondThickness, double? expected)
    {
        var angle = TiltedColumns.Angle(
        [
            Geometry(first, firstLength, firstThickness),
            Geometry(second, secondLength, secondThickness),
        ]);

        if (expected is null) Assert.Null(angle);
        else Assert.Equal(expected.Value, angle!.Value, tolerance: 0.01);
    }

    [Theory]
    // A long strip turned clearly: ja4 封印は… and 返事は….
    [InlineData(7.8, 452, 59, true)]
    [InlineData(-12.3, 339, 54, true)]
    // Long, but too slight to say so alone.
    [InlineData(5.3, 248, 42, false)]
    // Turned, but too short to be believed alone: ja4 振り向くな at 4.2 to one.
    [InlineData(8, 196, 47, false)]
    public void AColumnAloneIsTiltedOnlyWhenLongAndClearlyTurned(
        double deviation, double length, double thickness, bool tilted)
    {
        var angle = TiltedColumns.Angle([Geometry(deviation, length, thickness)]);

        Assert.Equal(tilted, angle is not null);
        if (tilted) Assert.Equal(deviation, angle!.Value, tolerance: 1e-9);
    }

    [Fact]
    public void AShortFitDoesNotCountAndTheLongColumnIsJudgedAlone()
    {
        // A two-character scrap beside a long tilted column: under two to one, it is not asked.
        var angle = TiltedColumns.Angle([Geometry(7.8, 452, 59), Geometry(-20, 60, 40)]);

        Assert.Equal(7.8, angle!.Value, tolerance: 1e-9);
    }

    [Fact]
    public void AReducedPicturesColumnsCanBeHeldToMoreThanTwoToOne()
    {
        // ja2 今日はこの辺で at 0.35×: two fits of barely 2:1 said −5.6° for a straight balloon.
        OcrLineGeometry[] columns = [Geometry(-5.0, 163, 80), Geometry(-6.5, 101, 49)];

        Assert.NotNull(TiltedColumns.Angle(columns));
        Assert.Null(TiltedColumns.Angle(columns, trustedLength: 2.5));
    }

    /// <summary>A column as the engine hands it to grouping: its quadrilateral and the box round it.</summary>
    private static OcrTextBlock Column(string text, double centreX, double centreY, double length, double thickness, double deviation)
    {
        var geometry = Geometry(deviation, length, thickness);
        var box = TiltedText.Enclosing(TiltedColumns.Quad(new Rect(centreX, centreY, 0, 0), geometry));
        return new OcrTextBlock(text, box, Confidence: 0.98) { LayoutBounds = box, LineGeometry = geometry };
    }

    private static List<OcrTextBlock> Narration(double deviation) =>
        VerticalOcrGeometry.PrepareBlocks(
        [
            Column("昨日のことが、", 513, 290, 260, 50, deviation),
            Column("まだ頭から離れない。", 462, 350, 374, 52, deviation - 0.1),
        ]);

    [Fact]
    public void ATiltedGroupIsGroupedExactlyAsAStraightOneAndOnlyCarriesItsTilt()
    {
        var tilted = Assert.Single(VerticalColumnGrouping.Group(Narration(-6.2), frameWidth: 941));

        // The same boxes, with quadrilaterals standing upright.
        var upright = Narration(-6.2)
            .Select(column => column with { LineGeometry = Geometry(0, column.LineGeometry!.Value.Length, column.LineGeometry!.Value.Thickness) })
            .ToList();
        var straight = Assert.Single(VerticalColumnGrouping.Group(upright, frameWidth: 941));

        Assert.Equal(Describe(straight), Describe(tilted));
        Assert.Null(straight.Tilt);
        var tilt = Assert.IsType<TiltedText>(tilted.Tilt);
        Assert.True(tilt.Column);
        Assert.False(tilt.Sheared);
        Assert.Equal(-6.26, tilt.Degrees, tolerance: 0.01);
        Assert.Equal(2, tilt.LineQuads.Count);
    }

    [Fact]
    public void AStraightPageCarriesNoTilt()
    {
        var groups = VerticalColumnGrouping.Group(Narration(-2.3), frameWidth: 941);

        Assert.All(groups, group => Assert.Null(group.Tilt));
    }

    private static string Describe(OcrTextBlock group) =>
        $"{group.Text}|{group.Bounds}|{group.LayoutBounds}|{string.Join(";", group.Lines)}|" +
        $"{group.RenderGlyphHeight}|{group.LayoutGlyphHeight}|{group.FontGlyphHeight}|{group.Confidence}";

    [Fact]
    public void TheLevelBoxIsTheColumnsOwnRoomAndTheEraseCoversThemEndToEnd()
    {
        var group = Assert.Single(VerticalColumnGrouping.Group(Narration(-12), frameWidth: 941));
        var tilt = group.Tilt!;

        // Two columns side by side, 51 apart, 50 and 52 thick: about 102 across; the longer column
        // sets the height. The upright box round them is far wider.
        Assert.InRange(tilt.Box.Width, 95, 115);
        Assert.InRange(tilt.Box.Height, 374, 400);
        Assert.True(group.Bounds.Width > tilt.Box.Width + 40);

        // Each column's head and foot are inside its own erase, and the middle between the two
        // columns' far sides is not left out by a cut at a margin.
        foreach (var quad in tilt.LineQuads)
        {
            var head = Point.Add(quad[0], (quad[3] - quad[0]) / 2);
            var foot = Point.Add(quad[1], (quad[2] - quad[1]) / 2);
            Assert.Contains(tilt.EraseQuads, polygon => TiltedText.Inside(polygon, head.X, head.Y));
            Assert.Contains(tilt.EraseQuads, polygon => TiltedText.Inside(polygon, foot.X, foot.Y));
        }
        Assert.Equal(tilt.LineQuads.Count, tilt.EraseQuads.Count);
        Assert.All(tilt.EraseQuads, polygon => Assert.Equal(4, polygon.Length));
    }

    private static TranslatedBlock Translated(OcrTextBlock group) =>
        new(group.Text, "昨天的事情還在腦海裡揮之不去。", group.Bounds, group.Lines, group.RenderGlyphHeight)
        {
            Tilt = group.Tilt,
        };

    [Fact]
    public void TheLiveColumnIsTurnedWithItsBandAndOutlinedInBothLooks()
    {
        var group = Assert.Single(VerticalColumnGrouping.Group(Narration(-6.2), frameWidth: 941));
        var line = Translated(group);

        OnStaThread(() =>
        {
            var window = Window([line], natural: false, border: true);
            var band = Assert.IsType<Border>(Assert.Single(((Canvas)window.FindName("ScrimCanvas")).Children));
            Assert.Equal(-6.26, Assert.IsType<RotateTransform>(band.RenderTransform).Angle, tolerance: 0.01);
            Assert.Equal(new Thickness(RealtimeSubtitleColors.BorderThickness), band.BorderThickness);
            var text = Assert.IsType<Border>(Assert.Single(((Canvas)window.FindName("TextCanvas")).Children));
            Assert.Equal(-6.26, Assert.IsType<RotateTransform>(text.RenderTransform).Angle, tolerance: 0.01);

            // Over a repaired patch: upright, cut to the band and the erased quadrilaterals, and the
            // band drawn as a stroke inside it rather than the patch's upright edge.
            var patches = Window([line], natural: true, border: true);
            var patch = Assert.IsType<Border>(Assert.Single(((Canvas)patches.FindName("ScrimCanvas")).Children));
            Assert.True(patch.RenderTransform.Value.IsIdentity);
            Assert.NotNull(patch.Clip);
            Assert.Equal(new Thickness(0), patch.BorderThickness);
            Assert.IsType<System.Windows.Shapes.Path>(patch.Child);
            return 0;
        });
    }

    [Fact]
    public void AStraightColumnIsDrawnAsBefore()
    {
        var group = Assert.Single(VerticalColumnGrouping.Group(Narration(-2.3), frameWidth: 941));
        var line = Translated(group);

        OnStaThread(() =>
        {
            foreach (var natural in new[] { false, true })
            {
                var window = Window([line], natural, border: true);
                foreach (var element in ((Canvas)window.FindName("ScrimCanvas")).Children.Cast<FrameworkElement>()
                             .Concat(((Canvas)window.FindName("TextCanvas")).Children.Cast<FrameworkElement>()))
                    Assert.True(element.RenderTransform.Value.IsIdentity);
            }
            return 0;
        });
    }

    [Fact]
    public void TheCaptureColumnIsTurnedOntoTheSource()
    {
        var tilted = Translated(Assert.Single(VerticalColumnGrouping.Group(Narration(-6.2), frameWidth: 941)));
        var straight = Translated(Assert.Single(VerticalColumnGrouping.Group(Narration(-2.3), frameWidth: 941)));

        OnStaThread(() =>
        {
            var (background, cells) = Capture(tilted);
            Assert.Equal(-6.26, Assert.IsType<RotateTransform>(background.RenderTransform).Angle, tolerance: 0.01);
            Assert.Equal(-6.26, Assert.IsType<RotateTransform>(cells.RenderTransform).Angle, tolerance: 0.01);
            // The bubble is laid out on the level box: as wide as the two columns, not as their
            // upright box.
            Assert.InRange(background.Width, tilted.Tilt!.Box.Width, tilted.Tilt.Box.Width + 10);

            var (level, levelCells) = Capture(straight);
            Assert.True(level.RenderTransform.Value.IsIdentity);
            Assert.True(levelCells.RenderTransform.Value.IsIdentity);
            return 0;
        });
    }

    private static (FrameworkElement Background, FrameworkElement Cells) Capture(TranslatedBlock line)
    {
        var window = new OverlayWindow([], [], 0, 0, 941, 1672, "JA", "ZH-HANT", true);
        try
        {
            typeof(OverlayWindow).GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.UpdateBlocks([line], [], 0, 0, 941, 1672, "JA", "ZH-HANT", true);
            var background = Assert.IsAssignableFrom<FrameworkElement>(
                Assert.Single(((Canvas)window.FindName("BubbleBackgroundCanvas")).Children));
            var texts = ((Canvas)window.FindName("BubbleTextCanvas")).Children.Cast<FrameworkElement>().ToList();
            // A tilted group's glyphs are in one turned canvas; a straight one's are placed one by one.
            return (background, texts.Count == 1 && texts[0] is Canvas canvas ? canvas : texts[0]);
        }
        finally
        {
            window.Close();
        }
    }

    private static RealtimeBlockWindow Window(IReadOnlyList<TranslatedBlock> lines, bool natural, bool border)
    {
        var window = new RealtimeBlockWindow(
            0, new System.Drawing.Rectangle(0, 0, 941, 1672), _ => null, "JA", "ZH-TW",
            RealtimeSubtitleColors.DefaultText,
            RealtimeSubtitleColors.DefaultScrim,
            RealtimeSubtitleColors.DefaultScrimOpacity,
            naturalBackground: natural,
            border: border,
            borderColor: border ? "#E0A030" : null,
            orientation: RealtimeTextOrientation.Vertical);
        typeof(RealtimeBlockWindow)
            .GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, true);
        window.SetLines(lines);
        return window;
    }

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
