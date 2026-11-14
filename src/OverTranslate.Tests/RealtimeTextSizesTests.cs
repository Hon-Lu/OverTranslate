using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using OverTranslate.Views.Realtime;
using Xunit;
using Reading = OverTranslate.Layout.RealtimeTextSizes.Reading;

namespace OverTranslate.Tests;

/// <summary>
/// 自動's type size holds still: lines that are one size on screen are drawn at one size, whatever
/// height each of them was read at.
/// </summary>
/// <remarks>
/// The rules are checked on bare heights, the session on the heights a real one reported (see the
/// fixture's own header), and the window is built only to see that what it draws is what the rules
/// said — a size that is steady in a list and not on the canvas has fixed nothing.
/// </remarks>
public class RealtimeTextSizesTests
{
    [Fact]
    public void Lines_of_one_typeface_read_at_different_heights_are_given_one_size()
    {
        // Three lines of one sentence, on screen together, as one frame of the session reported them.
        var settled = new RealtimeTextSizes().Settle([Long("a", 31.2), Long("b", 28.7), Long("c", 35.6)]);

        Assert.Single(settled.Distinct());
        Assert.Equal(31.2, settled[0]);
    }

    [Fact]
    public void A_line_read_again_at_another_height_keeps_the_size_it_was_drawn_at()
    {
        var sizes = Warmed(32.4);
        double first = sizes.Settle([Long("again", 30.3)])[0];
        double second = sizes.Settle([Long("again", 35.3)])[0];

        Assert.Equal(first, second);
        Assert.Equal(32.4, second, 1);
    }

    [Fact]
    public void A_sentence_left_on_screen_is_one_vote_however_often_it_is_read()
    {
        var sizes = Warmed(32.4);
        for (int reading = 0; reading < 200; reading++)
            sizes.Settle([Long("held", 27.1)]);

        // Two hundred readings of one low line, and the size is where the other lines put it.
        Assert.Equal(32.4, sizes.Settle([Long("next", 33.6)])[0], 1);
    }

    [Fact]
    public void Sizes_that_really_differ_stay_apart()
    {
        // The block the learned yardstick failed on: a chat log in the corner of a game. 13px chat
        // must not make 55px subtitles small, nor the other way round.
        var sizes = new RealtimeTextSizes();
        for (int line = 0; line < 30; line++)
            sizes.Settle([Long($"chat{line}", 13 + line % 3 * 0.6), Long($"sub{line}", 55 + line % 5 - 2)]);

        var settled = sizes.Settle([Long("chat", 13.4), Long("subtitle", 56.2)]);
        Assert.InRange(settled[0], 12.5, 14.5);
        Assert.InRange(settled[1], 53, 58);
    }

    [Fact]
    public void A_line_outside_every_size_is_drawn_as_it_was_read()
    {
        var sizes = Warmed(32.4);

        Assert.Equal(64, sizes.Settle([Long("title", 64)])[0]);
        Assert.Equal(14, sizes.Settle([Long("footnote", 14)])[0]);
        // A short line too: twice the dialogue is past what a short line is ever misread by.
        Assert.Equal(70, Warmed(32.4).Settle([Short("勝利！", 70)])[0]);
    }

    [Fact]
    public void A_short_line_is_drawn_at_the_size_of_the_long_lines_round_it()
    {
        // 「え」 came back 45.9 where its sentence's neighbours were 32-odd.
        var sizes = Warmed(32.4);

        Assert.Equal(32.4, sizes.Settle([Short("「え」", 45.9)])[0], 1);
        // As far out as the application has been seen to read one: 50 where the sentences were 27.7.
        Assert.Equal(32.4, sizes.Settle([Short("「うん。」", 32.4 * 1.8)])[0], 1);
        // In one frame with a long line too, before the block has seen anything.
        var together = new RealtimeTextSizes().Settle([Short("うん。", 46.7), Long("long", 33.6)]);
        Assert.Equal(33.6, together[0]);
    }

    [Fact]
    public void A_short_line_has_no_say_in_the_size_of_the_long_ones()
    {
        var sizes = Warmed(32.4);
        for (int line = 0; line < 60; line++)
            sizes.Settle([Short($"short{line}", 41)]);

        // Left to vote, sixty short lines at 41 are a majority of the forty the size remembers.
        Assert.Equal(32.4, sizes.Settle([Long("next", 36.5)])[0], 1);
    }

    [Fact]
    public void A_short_line_with_a_glyph_height_of_its_own_reaches_no_further_than_a_long_one()
    {
        // A Latin source's short lines are corrected where they are measured; a heading 1.4 times
        // the body is a heading.
        var sizes = Warmed(14);

        Assert.Equal(19.6, sizes.Settle([new Reading("Menu", 19.6, 4, FromRowBox: false)])[0]);
        Assert.Equal(14, sizes.Settle([new Reading("Menu", 19.6, 4, FromRowBox: true)])[0], 1);
    }

    [Fact]
    public void A_block_of_nothing_but_short_lines_still_settles()
    {
        // A menu: every item two or three characters, nothing longer to go by.
        var sizes = new RealtimeTextSizes();
        double[] heights = [22.1, 19.7, 24.6, 21.3, 23.0, 20.5];
        for (int pass = 0; pass < 3; pass++)
            sizes.Settle([.. heights.Select((height, item) => Short($"item{item}", height))]);

        var settled = sizes.Settle([.. heights.Select((height, item) => Short($"item{item}", height))]);
        Assert.Single(settled.Distinct());
        Assert.InRange(settled[0], 20.5, 23);
    }

    [Fact]
    public void A_size_follows_its_lines_once_they_have_plainly_moved()
    {
        // The game changes its dialogue type by a tenth: inside the reach, so the lines stay filed
        // under the same size, and the size goes with them once they are most of what it remembers.
        var sizes = Warmed(32.4);
        double[] spread = [-1.6, 0, 1.6, -0.8, 0.8];
        double last = 0;
        for (int line = 0; line < RealtimeTextSizes.Memory; line++)
            last = sizes.Settle([Long($"smaller{line}", 29 + spread[line % spread.Length])])[0];

        Assert.InRange(last, 28.2, 29.8);
    }

    [Fact]
    public void Only_so_many_sizes_are_kept_and_the_ones_in_use_are_not_the_ones_dropped()
    {
        var sizes = Warmed(32.4);
        for (int size = 0; size < 20; size++)
        {
            // A new size every pass, twice the last, with the dialogue still on screen.
            var settled = sizes.Settle([Long($"other{size}", 100 * Math.Pow(2, size)), Long($"line{size}", 33.6)]);
            Assert.Equal(32.4, settled[1], 1);
        }
    }

    [Fact]
    public void What_is_not_a_height_is_handed_back_untouched()
    {
        var settled = Warmed(32.4).Settle([Long("nan", double.NaN), Long("zero", 0), Long("fine", 33)]);

        Assert.True(double.IsNaN(settled[0]));
        Assert.Equal(0, settled[1]);
        Assert.Equal(32.4, settled[2], 1);
    }

    [Fact]
    public void A_dialogue_session_settles_on_one_size_and_stays_there()
    {
        var session = Session();
        var sizes = new RealtimeTextSizes();

        var drawn = new List<(double Read, double Settled, int Frame)>();
        for (int frame = 0; frame < session.Count; frame++)
        {
            var settled = sizes.Settle(session[frame]);
            for (int line = 0; line < settled.Length; line++)
                if (IsDialogue(session[frame][line]))
                    drawn.Add((session[frame][line].Height, settled[line], frame));
        }

        // Line by line, as 自動 used to size them: most lines a different size from the one before.
        double readChanging = Changing(drawn.Select(line => line.Read));
        Assert.True(readChanging > 0.6, $"read: {readChanging:P0} of lines change size");

        double settledChanging = Changing(drawn.Select(line => line.Settled));
        // 75.0% and 1.5% when this was written.
        Assert.True(settledChanging < 0.03, $"settled: {settledChanging:P1} of lines change size");

        // And once the first frames have found the size, it is the size: the one typeface this
        // session held is drawn at 32.4 and, after the median has moved the once, at 33.8 — a
        // step of 4% in 329 frames — with two lines in the whole of it anywhere else.
        var later = drawn.Where(line => line.Frame >= 15).Select(line => Math.Round(line.Settled, 1)).ToList();
        var usual = later.GroupBy(size => size).MaxBy(group => group.Count())!;
        Assert.InRange(usual.Key, 31, 34);
        int beside = later.Count(size => Math.Max(size / usual.Key, usual.Key / size) <= 1.05);
        Assert.True(beside >= later.Count - 2,
            $"{beside} of {later.Count} lines within 5% of {usual.Key}: " +
            string.Join(" ", later.GroupBy(size => size).Select(group => $"{group.Key}x{group.Count()}")));
    }

    [Fact]
    public void The_short_lines_of_that_session_are_drawn_with_its_long_ones()
    {
        var session = Session();
        var sizes = new RealtimeTextSizes();

        var shortLines = new List<(double Read, double Settled)>();
        for (int frame = 0; frame < session.Count; frame++)
        {
            var settled = sizes.Settle(session[frame]);
            if (frame < 15) continue;
            for (int line = 0; line < settled.Length; line++)
                if (session[frame][line] is { Glyphs: >= 3 and < RealtimeTextSizes.ReliableFromGlyphs, Height: > 30 } reading)
                    shortLines.Add((reading.Height, settled[line]));
        }

        Assert.True(shortLines.Count >= 10, $"{shortLines.Count} short dialogue lines in the session");
        // Read a fifth taller than the sentences round them...
        Assert.True(shortLines.Average(line => line.Read) > 38, $"read at {shortLines.Average(line => line.Read):0.0}");
        // ...and nearly all of them drawn at those sentences' size.
        int together = shortLines.Count(line => line.Settled is > 31 and < 34.5);
        Assert.True(together >= shortLines.Count * 0.85, $"{together} of {shortLines.Count} at the dialogue's size");
    }

    [Fact]
    public void A_line_sized_from_its_box_gets_the_settled_height_round_its_own_centre()
    {
        var sizes = Warmed(32.4);
        var line = new TranslatedBlock("どんな傾向の映画が流行っているのか", "譯文", new Rect(20, 64.2, 530, 28.7));

        var settled = Assert.Single(sizes.Settle([line], vertical: false));

        Assert.Equal(32.4, settled.Bounds.Height, 1);
        Assert.Equal(line.Bounds.Y + line.Bounds.Height / 2, settled.Bounds.Y + settled.Bounds.Height / 2, 6);
        Assert.Equal(line.Bounds.X, settled.Bounds.X);
        Assert.Equal(line.Bounds.Width, settled.Bounds.Width);
    }

    [Fact]
    public void The_rows_of_a_paragraph_come_out_one_height_and_the_paragraph_stays_where_it_was()
    {
        var sizes = Warmed(32.4);
        Rect[] rows = [new(0, 14.4, 568, 31.2), new(0, 64.2, 530, 28.7), new(0, 104.2, 483, 35.6)];
        var paragraph = new TranslatedBlock(
            "こういうところにもヒントはあるらしい。どんな傾向の映画が流行っているのか", "譯文",
            rows.Aggregate(Rect.Union), rows);

        var settled = Assert.Single(sizes.Settle([paragraph], vertical: false));

        Assert.All(settled.SourceLineBounds!, row => Assert.Equal(32.4, row.Height, 1));
        Assert.Equal(rows.Select(row => row.Y + row.Height / 2),
            settled.SourceLineBounds!.Select(row => Math.Round(row.Y + row.Height / 2, 6)));
        Assert.Equal(paragraph.Bounds, settled.Bounds);
    }

    [Fact]
    public void A_paragraph_whose_median_is_already_the_size_still_has_its_rows_evened()
    {
        // Rows of 28.7 and 36.1 round a median of 32.4: nothing about the paragraph's own height
        // moves, and a panel would still set all of it by the 28.7.
        var sizes = Warmed(32.4);
        Rect[] rows = [new(0, 14, 568, 28.7), new(0, 60, 530, 36.1)];
        var paragraph = new TranslatedBlock(
            "こういうところにもヒントはあるらしい。どんな傾向の映画が流行っているのか", "譯文",
            rows.Aggregate(Rect.Union), rows);

        var settled = Assert.Single(sizes.Settle([paragraph], vertical: false));

        Assert.All(settled.SourceLineBounds!, row => Assert.Equal(32.4, row.Height, 1));
    }

    [Fact]
    public void A_line_with_a_glyph_height_takes_the_settled_one_for_its_font_alone()
    {
        var sizes = new RealtimeTextSizes();
        for (int line = 0; line < 10; line++)
            sizes.Settle([Latin($"A line of English subtitle {line}", 44)], vertical: false);

        var read = Latin("Another line of English subtitle", 39.5);
        var settled = Assert.Single(sizes.Settle([read], vertical: false));

        Assert.Equal(44, settled.FontGlyphHeight);
        // What the line is covered by is what was read.
        Assert.Equal(39.5, settled.RenderGlyphHeight);
        // Its box moved by as much as its glyphs did.
        Assert.Equal(read.Bounds.Height * 44 / 39.5, settled.Bounds.Height, 6);
    }

    [Fact]
    public void Lines_this_has_no_business_with_come_back_as_they_went_in()
    {
        var sizes = Warmed(32.4);
        var column = new TranslatedBlock("縦書きの台詞がここに入る", "譯文", new Rect(300, 20, 28.7, 240));
        var plate = new TranslatedBlock("横書きの名札がここにある", "譯文", new Rect(20, 20, 300, 28.7)) { RunsAcross = true };
        var empty = new TranslatedBlock("まだ訳されていない台詞です", "", new Rect(20, 80, 300, 28.7));
        var steady = new TranslatedBlock("ちょうど同じ高さで読まれた行", "譯文", new Rect(20, 140, 300, 32.4));

        // On a vertical page only what runs across is sized.
        var page = sizes.Settle([column, plate], vertical: true);
        Assert.Same(column, page[0]);
        Assert.NotSame(plate, page[1]);
        Assert.Equal(32.4, page[1].Bounds.Height, 1);

        var across = sizes.Settle([empty, steady], vertical: false);
        Assert.Same(empty, across[0]);
        Assert.Same(steady, across[1]);

        // Nothing moved: the list itself, not a copy of it.
        IReadOnlyList<TranslatedBlock> untouched = [steady];
        Assert.Same(untouched, sizes.Settle(untouched, vertical: false));
    }

    [Fact]
    public void A_subtitle_block_draws_the_lines_of_one_sentence_at_one_size() => OnSta(() =>
    {
        // One frame of the session, three lines of one sentence: read 31.2, 28.7 and 35.6.
        var drawn = Draw(RealtimeBlockMode.Subtitle,
        [
            [
                new("こういうところにもヒントはあるらしい。", "看來這種地方也有線索。", new Rect(20, 14.4, 568, 31.2)),
                new("どんな傾向の映画が流行っているのか", "流行的是什麼樣的電影", new Rect(20, 64.2, 530, 28.7)),
                new("どんなキャラが流行っているのか。", "流行的是什麼樣的角色。", new Rect(20, 104.2, 483, 35.6)),
            ],
        ]);

        Assert.Equal(3, drawn[0].Count);
        Assert.Single(drawn[0].Distinct());
    });

    [Fact]
    public void A_subtitle_block_keeps_its_size_from_one_sentence_to_the_next() => OnSta(() =>
    {
        double[] heights = [33.6, 28.7, 36.1, 31.2, 35.3, 27.1, 34.4, 32.0, 36.4, 29.5, 33.6, 30.3];
        var frames = heights.Select((height, line) => (IReadOnlyList<TranslatedBlock>)
        [
            new($"これは台詞の{line}行目として読まれた文です。", $"這是第{line}句台詞的譯文", new Rect(20, 60, 640, height)),
        ]).ToList();

        var drawn = Draw(RealtimeBlockMode.Subtitle, frames).Select(frame => Assert.Single(frame)).ToList();

        // The first few are the size still being found; from there every sentence is drawn alike,
        // where sized one at a time they would run from 25.6 to 34.3.
        var later = drawn.Skip(4).ToList();
        Assert.True(later.Max() / later.Min() < 1.04, $"sizes {string.Join(", ", later.Select(size => size.ToString("0.0")))}");
    });

    [Fact]
    public void A_sentence_of_three_rows_is_drawn_at_the_size_of_a_sentence_of_one() => OnSta(() =>
    {
        // The same typeface, read at the same height: one row on its own, then three grouped under
        // one translation. The group used to be set a size up — 1.06 of its height against 0.94.
        Rect[] rows = [new(20, 14, 560, 32), new(20, 60, 530, 32), new(20, 106, 480, 32)];
        var drawn = Draw(RealtimeBlockMode.Subtitle,
        [
            [new("彼女は静かに本を閉じて、こちらを見た。", "她靜靜地闔上書，看向這邊。", new Rect(20, 60, 560, 32))],
            [
                new("駅前の喫茶店は思ったより混んでいて、窓際の席はひとつも空いていなかった。",
                    "車站前的咖啡廳比想像中擁擠，靠窗的位子一個也沒有。", rows.Aggregate(Rect.Union), rows),
            ],
        ]);

        Assert.Equal(Assert.Single(drawn[0]), Assert.Single(drawn[1]));
    });

    [Fact]
    public void A_panel_block_keeps_its_size_from_one_paragraph_to_the_next() => OnSta(() =>
    {
        // A panel sets a paragraph by its shortest row, so one low row used to shrink all of it.
        double[][] paragraphs =
        [
            [33.6, 32.8], [27.1, 33.6], [36.1, 30.3], [31.2, 32.8], [28.7, 35.6],
            [34.4, 39.3], [31.2, 35.3], [27.9, 28.7], [36.0, 32.0], [33.6, 33.6],
        ];
        var frames = paragraphs.Select((heights, paragraph) =>
        {
            Rect[] rows = [new(20, 20, 560, heights[0]), new(20, 65, 430, heights[1])];
            return (IReadOnlyList<TranslatedBlock>)
            [
                new($"これは段落{paragraph}の一行目として読まれた文で、二行目に続いている。", "這是第一行的譯文。這是第二行。",
                    rows.Aggregate(Rect.Union), rows),
            ];
        }).ToList();

        var drawn = Draw(RealtimeBlockMode.Panel, frames);

        var later = drawn.Skip(4).SelectMany(frame => frame).ToList();
        Assert.NotEmpty(later);
        Assert.True(later.Max() / later.Min() < 1.04, $"sizes {string.Join(", ", later.Select(size => size.ToString("0.0")))}");
    });

    [Fact]
    public void A_fixed_size_is_not_steadied_because_there_is_nothing_to_steady() => OnSta(() =>
    {
        IReadOnlyList<TranslatedBlock> lines =
        [
            new("こういうところにもヒントはあるらしい。", "看來這種地方也有線索。", new Rect(20, 14.4, 568, 31.2)),
            new("どんな傾向の映画が流行っているのか", "流行的是什麼樣的電影", new Rect(20, 64.2, 530, 28.7)),
        ];
        var window = new RealtimeBlockWindow(0, new System.Drawing.Rectangle(0, 0, 700, 200),
            _ => null, "JA", "ZH-HANT", "#FFFFFF", "#000000", 70, fixedFontSize: 20);
        try
        {
            Rebuild(window, lines);
            var laidOut = (IReadOnlyList<TranslatedBlock>)typeof(RealtimeBlockWindow)
                .GetField("_layoutLines", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.Same(lines, laidOut);
        }
        finally { window.Close(); }
    });

    /// <summary>
    /// A line of the session's dialogue, which is nearly all of it. The rest is what else was in
    /// the block now and then — a 12px caption, a sign in the background art — and those are other
    /// sizes, which changing to and from is not the fault being measured.
    /// </summary>
    private static bool IsDialogue(Reading line) =>
        line.Glyphs >= RealtimeTextSizes.ReliableFromGlyphs && line.Height is > 26.5 and < 46;

    private static Reading Long(string key, double height) => new(key, height, 20, FromRowBox: true);

    private static Reading Short(string key, double height) => new(key, height, 3, FromRowBox: true);

    private static TranslatedBlock Latin(string text, double glyphHeight) =>
        new(text, "譯文", new Rect(20, 60, 640, glyphHeight * 1.6), RenderGlyphHeight: glyphHeight)
        {
            FontGlyphHeight = glyphHeight,
        };

    /// <summary>A block that has shown enough long lines to have settled on <paramref name="size"/>.</summary>
    private static RealtimeTextSizes Warmed(double size)
    {
        var sizes = new RealtimeTextSizes();
        double[] spread = [0, -3.7, 3.2, -1.2, 1.2, -0.4, 0.4, -2.1, 2.9, 0];
        for (int line = 0; line < spread.Length * 2; line++)
            sizes.Settle([Long($"warm{line}", size + spread[line % spread.Length])]);
        Assert.Equal(size, sizes.Settle([Long("warm0", size)])[0], 1);
        return sizes;
    }

    /// <summary>Share of lines whose size is more than 3% off the line before.</summary>
    private static double Changing(IEnumerable<double> sizes)
    {
        var list = sizes.ToList();
        int changes = 0;
        for (int line = 1; line < list.Count; line++)
            if (Math.Max(list[line] / list[line - 1], list[line - 1] / list[line]) > 1.03) changes++;
        return (double)changes / (list.Count - 1);
    }

    private static List<List<Reading>> Session() =>
    [
        .. File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RealtimeTextSizes", "dialogue-session.txt"))
            .Where(row => row.Length > 0 && row[0] != '#')
            .Select(row => row.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('/'))
                .Select(parts => new Reading(
                    parts[0], double.Parse(parts[2], CultureInfo.InvariantCulture), int.Parse(parts[1]), FromRowBox: true))
                .ToList()),
    ];

    /// <summary>
    /// The font sizes one window draws for each frame in turn, as it would over a session.
    /// </summary>
    private static List<List<double>> Draw(RealtimeBlockMode mode, IReadOnlyList<IReadOnlyList<TranslatedBlock>> frames)
    {
        var window = new RealtimeBlockWindow(0, new System.Drawing.Rectangle(0, 0, 700, 200),
            _ => null, "JA", "ZH-HANT", "#FFFFFF", "#000000", 70, mode: mode);
        try
        {
            var drawn = new List<List<double>>();
            foreach (var frame in frames)
            {
                Rebuild(window, frame);
                drawn.Add([.. ((Canvas)window.FindName("TextCanvas")).Children.Cast<Border>().Select(box =>
                    (box.Child as TextBlock ?? Assert.IsType<TextBlock>(Assert.IsType<Viewbox>(box.Child).Child)).FontSize)]);
            }
            return drawn;
        }
        finally { window.Close(); }
    }

    private static void Rebuild(RealtimeBlockWindow window, IReadOnlyList<TranslatedBlock> lines)
    {
        var type = typeof(RealtimeBlockWindow);
        type.GetField("_lines", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, lines);
        type.GetMethod("Rebuild", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    }

    private static void OnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
