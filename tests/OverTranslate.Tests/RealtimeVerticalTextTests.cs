using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The live overlay setting a translation into vertical writing: every character on screen, in
/// reading order, inside the block the user drew.
/// </summary>
/// <remarks>
/// The same three promises <see cref="OverlayTextFittingTests"/> holds the horizontal band to, asked
/// of the other axis, and built the same way — the real window, driven on an STA thread, read back
/// out of the visual tree rather than re-derived. Reading order matters more here than it does
/// across: a column filled the wrong way round is still a complete sentence, and still unreadable.
/// </remarks>
public class RealtimeVerticalTextTests
{
    // A tall, narrow watch area of the kind vertical writing is framed in, with one column of
    // source down the right of it.
    private static readonly System.Drawing.Rectangle ColumnBlock = new(300, 100, 600, 400);

    private static readonly Rect SourceColumn = new(400, 40, 34, 300);

    private const double SourceGlyphSize = 34;

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(140)]
    [InlineData(600)]
    public void A_vertical_grid_never_asks_for_more_room_than_it_was_given(int characters)
    {
        var (cellSize, width, height) = VerticalTextGrid.FitWithin(
            columnRoom: 48, columnLength: 300, maxWidth: 200,
            preferredCellSize: 24, minCellSize: 8.5, characters);

        Assert.True(width <= 200 + 0.001, $"grid is {width:0.##}px wide against 200px of room");
        Assert.True(height <= 300 + 0.001, $"grid is {height:0.##}px tall against 300px of room");
        Assert.True(cellSize >= 8.5, $"cell shrank past the floor to {cellSize:0.##}px");
    }

    /// <summary>
    /// The choice the live layer makes that the screenshot one does not: the block is fixed, so what
    /// gives is the type size rather than the rectangle.
    /// </summary>
    [Fact]
    public void A_column_too_full_for_the_block_shrinks_the_cell_instead_of_growing()
    {
        var (cellSize, width, height) = VerticalTextGrid.FitWithin(
            columnRoom: 80, columnLength: 200, maxWidth: 80,
            preferredCellSize: 24, minCellSize: 8.5, characterCount: 120);

        Assert.True(cellSize < 24, $"cell stayed at {cellSize:0.##}px with 120 characters to place");
        Assert.True(width <= 80 + 0.001);
        Assert.True(height <= 200 + 0.001);
    }

    /// <summary>
    /// The live layer's answer to a translation longer than the column it replaces, and the one it
    /// used to get wrong: the type is set smaller inside the balloon before a second column is
    /// opened beside it.
    /// </summary>
    /// <remarks>
    /// A column beside the source is not free room, it is the picture — the next balloon, the next
    /// panel. The screenshot overlay has always shrunk first, which is why the same page read well
    /// there and wrapped into its neighbours here.
    /// </remarks>
    [Fact]
    public void A_translation_longer_than_its_source_column_shrinks_before_it_takes_another()
    {
        // One source column 24px across and five characters long, asked to hold seven.
        var (cellSize, width, _) = VerticalTextGrid.FitWithin(
            columnRoom: 24, columnLength: 120, maxWidth: 400,
            preferredCellSize: 24, minCellSize: 8.5, characterCount: 7);

        Assert.True(cellSize < 24, $"cell stayed at {cellSize:0.##}px rather than fitting the column");
        Assert.True(
            width <= 24 + 0.001,
            $"the grid took {width:0.##}px across a 24px source column");
    }

    [Fact]
    public void Every_character_of_a_vertical_translation_is_drawn()
    {
        const string translated = "說得也是今天過得還好嗎";

        var drawn = Draw(translated);

        Assert.Equal(translated, string.Concat(drawn.Glyphs));
    }

    /// <summary>
    /// Whitespace is dropped rather than given a cell. A blank square mid-column does not read as a
    /// word break in vertical writing.
    /// </summary>
    [Fact]
    public void Spaces_do_not_take_a_cell_of_their_own()
    {
        var drawn = Draw("Are you well");

        Assert.Equal("Areyouwell", string.Concat(drawn.Glyphs));
    }

    [Fact]
    public void A_vertical_column_runs_down_then_one_column_left()
    {
        var drawn = Draw("說得也是今天過得還好嗎這樣安排可以嗎");

        var first = drawn.Cells[0];
        var second = drawn.Cells[1];

        Assert.Equal(first.Left, second.Left, 1);
        Assert.True(second.Top > first.Top, "the second character is expected below the first");

        // Where the first column ends, the next one starts to the left of it.
        var rows = drawn.Cells.Count(cell => Math.Abs(cell.Left - first.Left) < 0.5);
        Assert.True(rows < drawn.Cells.Count, "this translation is expected to need a second column");

        var nextColumn = drawn.Cells[rows];
        Assert.True(
            nextColumn.Left < first.Left,
            $"the second column sits at {nextColumn.Left:0.#}, not left of {first.Left:0.#}");
        Assert.Equal(first.Top, nextColumn.Top, 1);
    }

    /// <summary>
    /// The anchor along the writing: the reader is already looking at the top of the rightmost
    /// column, because that is where the source sentence began.
    /// </summary>
    [Fact]
    public void A_vertical_grid_starts_where_the_source_did()
    {
        var drawn = Draw("說得也是");

        var first = drawn.Cells[0];

        Assert.Equal(SourceColumn.Right, first.Right, 1);
        Assert.Equal(SourceColumn.Top, first.Top, 1);
    }

    /// <summary>
    /// The anchor across the writing, which is the other answer: slack is spent on both sides.
    /// </summary>
    /// <remarks>
    /// A three-column balloon whose translation needs two leaves one column of slack. Hung off the
    /// right edge it slides the whole block a column away from the writing it replaces — and away
    /// by a different amount for every balloon on the page, which is what read as everything
    /// drifting towards the top right.
    /// </remarks>
    [Fact]
    public void A_grid_narrower_than_its_source_is_centred_across_it()
    {
        // Three columns' worth of source, holding a translation that fills two of them.
        var balloon = new Rect(360, 40, SourceGlyphSize * 3, SourceGlyphSize * 4);

        var drawn = Draw("說得也是今天過得", balloon);

        var left = drawn.Cells.Min(cell => cell.Left);
        var right = drawn.Cells.Max(cell => cell.Right);

        Assert.Equal(2, drawn.Cells.Select(cell => Math.Round(cell.Left, 1)).Distinct().Count());
        Assert.Equal(
            balloon.Left + balloon.Right,
            left + right,
            1);
    }

    [Fact]
    public void A_long_translation_stays_inside_the_block()
    {
        var drawn = Draw(string.Concat(Enumerable.Repeat("這樣安排可以嗎", 40)));

        double blockWidth = ColumnBlock.Width;
        double blockHeight = ColumnBlock.Height;

        foreach (var cell in drawn.Cells)
        {
            Assert.True(cell.Left >= -0.5, $"a cell starts at {cell.Left:0.#}, left of the block");
            Assert.True(cell.Top >= -0.5, $"a cell starts at {cell.Top:0.#}, above the block");
            Assert.True(
                cell.Right <= blockWidth + 0.5,
                $"a cell reaches {cell.Right:0.#}, past the block's {blockWidth}px");
            Assert.True(
                cell.Bottom <= blockHeight + 0.5,
                $"a cell reaches {cell.Bottom:0.#}, past the block's {blockHeight}px");
        }
    }

    /// <summary>
    /// The column the translation is set in is as long as the one it replaces, not as long as the
    /// block.
    /// </summary>
    /// <remarks>
    /// The block over a comic page is the page, so a grid allowed the block's height puts every
    /// sentence into one column and runs it from the top of the picture to the bottom — past the
    /// balloon it came from and across whatever is drawn underneath. What a translation too long for
    /// its source takes instead is another column beside it.
    /// </remarks>
    [Fact]
    public void A_column_stays_as_long_as_the_source_and_takes_another_column_instead()
    {
        // A short balloon, high up a tall block: three source characters' worth of column length.
        var balloon = new Rect(400, 40, 34, 108);

        var drawn = Draw("說得也是今天過得還好嗎這樣安排可以嗎", balloon);

        var bottom = drawn.Cells.Max(cell => cell.Bottom);
        Assert.True(
            bottom <= balloon.Bottom + SourceGlyphSize,
            $"the column runs to {bottom:0.#}, well past the source's {balloon.Bottom:0.#}");

        var columns = drawn.Cells.Select(cell => Math.Round(cell.Left, 1)).Distinct().Count();
        Assert.True(columns > 1, "a translation this long is expected to take more than one column");
    }

    /// <summary>The background still covers the source it is there to hide.</summary>
    [Fact]
    public void The_background_covers_the_source_column()
    {
        var drawn = Draw("是");

        Assert.True(drawn.Background.Left <= SourceColumn.Left + 0.5);
        Assert.True(drawn.Background.Top <= SourceColumn.Top + 0.5);
        Assert.True(drawn.Background.Right >= SourceColumn.Right - 0.5);
        Assert.True(drawn.Background.Bottom >= SourceColumn.Bottom - 0.5);
    }

    // Plain values, not the elements themselves: those belong to the STA thread that built them
    // and cannot be touched once it has ended.
    /// <summary>
    /// The strips a region watches between recognitions turn with the writing too: in vertical text
    /// the next line arrives beside the column, never under it.
    /// </summary>
    /// <remarks>
    /// Both halves matter. Reaching sideways is what makes a second column noticed the moment it
    /// appears; NOT reaching along the column is what keeps the fingerprint meaningful, since it is
    /// an average over the strip and a strip mostly made of untouched picture cannot tell a changed
    /// line from a still one.
    /// </remarks>
    [Fact]
    public void Watch_strips_reach_across_the_writing_and_not_along_it()
    {
        var down = new RealtimeRegionState(RealtimeTextOrientation.Vertical);
        down.MarkRendered([new System.Drawing.Rectangle(400, 40, 34, 300)], Blank, "たてがき");

        Assert.True(down.IsInsideWatchedText(new Rect(366, 40, 34, 300)), "the next column left");
        Assert.False(down.IsInsideWatchedText(new Rect(400, -260, 34, 300)), "far above the column");

        var across = new RealtimeRegionState();
        across.MarkRendered([new System.Drawing.Rectangle(40, 400, 300, 34)], Blank, "yokogaki");

        Assert.True(across.IsInsideWatchedText(new Rect(40, 434, 300, 34)), "the next line below");
        Assert.False(across.IsInsideWatchedText(new Rect(-260, 400, 300, 34)), "far left of the line");
    }

    // The strips are what is under test, not the picture in them.
    private static FrameFingerprint Blank(IReadOnlyList<System.Drawing.Rectangle>? bands) =>
        new(new byte[100]);

    private readonly record struct DrawnColumn(
        IReadOnlyList<string> Glyphs, IReadOnlyList<Rect> Cells, Rect Background);

    /// <summary>
    /// Builds the block window for real with one vertical source column in it, and hands back where
    /// each glyph landed in the block's own coordinates.
    /// </summary>
    private static DrawnColumn Draw(string translated, Rect? source = null) =>
        OnStaThread(() =>
        {
            var column = source ?? SourceColumn;

            // 進階選項 off, as in the horizontal fitting tests: this is about where the glyphs land,
            // not about what is drawn behind them.
            var window = new RealtimeBlockWindow(
                0, ColumnBlock, _ => null, "JA", "ZH-TW",
                RealtimeSubtitleColors.DefaultText,
                RealtimeSubtitleColors.DefaultScrim,
                RealtimeSubtitleColors.DefaultScrimOpacity,
                orientation: RealtimeTextOrientation.Vertical);

            // Never shown, so nothing raises Loaded and the DPI stays at the 1.0 an unscaled display
            // would have given — the same arrangement OverlayTextFittingTests uses.
            typeof(RealtimeBlockWindow)
                .GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, true);

            window.SetLines([
                new TranslatedBlock("これはたてがき", translated, column, null, SourceGlyphSize)]);

            var canvas = (Canvas)window.FindName("TextCanvas");
            var container = Assert.IsType<Border>(Assert.Single(canvas.Children));
            var grid = Assert.IsType<Canvas>(container.Child);

            double originX = Canvas.GetLeft(container);
            double originY = Canvas.GetTop(container);

            var glyphs = grid.Children.Cast<TextBlock>().ToList();
            var cells = glyphs
                .Select(glyph => new Rect(
                    originX + Canvas.GetLeft(glyph),
                    originY + Canvas.GetTop(glyph),
                    glyph.Width,
                    glyph.Height))
                .ToList();

            var scrim = Assert.IsType<Border>(
                Assert.Single(((Canvas)window.FindName("ScrimCanvas")).Children));
            var background = new Rect(
                Canvas.GetLeft(scrim), Canvas.GetTop(scrim), scrim.Width, scrim.Height);

            return new DrawnColumn([.. glyphs.Select(glyph => glyph.Text)], cells, background);
        });

    /// <summary>
    /// Runs the work on a fresh STA thread, which WPF elements require and xunit's own worker is
    /// not. Nothing built there outlives the call, so no state reaches the rest of the suite.
    /// </summary>
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
