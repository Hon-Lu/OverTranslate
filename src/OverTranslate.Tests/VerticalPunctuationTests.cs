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
/// Where punctuation lands at a column break in vertical writing: a comma or full stop is hung below
/// a full column rather than heading the next, what may not head or end a column is pushed on, and
/// none of it ever costs a glyph, a column or the grid's place over the source.
/// </summary>
/// <remarks>
/// The grids here are 20px cells five rows deep unless a test says otherwise, so the sixth glyph is
/// the one that would head the second column.
/// </remarks>
public class VerticalPunctuationTests
{
    private const double Cell = 20;

    /// <summary>A live grid two columns wide and five rows deep, with room to hang below it.</summary>
    private static (double CellSize, double Width, double Height, VerticalTextGrid.Breaks Breaks) FitLive(
        string text, double columnRoom = 40, double columnLength = 100, double maxWidth = 400,
        double? hangingReach = 200) =>
        VerticalTextGrid.FitWithin(columnRoom, columnLength, maxWidth, Cell, 8.5, text, hangingReach);

    private static List<(char Glyph, Rect Cell)> Place(
        string text, (double CellSize, double Width, double Height, VerticalTextGrid.Breaks Breaks) grid) =>
        [.. VerticalTextGrid.Cells(text, new Rect(0, 0, grid.Width, grid.Height), grid.CellSize, grid.Breaks)];

    /// <summary>The glyphs of each column, right to left, as one string per column.</summary>
    private static List<string> Columns(IEnumerable<(char Glyph, Rect Cell)> cells) =>
        [.. cells
            .GroupBy(cell => Math.Round(cell.Cell.Left, 2))
            .OrderByDescending(column => column.Key)
            .Select(column => string.Concat(column.OrderBy(cell => cell.Cell.Top).Select(cell => cell.Glyph)))];

    [Fact]
    public void A_comma_that_would_head_the_second_column_hangs_below_the_first()
    {
        const string text = "一二三四五、六七八九";

        var grid = FitLive(text);
        var cells = Place(text, grid);

        Assert.True(grid.Breaks.HasFlag(VerticalTextGrid.Breaks.Hang));
        Assert.Equal(new[] { "一二三四五、", "六七八九" }, Columns(cells));

        // The hung mark is one cell under the first column, and the grid was measured with it.
        Assert.Equal(new Rect(20, 100, 20, 20), cells[5].Cell);
        Assert.Equal(2 * Cell, grid.Width, 3);
        Assert.Equal(6 * Cell, grid.Height, 3);
    }

    /// <summary>
    /// The case hanging is most often for: a sentence one full stop longer than its column. Hung, it
    /// stays one column wide, rather than opening a column for the full stop alone.
    /// </summary>
    /// <remarks>
    /// Two columns of room, so the cell is not shrunk first: the size is chosen on a character
    /// count, as it always was, and the hanging is decided on the grid that size gives.
    /// </remarks>
    [Fact]
    public void A_full_stop_after_a_full_column_hangs_rather_than_taking_a_column_of_its_own()
    {
        const string text = "一二三四五。";

        var grid = FitLive(text);
        var cells = Place(text, grid);

        Assert.Equal(new[] { "一二三四五。" }, Columns(cells));
        Assert.Equal(Cell, grid.Width, 3);
        Assert.Equal(6 * Cell, grid.Height, 3);
    }

    [Theory]
    [InlineData('。')]
    [InlineData('｡')]
    [InlineData('、')]
    [InlineData('､')]
    [InlineData('，')]
    [InlineData('︒')]
    [InlineData('︑')]
    [InlineData('︐')]
    public void Every_spelling_of_a_comma_or_full_stop_hangs(char mark)
    {
        string text = $"一二三四五{mark}六七";

        var cells = Place(text, FitLive(text));

        Assert.Equal(new[] { $"一二三四五{mark}", "六七" }, Columns(cells));
    }

    /// <summary>
    /// Only one mark hangs below a column. A second has a column head to go to, and may not take it,
    /// so the glyph before the pair is pushed on with both.
    /// </summary>
    [Fact]
    public void A_mark_followed_by_a_closing_bracket_pushes_the_glyph_before_it_on()
    {
        const string text = "一二三四五。」六七";

        var cells = Place(text, FitLive(text));

        Assert.Equal(new[] { "一二三四", "五。」六七" }, Columns(cells));
    }

    [Theory]
    [InlineData("一二三四五」六七", "一二三四", "五」六七")]
    [InlineData("一二三四五！六七", "一二三四", "五！六七")]
    [InlineData("一二三四五？六七", "一二三四", "五？六七")]
    [InlineData("一二三四五』六七", "一二三四", "五』六七")]
    [InlineData("一二三四五）六七", "一二三四", "五）六七")]
    [InlineData("一二三四五：六七", "一二三四", "五：六七")]
    [InlineData("一二三四五”六七", "一二三四", "五”六七")]
    public void What_may_not_head_a_column_takes_the_glyph_before_it_along(
        string text, string first, string second)
    {
        var grid = FitLive(text);
        var cells = Place(text, grid);

        Assert.Equal(new[] { first, second }, Columns(cells));
        Assert.False(grid.Breaks.HasFlag(VerticalTextGrid.Breaks.Hang));
        Assert.Equal(5 * Cell, grid.Height, 3);
    }

    [Theory]
    [InlineData("一二三四「五六」七", "一二三四", "「五六」七")]
    [InlineData("一二三四『五六』七", "一二三四", "『五六』七")]
    [InlineData("一二三四（五六）七", "一二三四", "（五六）七")]
    [InlineData("一二三四“五六”七", "一二三四", "“五六”七")]
    public void An_opening_bracket_does_not_end_a_column(string text, string first, string second)
    {
        var cells = Place(text, FitLive(text));

        Assert.Equal(new[] { first, second }, Columns(cells));
    }

    /// <summary>A run of closing marks takes as many glyphs on as it has to, within the cap.</summary>
    [Fact]
    public void A_run_of_closing_marks_is_pushed_on_whole()
    {
        // Seven rows, so up to three glyphs may be pushed on. 」 would head the second column, and
        // neither ？ nor ！ before it may either.
        const string text = "一二三四五六七！？」八";

        var cells = Place(text, FitLive(text, columnLength: 140));

        Assert.Equal(new[] { "一二三四五六", "七！？」八" }, Columns(cells));

        const string run = "一二三四五！？」八";

        Assert.Equal(new[] { "一二三四", "五！？」八" }, Columns(Place(run, FitLive(run, columnLength: 140))));
    }

    [Fact]
    public void An_ellipsis_is_not_split_between_columns()
    {
        const string text = "一二三四……五六";

        var cells = Place(text, FitLive(text));

        Assert.Equal(new[] { "一二三四", "……五六" }, Columns(cells));
    }

    /// <summary>
    /// The user's call on what gives when the rules do not fit: the punctuation stays at the column
    /// head, as it always did, rather than a glyph or a column being lost to them.
    /// </summary>
    [Fact]
    public void A_push_that_would_need_another_column_leaves_the_break_where_it_was()
    {
        // Two full columns: pushing 五 on runs the second column into a third.
        const string text = "一二三四五」六七八九";

        var grid = FitLive(text, maxWidth: 40);
        var cells = Place(text, grid);

        Assert.Equal(new[] { "一二三四五", "」六七八九" }, Columns(cells));
        Assert.Equal(2 * Cell, grid.Width, 3);
    }

    /// <summary>
    /// A comma that cannot hang still may not head the column — but pushing is the rule that costs
    /// room, and with none to spare the comma heads it as before.
    /// </summary>
    [Fact]
    public void A_comma_with_no_room_to_hang_below_the_grid_is_left_where_it_was()
    {
        const string text = "一二三四五、六七八九";

        // The hanging row would end at 120px; the block ends at 110.
        var grid = FitLive(text, hangingReach: 110);
        var cells = Place(text, grid);

        Assert.False(grid.Breaks.HasFlag(VerticalTextGrid.Breaks.Hang));
        Assert.Equal(new[] { "一二三四五", "、六七八九" }, Columns(cells));
        Assert.Equal(5 * Cell, grid.Height, 3);
    }

    /// <summary>With room to push and none to hang, the comma is kept off the head by a push.</summary>
    [Fact]
    public void A_comma_with_no_room_to_hang_is_pushed_on_if_that_fits()
    {
        const string text = "一二三四五、六七";

        var grid = FitLive(text, hangingReach: null);
        var cells = Place(text, grid);

        Assert.Equal(new[] { "一二三四", "五、六七" }, Columns(cells));
        Assert.Equal(5 * Cell, grid.Height, 3);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Columns_shorter_than_three_cells_are_broken_where_they_fill(int rows)
    {
        string text = new string('字', rows) + "、" + new string('字', rows) + "」字";

        var grid = FitLive(text, columnRoom: 400, columnLength: rows * Cell);
        var cells = Place(text, grid);

        Assert.Equal(VerticalTextGrid.Breaks.None, grid.Breaks);
        Assert.Equal(rows * Cell, grid.Height, 3);
        Assert.Equal('、', Columns(cells)[1][0]);
    }

    [Fact]
    public void A_single_column_is_left_alone()
    {
        const string text = "一、二」三「";

        var grid = FitLive(text, columnRoom: 20, columnLength: 200);
        var cells = Place(text, grid);

        Assert.Equal(new[] { text }, Columns(cells));
        Assert.Equal(text.Length * Cell, grid.Height, 3);
    }

    [Fact]
    public void Columns_that_break_cleanly_are_left_alone()
    {
        const string text = "一二三四五六七八九十";

        var grid = FitLive(text);
        var cells = Place(text, grid);

        Assert.Equal(new[] { "一二三四五", "六七八九十" }, Columns(cells));
        Assert.Equal(VerticalTextGrid.Breaks.Push, grid.Breaks);
        Assert.Equal(5 * Cell, grid.Height, 3);
    }

    public static TheoryData<string> Sentences => new()
    {
        "一二三四五、六七八九",
        "一二三四五。」六七八九十一二三、四",
        "「一二三」、四五六七「八」九十。",
        "一、二、三、四、五、六、七、八、九、十、",
        "！？」」」、。，一二三四五六七八九「「「",
        "一二三四……五六七——八九十。",
        "這樣安排可以嗎？「說得也是。」今天過得還好嗎，我想是的。",
        "そうですね、今日は「いい天気」ですね。本当に！",
        "Are,you.well?(yes)",
    };

    /// <summary>
    /// What every grid owes its caller, whatever the punctuation: each glyph placed once, in reading
    /// order, in a cell of its own, and every cell inside the size the fit returned — the size the
    /// live overlay cuts its grid to, past which a glyph is clipped away.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sentences))]
    public void Every_glyph_is_placed_once_inside_the_live_grid(string text)
    {
        foreach (var (room, length, width, reach) in new (double, double, double, double?)[]
                 {
                     (40, 100, 400, 200), (40, 100, 400, null), (60, 60, 60, 80), (20, 140, 400, 400),
                     (40, 100, 40, 200), (100, 80, 100, 100),
                 })
        {
            var grid = FitLive(text, room, length, width, reach);
            var cells = Place(text, grid);

            int rows = (int)Math.Floor(length / grid.CellSize);
            int columns = (int)Math.Floor(width / grid.CellSize);
            if (rows * columns >= text.Length)
                Assert.Equal(text.Length, cells.Count);

            AssertSound(text, cells, new Rect(0, 0, grid.Width, grid.Height));
            Assert.True(grid.Width <= width + 0.01, $"{grid.Width} wide against {width}");
            Assert.True(grid.Height <= Math.Max(length, reach ?? 0) + 0.01, $"{grid.Height} tall");
        }
    }

    /// <summary>
    /// The screenshot overlay's grid: the same promises, on the whole bubble's width and the height
    /// <see cref="VerticalTextGrid.Fit"/> grew it to.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sentences))]
    public void Every_glyph_is_placed_once_inside_the_screenshot_grid(string text)
    {
        foreach (var (width, height, reach) in new (double, double, double?)[]
                 {
                     (40, 100, 200), (40, 100, null), (80, 100, 120), (20, 60, 400), (60, 30, 30),
                 })
        {
            var grid = VerticalTextGrid.Fit(width, height, Cell, 9.5, 7, text, reach);
            var bounds = new Rect(0, 0, width, grid.Height);
            var cells = VerticalTextGrid.Cells(text, bounds, grid.CellSize, grid.Breaks).ToList();

            Assert.Equal(text.Length, cells.Count);
            AssertSound(text, cells, bounds);
            if (grid.Breaks.HasFlag(VerticalTextGrid.Breaks.Hang))
                Assert.True(grid.Height <= reach + 0.01, $"{grid.Height} tall against a reach of {reach}");
        }
    }

    [Fact]
    public void The_screenshot_grid_grows_by_the_hanging_row_and_no_more()
    {
        const string text = "一二三四五、六七八九";

        var grid = VerticalTextGrid.Fit(40, 100, Cell, 9.5, 7, text, hangingReach: 200);
        var cells = VerticalTextGrid.Cells(text, new Rect(0, 0, 40, grid.Height), grid.CellSize, grid.Breaks);

        Assert.Equal(6 * Cell, grid.Height, 3);
        Assert.Equal(new[] { "一二三四五、", "六七八九" }, Columns(cells));
    }

    [Fact]
    public void The_screenshot_grid_does_not_hang_past_its_reach()
    {
        const string text = "一二三四五、六七八九";

        var grid = VerticalTextGrid.Fit(40, 100, Cell, 9.5, 7, text, hangingReach: 100);
        var cells = VerticalTextGrid.Cells(text, new Rect(0, 0, 40, grid.Height), grid.CellSize, grid.Breaks);

        Assert.Equal(5 * Cell, grid.Height, 3);
        Assert.Equal(new[] { "一二三四五", "、六七八九" }, Columns(cells));
    }

    /// <summary>
    /// The rules are asked of the translation's own character, and the cell may draw another for it:
    /// 。 as ︒ for Japanese, “ as 「 for Chinese. Each class holds both, so the answer is the same for
    /// what is in the text and what is on screen, whatever the target.
    /// </summary>
    [Fact]
    public void A_glyph_and_what_is_drawn_for_it_fall_under_the_same_rules_in_every_target()
    {
        const string glyphs = "、。，､｡︑︒︐」』）！？：；・「『（“”‘’…⋯—一字A,.";

        foreach (var target in new[] { "JA", "ZH-HANS", "ZH-HANT", "KO", "EN" })
        foreach (var glyph in glyphs)
        {
            var drawn = VerticalTextGrid.VerticalGlyphFor(glyph, target).Glyph;
            Assert.Equal(VerticalTextGrid.HangsBelowColumn(glyph), VerticalTextGrid.HangsBelowColumn(drawn));
            Assert.Equal(VerticalTextGrid.KeptFromColumnStart(glyph), VerticalTextGrid.KeptFromColumnStart(drawn));
            Assert.Equal(VerticalTextGrid.KeptFromColumnEnd(glyph), VerticalTextGrid.KeptFromColumnEnd(drawn));
        }
    }

    /// <summary>
    /// The live overlay for real, in each target: the comma is drawn under the first column, as the
    /// glyph that target draws for it, inside the text layer the grid is clipped to.
    /// </summary>
    [Theory]
    [InlineData("JA")]
    [InlineData("ZH-HANS")]
    [InlineData("ZH-HANT")]
    [InlineData("KO")]
    public void The_live_overlay_draws_the_hung_comma_under_its_column(string target)
    {
        const string translated = "說得也是今、天過得還";

        // Two columns of source, five 34px characters long.
        var drawn = Draw(translated, new Rect(400, 40, 68, 170), target);

        Assert.Equal(
            translated.Select(glyph => VerticalTextGrid.VerticalGlyphFor(glyph, target).Glyph.ToString()),
            drawn.Glyphs);

        var lastOfColumn = drawn.Cells[4];
        var comma = drawn.Cells[5];
        Assert.Equal(lastOfColumn.Left, comma.Left, 1);
        Assert.Equal(lastOfColumn.Bottom, comma.Top, 1);
        Assert.Equal(40, drawn.Cells[0].Top, 1);

        Assert.True(comma.Bottom <= drawn.Layer.Bottom + 0.5, $"the comma ends at {comma.Bottom:0.#}, past {drawn.Layer.Bottom:0.#}");
        Assert.True(drawn.Background.Bottom >= comma.Bottom - 0.5, "the background stops short of the comma");
    }

    private static void AssertSound(string text, IReadOnlyList<(char Glyph, Rect Cell)> cells, Rect bounds)
    {
        Assert.Equal(text[..cells.Count], string.Concat(cells.Select(cell => cell.Glyph)));
        Assert.Equal(cells.Count, cells.Select(cell => (Math.Round(cell.Cell.X, 2), Math.Round(cell.Cell.Y, 2))).Distinct().Count());
        foreach (var (glyph, cell) in cells)
        {
            Assert.True(
                cell.Left >= bounds.Left - 0.01 && cell.Top >= bounds.Top - 0.01
                && cell.Right <= bounds.Right + 0.01 && cell.Bottom <= bounds.Bottom + 0.01,
                $"{glyph} at {cell} is outside {bounds}");
        }
    }

    private static readonly System.Drawing.Rectangle Block = new(300, 100, 600, 400);

    private readonly record struct DrawnColumn(
        IReadOnlyList<string> Glyphs, IReadOnlyList<Rect> Cells, Rect Layer, Rect Background);

    /// <summary>
    /// The live block window built for real, as <see cref="RealtimeVerticalTextTests"/> builds it, with
    /// where each glyph, the clipped text layer and the background landed in the block's coordinates.
    /// </summary>
    private static DrawnColumn Draw(string translated, Rect source, string target) =>
        OnStaThread(() =>
        {
            var window = new RealtimeBlockWindow(
                0, Block, _ => null, "JA", target,
                RealtimeSubtitleColors.DefaultText,
                RealtimeSubtitleColors.DefaultScrim,
                RealtimeSubtitleColors.DefaultScrimOpacity,
                orientation: RealtimeTextOrientation.Vertical);

            typeof(RealtimeBlockWindow)
                .GetField("_isLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(window, true);

            window.SetLines([new TranslatedBlock("これはたてがき", translated, source, null, 34)]);

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

            return new DrawnColumn(
                [.. glyphs.Select(glyph => glyph.Text)],
                cells,
                new Rect(originX, originY, container.Width, container.Height),
                new Rect(Canvas.GetLeft(scrim), Canvas.GetTop(scrim), scrim.Width, scrim.Height));
        });

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
