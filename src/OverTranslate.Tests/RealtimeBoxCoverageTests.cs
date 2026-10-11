using System.Drawing;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Which translation boxes of an overlay another application's window takes away: only the ones it
/// touches, only when their state changes, and never by showing words older than the newest.
/// </summary>
public class RealtimeBoxCoverageTests
{
    // Two lines of a subtitle block, each a text area and its band, in physical pixels.
    private static readonly IReadOnlyList<IReadOnlyList<Rectangle>> TwoLines =
    [
        [new Rectangle(400, 900, 700, 50), new Rectangle(390, 895, 720, 60)],
        [new Rectangle(400, 960, 700, 50), new Rectangle(390, 955, 720, 60)],
    ];

    private static readonly Rectangle OverFirstLine = new(1000, 820, 480, 100);
    private static readonly Rectangle OverEmptyPart = new(1200, 1030, 480, 170);

    private static RealtimeBoxCoverage Drawn(IReadOnlyList<IReadOnlyList<Rectangle>> boxes)
    {
        var coverage = new RealtimeBoxCoverage();
        coverage.Redrawn(boxes.Count, () => boxes);
        return coverage;
    }

    [Fact]
    public void A_window_over_the_empty_part_of_a_block_hides_nothing()
    {
        var coverage = Drawn(TwoLines);

        Assert.Empty(coverage.Covered([OverEmptyPart], () => TwoLines));
        Assert.Equal(0, coverage.HiddenCount);
    }

    [Fact]
    public void A_window_over_one_line_hides_that_line_only()
    {
        var coverage = Drawn(TwoLines);

        Assert.Equal([(0, true)], coverage.Covered([OverFirstLine], () => TwoLines));
        Assert.Equal(1, coverage.HiddenCount);
    }

    // The band counts as much as the words on it.
    [Fact]
    public void Touching_only_a_lines_band_hides_it()
    {
        var coverage = Drawn(TwoLines);
        var overBandEdge = new Rectangle(1105, 1012, 50, 20);

        Assert.Equal([(1, true)], coverage.Covered([overBandEdge], () => TwoLines));
    }

    [Fact]
    public void Moving_the_window_away_brings_the_line_back()
    {
        var coverage = Drawn(TwoLines);
        coverage.Covered([OverFirstLine], () => TwoLines);

        Assert.Equal([(0, false)], coverage.Covered([OverEmptyPart], () => TwoLines));
        Assert.Equal(0, coverage.HiddenCount);
    }

    // The point of keeping state per box: a look that changes nothing touches nothing on the screen.
    [Fact]
    public void A_look_that_changes_nothing_reports_nothing()
    {
        var coverage = Drawn(TwoLines);
        coverage.Covered([OverFirstLine], () => TwoLines);

        Assert.Empty(coverage.Covered([OverFirstLine], () => TwoLines));
        Assert.Empty(coverage.Covered([OverFirstLine, OverEmptyPart], () => TwoLines));
    }

    // Nothing over the source is answered without laying anything out.
    [Fact]
    public void Nothing_is_measured_while_nothing_is_over_the_source()
    {
        var coverage = Drawn(TwoLines);

        Assert.Empty(coverage.Covered([], () => throw new InvalidOperationException("measured")));
        Assert.Empty(coverage.Redrawn(2, () => throw new InvalidOperationException("measured")));
    }

    // A new translation arriving under a window is drawn hidden at once rather than shown until the
    // next look — and the line under the window is the one hidden, not whatever was there before.
    [Fact]
    public void A_line_drawn_under_a_window_is_hidden_as_it_is_drawn()
    {
        var coverage = Drawn(TwoLines);
        coverage.Covered([OverFirstLine], () => TwoLines);

        IReadOnlyList<IReadOnlyList<Rectangle>> newLines =
        [
            [new Rectangle(400, 1100, 700, 50)],
            [new Rectangle(400, 900, 700, 50)],
            [new Rectangle(400, 960, 700, 50)],
        ];

        Assert.Equal([1], coverage.Redrawn(3, () => newLines));
        Assert.Equal(1, coverage.HiddenCount);
    }

    // What comes back when the window moves is the newest drawing, whatever was hidden before it.
    [Fact]
    public void Uncovering_after_a_redraw_shows_the_new_lines()
    {
        var coverage = Drawn(TwoLines);
        coverage.Covered([OverFirstLine], () => TwoLines);
        IReadOnlyList<IReadOnlyList<Rectangle>> newLines = [[new Rectangle(400, 900, 700, 50)]];
        coverage.Redrawn(1, () => newLines);

        Assert.Equal([(0, false)], coverage.Covered([], () => newLines));
        Assert.Equal(0, coverage.HiddenCount);
    }

    [Fact]
    public void An_overlay_drawing_nothing_has_nothing_to_hide()
    {
        var coverage = Drawn([]);

        Assert.Empty(coverage.Covered([new Rectangle(0, 0, 3840, 2160)], () => []));
    }
}
