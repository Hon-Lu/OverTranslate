using System.Drawing;
using System.Drawing.Text;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The sentence-end zones: a line read a moment before its last glyph or full stop was drawn has to
/// be read again once that glyph arrives, though nothing else the region watches can see it.
/// </summary>
public class RealtimeSentenceEndTests
{
    private static readonly Rectangle Line = new(10, 40, 300, 30);
    private static readonly Rectangle[] Zones = [new(310, 40, 90, 30), new(10, 70, 90, 45)];

    [Fact]
    public void AnEndingThatChangedAndSettledIsRead()
    {
        var (state, ends) = Watching("静かに");

        ends.Add();
        Assert.Equal(RealtimeReadReason.Nothing, Poll(state, ends));
        Assert.Equal(RealtimeReadReason.TextChanged, Poll(state, ends));
        Assert.True(state.ReadForEnding);
    }

    [Fact]
    public void AnEndingStillChangingIsLeftAlone()
    {
        // A line still being typed out, or a video behind the zone: never the same twice running.
        var (state, ends) = Watching("静かに");

        for (var poll = 0; poll < 20; poll++)
        {
            ends.Add();
            Assert.Equal(RealtimeReadReason.Nothing, Poll(state, ends));
        }
    }

    [Fact]
    public void TheEndingReadsRunOutUntilAReadingIsTaken()
    {
        var (state, ends) = Watching("静かに");

        // Each read that was not taken records the frame the same words again, and costs one.
        for (var read = 0; read < RealtimeRegionState.EndReadsPerChange; read++)
        {
            Assert.Equal(RealtimeReadReason.TextChanged, Settle(state, ends));
            state.MarkRendered([Line], Still, Lines("静かに"), Zones, ends.Capture);
        }

        Assert.Equal(RealtimeReadReason.Nothing, Settle(state, ends));

        // A reading taken gives them back.
        state.MarkRendered([Line], Still, Lines("静かに。"), Zones, ends.Capture);
        Assert.Equal(RealtimeReadReason.TextChanged, Settle(state, ends));
    }

    [Fact]
    public void AConfirmationAfterTheEndingGrewIsARealRead()
    {
        // The C-type loss on the stage: the confirmation pass reused the last reading because the
        // whole region's print could not see the full stop that had arrived since.
        var (state, ends) = Watching("静かに");
        state.RememberRead([new OcrTextBlock("静かに", new System.Windows.Rect(10, 40, 300, 30))]);
        state.Dialogue.Merge([], [new OcrTextBlock("静かに", new System.Windows.Rect(10, 40, 300, 30))]);
        Assert.True(state.Dialogue.NeedsConfirmation);

        ends.Add();
        Assert.Equal(RealtimeReadReason.TextChanged, state.Examine(Still, dialogue: true, ends.Capture));
        Assert.True(state.ReadingAgain);
        Assert.False(state.SamePictureAsRead);
    }

    [Fact]
    public void AConfirmationOverAnUnchangedEndingStillReusesTheRead()
    {
        var (state, ends) = Watching("静かに");
        state.RememberRead([new OcrTextBlock("静かに", new System.Windows.Rect(10, 40, 300, 30))]);
        state.Dialogue.Merge([], [new OcrTextBlock("静かに", new System.Windows.Rect(10, 40, 300, 30))]);

        Assert.Equal(RealtimeReadReason.TextChanged, state.Examine(Still, dialogue: true, ends.Capture));
        Assert.True(state.SamePictureAsRead);
    }

    [Fact]
    public void APanelConfirmsOnlyWhenAnEndingIsPending()
    {
        var (state, ends) = Watching("We should leave this place right no");
        Assert.Equal(RealtimeReadReason.Nothing, Poll(state, ends));

        var shown = state.Dialogue.MergePanel([], [Read("We should leave this place right no")]);
        state.Dialogue.MergePanel(shown.Lines, [Read("We should leave this place right now.")]);
        Assert.True(state.Dialogue.HasPendingEnding);
        Assert.Equal(RealtimeReadReason.TextChanged, Poll(state, ends));
    }

    [Fact]
    public void ZonesOutsideTheRegionAreNotWatched()
    {
        // A line ending flush against the right edge, sitting on the bottom edge.
        var block = new OcrTextBlock("端の行", new System.Windows.Rect(300, 60, 100, 40));
        Assert.Empty(RealtimeRegionState.BuildEndZones(
            [block], new Size(400, 100), RealtimeTextOrientation.Horizontal));

        // Partly outside: cut to the region.
        var inside = new OcrTextBlock("中の行", new System.Windows.Rect(10, 10, 300, 30));
        var zones = RealtimeRegionState.BuildEndZones(
            [inside], new Size(350, 60), RealtimeTextOrientation.Horizontal);
        Assert.Equal(new[] { new Rectangle(310, 10, 40, 30), new Rectangle(10, 40, 90, 20) }, zones);
    }

    [Fact]
    public void ZonesFollowTheLastLineOfTheBlock()
    {
        var across = new OcrTextBlock("一行目\n二行目", new System.Windows.Rect(10, 10, 300, 70),
            SourceLineBounds: [new System.Windows.Rect(10, 10, 300, 30), new System.Windows.Rect(10, 50, 200, 30)]);
        Assert.Equal(
            new[] { new Rectangle(210, 50, 90, 30), new Rectangle(10, 80, 90, 45) },
            RealtimeRegionState.BuildEndZones([across], new Size(1000, 1000), RealtimeTextOrientation.Horizontal));

        // Written down the page the last column is the leftmost: it carries on below itself, or
        // wraps to the top of the column to its left.
        var down = new OcrTextBlock("縦書き", new System.Windows.Rect(100, 50, 70, 300),
            SourceLineBounds: [new System.Windows.Rect(140, 50, 30, 300), new System.Windows.Rect(100, 50, 30, 200)]);
        Assert.Equal(
            new[] { new Rectangle(100, 250, 30, 90), new Rectangle(55, 50, 45, 90) },
            RealtimeRegionState.BuildEndZones([down], new Size(1000, 1000), RealtimeTextOrientation.Vertical));
    }

    [Fact]
    public void AFullStopDrawnAfterTheReadIsOnlySeenByTheEndZones()
    {
        // The stage's geometry: 34px white text on a dark dialogue box, region 1240x330.
        using var before = Draw("静かに");
        using var after = Draw("静かに。");
        var box = Ink(before);
        var height = box.Height;
        var band = Rectangle.FromLTRB(box.Left - 6, box.Top - (int)Math.Round(height * 0.75),
            box.Right + 6, box.Bottom + (int)Math.Round(height * 0.75));

        // The three comparisons the region had: the strips, the whole region by its busiest rows,
        // and the identical-picture test the idle scan trusts. None of them sees the full stop.
        Assert.False(FrameFingerprint.Capture(after, [band]).Differs(FrameFingerprint.Capture(before, [band])));
        Assert.False(FrameFingerprint.Capture(after, null).DiffersLocally(FrameFingerprint.Capture(before, null)));
        Assert.True(FrameFingerprint.Capture(after, null).IsIdenticalTo(FrameFingerprint.Capture(before, null)));

        var zones = RealtimeRegionState.BuildEndZones(
            [new OcrTextBlock("静かに", new System.Windows.Rect(box.X, box.Y, box.Width, box.Height))],
            before.Size, RealtimeTextOrientation.Horizontal);
        Assert.True(FrameFingerprint.CaptureEnds(after, zones)
            .DiffersInAnyCell(FrameFingerprint.CaptureEnds(before, zones)));

        // And the region reads it: without the zones it never would, with them it does once settled.
        var state = new RealtimeRegionState();
        state.MarkRendered([box], areas => FrameFingerprint.Capture(before, areas), Lines("静かに"),
            zones, areas => FrameFingerprint.CaptureEnds(before, areas));
        FrameFingerprint Now(IReadOnlyList<Rectangle>? areas) => FrameFingerprint.Capture(after, areas);
        FrameFingerprint NowEnds(IReadOnlyList<Rectangle> areas) => FrameFingerprint.CaptureEnds(after, areas);

        var reasons = Enumerable.Range(0, 3).Select(_ => state.Examine(Now, captureEnds: NowEnds)).ToList();
        Assert.Contains(RealtimeReadReason.TextChanged, reasons);

        var blind = new RealtimeRegionState();
        blind.MarkRendered([box], areas => FrameFingerprint.Capture(before, areas), Lines("静かに"));
        for (var poll = 0; poll < 40; poll++)
            Assert.Equal(RealtimeReadReason.Nothing, blind.Examine(Now));
    }

    private static Bitmap Draw(string text)
    {
        var bitmap = new Bitmap(1240, 330);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(28, 30, 42));
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Yu Gothic", 34, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.DrawString(text, font, Brushes.White, 34, 84);
        return bitmap;
    }

    // The bounding box of the bright pixels — the line as a detector would box it.
    private static Rectangle Ink(Bitmap bitmap)
    {
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (bitmap.GetPixel(x, y).GetBrightness() < 0.5f) continue;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }

        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static (RealtimeRegionState State, FakeEnds Ends) Watching(string text)
    {
        var state = new RealtimeRegionState();
        var ends = new FakeEnds();
        state.MarkRendered([Line], Still, Lines(text), Zones, ends.Capture);
        return (state, ends);
    }

    private static RealtimeReadReason Poll(RealtimeRegionState state, FakeEnds ends) =>
        state.Examine(Still, captureEnds: ends.Capture);

    // A glyph arrives and holds still: the first poll sees it moving, the second sees it settled.
    private static RealtimeReadReason Settle(RealtimeRegionState state, FakeEnds ends)
    {
        ends.Add();
        var first = Poll(state, ends);
        return first != RealtimeReadReason.Nothing ? first : Poll(state, ends);
    }

    private static IReadOnlyList<RenderedLine> Lines(string text) => [new RenderedLine(text, 0.98)];

    private static OcrTextBlock Read(string text) =>
        new(text, new System.Windows.Rect(10, 40, 300, 30), Confidence: 0.98);

    // Everything but the end zones holds perfectly still.
    private static FrameFingerprint Still(IReadOnlyList<Rectangle>? areas) =>
        new(Enumerable.Repeat((byte)20, 100).ToArray());

    private sealed class FakeEnds
    {
        private readonly byte[] _cells = new byte[24];
        private int _glyphs;

        // One more glyph: one more cell lights up, well past the tolerance.
        public void Add() => _cells[_glyphs++ % _cells.Length] += 60;

        public FrameFingerprint Capture(IReadOnlyList<Rectangle> zones) => new([.. _cells]);
    }
}
