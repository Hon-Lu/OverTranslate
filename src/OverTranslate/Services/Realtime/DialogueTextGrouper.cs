using System.Windows;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Dialogue-box geometry, independent of screenshot paragraph, punctuation and heading rules.
/// Horizontal rows are completed first. An unresolved row is never partially joined vertically,
/// but for a line beside a dialogue box's arrow or button (see <see cref="StacksBesideItems"/>).
/// </summary>
/// <remarks>
/// <para>One content test is shared with the other grouper: a line that opens like a chat entry —
/// timestamp, channel tag, @handle, name and colon (<see cref="SpeakerLineStart"/>) — does not
/// continue the line above it. A chat log framed in this mode is stacked rows of one size and one
/// edge, which is exactly what this grouper joins, so the 23 hand-marked chat captures came back
/// with one message per group in 14 of 245 cases. With the test, 123. Across the subtitle and comic
/// captures (the 410 frames of the speaker-name probe, every subtitle set, all four comic sets) it
/// changes nothing; elsewhere it parts a Hacker News title from the line above and a wiki list
/// entry 「フランス革命：…」 from the one before, both correctly.</para>
///
/// <para>The other half the panel path has is taken too: a line holding only a speaker in that
/// form (「[GLOBAL] Conor_Braxton:」) is not continued, so a name on its own line stays apart from
/// its message. #341 had left that layout joined; the name-plate rule there is about Ren'Py's
/// plain name over indented dialogue and is unchanged.</para>
///
/// <para>Both halves use the strict form of a bare name (<see cref="SpeakerLineStart.OpensUnmistakably"/>,
/// <see cref="SpeakerLineStart.IsUnmistakablyBareLabel"/>): subtitles wrap after a colon in running
/// text — 「他回頭對我說：」 over 「「快走！」」, 「也就是說：…」, "this: never give up" — and here a
/// sentence cut at its colon is worse than a chat entry left joined. A channel tag, an @handle or
/// a timestamp is taken as it is.</para>
/// </remarks>
internal static class DialogueTextGrouper
{
    /// <param name="decisions">
    /// Collects why each pair was joined or refused, for <c>OcrHarness --group-explain --realtime</c>.
    /// Null everywhere but the harness, and read by no rule: a pass with one and a pass without
    /// reach the same verdicts. The record is shared with the screenshot grouper, so several of its
    /// fields have no dialogue meaning and are left at zero; the ones filled are named in the
    /// harness's own legend.
    /// </param>
    /// <param name="maxStackGap">
    /// The widest gap between stacked rows, in line heights — <see cref="MaxStackGap"/>, which every
    /// mode reads with (see <see cref="ResolvedProfile"/>).
    /// </param>
    /// <param name="merge">
    /// 合併, 語句合併's last value: stacked lines join regardless of size and alignment, and across a
    /// short item beside one of them — see <see cref="NextRowMerging"/> and <see cref="AsideOnly"/>.
    /// Pieces of one row are joined as always.
    /// </param>
    internal static List<OcrTextBlock> Group(
        IReadOnlyList<OcrTextBlock> input,
        GroupingTrace? trace = null,
        List<OcrTextBlockGrouper.NextLineDecision>? decisions = null,
        double maxStackGap = MaxStackGap,
        bool merge = false)
    {
        if (trace is not null && trace.Blocks.Count == 0) trace.RegisterBlocks(input);
        if (input.Count <= 1)
        {
            foreach (var b in input) trace?.RegisterLine(b, [b]);
            trace?.OrderLines(input);
            trace?.RegisterGroups(input.Select(b => (IReadOnlyList<OcrTextBlock>)new[] { b }).ToList());
            return input.ToList();
        }
        var rows = new List<List<OcrTextBlock>>();
        foreach (var block in input.OrderBy(b => b.LayoutBounds.X).ThenBy(b => b.LayoutBounds.Y))
        {
            var row = rows.FirstOrDefault(r => r.Any(b => SameRow(b.LayoutBounds, block.LayoutBounds)));
            if (row is null) rows.Add([block]);
            else row.Add(block);
        }
        var lines = new List<(OcrTextBlock Block, bool Unresolved)>();
        // Lines built from more than one detected block. A speaker label is one word the detector
        // read in one piece; see SpeakerLabel.
        var joinedLines = new HashSet<OcrTextBlock>(ReferenceEqualityComparer.Instance);
        // The rows ItemRow took for slot labels, and the row each line came from; see
        // ValuesBesideItems.
        var itemRows = new List<(Rect Span, List<OcrTextBlock> Row)>();
        var lineRows = new Dictionary<OcrTextBlock, List<OcrTextBlock>>(ReferenceEqualityComparer.Instance);
        // The other lines of each line's row: what 合併 may stack across (AsideOnly), and what
        // 整段 may (StacksBesideItems).
        var besides = new Dictionary<OcrTextBlock, List<OcrTextBlock>>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows)
        {
            var segments = new List<List<OcrTextBlock>>();
            var items = ItemRow(row, rows);
            if (items && row.Any(b => HasLetter(b.Text))) itemRows.Add((Span(row), row));
            for (int k = 0; k < row.Count; k++)
            {
                var block = row[k];
                var joinsRow = segments.Count > 0 && Adjacent(segments[^1][^1].LayoutBounds, block.LayoutBounds);
                string? refused = null;
                if (joinsRow && items && SpacedItems(segments[^1][^1], block))
                {
                    joinsRow = false;
                    refused = "row-items";
                }
                else if (joinsRow && ColumnStart(row, k, rows))
                {
                    joinsRow = false;
                    refused = "column-gutter";
                }
                if (segments.Count > 0) RecordRow(decisions, segments[^1][^1], block, joinsRow, refused);
                if (!joinsRow) segments.Add([block]);
                else segments[^1].Add(block);
            }
            // Single-character detections need a real text fragment as an anchor. Joining only
            // isolated symbols would manufacture enough length to evade the downstream filter.
            segments = segments.SelectMany(s => s.Count > 1 && s.All(b => b.Text.Count(c => !char.IsWhiteSpace(c)) < 2)
                ? s.Select(b => new List<OcrTextBlock> { b }) : new[] { s }).ToList();
            var rowLines = new List<OcrTextBlock>();
            foreach (var segment in segments)
            {
                var line = OcrTextBlockGrouper.BuildGroup(segment);
                if (segment.Count > 1)
                {
                    line = line with { Text = segment.Select(b => b.Text.Trim()).Aggregate(OcrTextBlockGrouper.JoinInlineText) };
                    joinedLines.Add(line);
                }
                trace?.RegisterLine(line, segment);
                lines.Add((line, segments.Count > 1));
                lineRows[line] = row;
                rowLines.Add(line);
            }
            foreach (var line in rowLines)
                besides[line] = rowLines.Where(other => !ReferenceEquals(other, line)).ToList();
        }
        var chatNames = ChatNameLines(lines, joinedLines, maxStackGap);
        var groups = new List<List<OcrTextBlock>>();
        var open = new List<bool>();
        trace?.OrderLines(lines.OrderBy(l => l.Block.LayoutBounds.Y).ThenBy(l => l.Block.LayoutBounds.X).Select(l => l.Block).ToList());
        foreach (var line in lines.OrderBy(l => l.Block.LayoutBounds.Y).ThenBy(l => l.Block.LayoutBounds.X))
        {
            int target = -1;
            double nearest = double.MaxValue;
            bool stacks = merge || !line.Unresolved || StacksBesideItems(line.Block, besides[line.Block], lines);
            for (int i = 0; stacks && i < groups.Count; i++)
            {
                if (!open[i]) continue;
                var previous = groups[i][^1].LayoutBounds;
                var current = line.Block.LayoutBounds;
                var joins = merge
                    ? NextRowMerging(previous, current, maxStackGap, out var rule)
                    : NextRow(previous, current, maxStackGap, out rule);
                if (joins && merge && !AsideOnly(besides[groups[i][^1]], current, besides[line.Block], previous))
                {
                    joins = false;
                    rule = "row-beside";
                }
                if (joins && !merge && !ItemBesideBoth(besides[groups[i][^1]], previous, besides[line.Block], current))
                {
                    joins = false;
                    rule = "row-beside";
                }
                // Only a group of that one line can be a name: the last line of a paragraph that
                // happens to be short and unpunctuated is still that paragraph's.
                if (joins && groups[i].Count == 1 && !joinedLines.Contains(groups[i][^1]) &&
                    SpeakerLabel(groups[i][^1], line.Block))
                {
                    joins = false;
                    rule = "speaker-label";
                }
                if (joins && HasLetter(groups[i][^1].Text) != HasLetter(line.Block.Text) &&
                    (ValuesBesideItems(groups[i][^1], itemRows, lineRows) || ValuesBesideItems(line.Block, itemRows, lineRows)))
                {
                    joins = false;
                    rule = "numeric-line";
                }
                if (joins && SpeakerLineStart.OpensUnmistakably(line.Block.Text))
                {
                    joins = false;
                    rule = "speaker line start";
                }
                if (joins && SpeakerLineStart.IsUnmistakablyBareLabel(groups[i][^1].Text))
                {
                    joins = false;
                    rule = "speaker label";
                }
                if (joins && (chatNames.Contains(line.Block) || chatNames.Contains(groups[i][^1])))
                {
                    joins = false;
                    rule = "chat name";
                }
                RecordNext(decisions, groups[i][^1], line.Block, joins, rule);
                if (!joins) continue;
                var distance = current.Y - previous.Y;
                if (distance < nearest) { target = i; nearest = distance; }
            }
            // A line we did not join is a boundary for older overlapping text. Do not later
            // jump across a speaker label or a differently sized line to resume that group.
            for (int i = 0; i < groups.Count; i++)
            {
                if (i == target) continue;
                var previous = groups[i][^1].LayoutBounds;
                var current = line.Block.LayoutBounds;
                if (current.Y > previous.Y && Math.Min(previous.Right, current.Right) > Math.Max(previous.Left, current.Left))
                    open[i] = false;
            }
            if (target < 0) { groups.Add([line.Block]); open.Add(stacks); }
            else groups[target].Add(line.Block);
        }
        trace?.RegisterGroups(groups);
        return groups.Select(OcrTextBlockGrouper.BuildGroup).ToList();
    }

    /// <summary>
    /// The name lines of a chat log that puts each speaker on a line of its own, without a colon
    /// (PSO2's chat window): kept apart from the message under them and from the one above.
    /// </summary>
    /// <remarks>
    /// <para>Geometry cannot find them on its own. A name sits 0.07–0.43 heights left of its
    /// message, inside the range a dialogue box's own short first lines reach, and #341 already
    /// leaves that layout joined. What a subtitle or a dialogue box never has is the same short
    /// line again and again: Rossa twice and Hatsune Miku three times in one window, System over
    /// every notice. So the frame has to show that first — at least three name-shaped lines whose
    /// text recurs — and only then is every name-shaped line taken for a name.</para>
    ///
    /// <para>Name-shaped: one detected block, under half as wide as the line it would continue
    /// into, no closing punctuation or opening bracket, and starting at that line's left edge or a
    /// little left of it.</para>
    /// </remarks>
    private static HashSet<OcrTextBlock> ChatNameLines(
        List<(OcrTextBlock Block, bool Unresolved)> lines, HashSet<OcrTextBlock> joinedLines, double maxStackGap)
    {
        var names = new HashSet<OcrTextBlock>(ReferenceEqualityComparer.Instance);
        var ordered = lines.Where(l => !l.Unresolved).Select(l => l.Block)
            .OrderBy(b => b.LayoutBounds.Y).ThenBy(b => b.LayoutBounds.X).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            var name = ordered[i];
            if (joinedLines.Contains(name)) continue;
            var message = ordered.Skip(i + 1).FirstOrDefault(b => NextRow(name.LayoutBounds, b.LayoutBounds, maxStackGap, out _));
            if (message is not null && NameOverMessage(name, message)) names.Add(name);
        }
        int recurring = names.GroupBy(b => b.Text.Trim()).Where(g => g.Count() >= 2).Sum(g => g.Count());
        if (recurring < 2) { names.Clear(); return names; }
        // A name over a message shorter than itself (Hatsune Miku over Rawr~!) is not name-shaped,
        // but it is the name already seen elsewhere in the window.
        var known = names.Select(b => b.Text.Trim()).ToHashSet();
        foreach (var line in ordered)
            if (!joinedLines.Contains(line) && known.Contains(line.Text.Trim())) names.Add(line);
        return names;
    }

    private static bool NameOverMessage(OcrTextBlock name, OcrTextBlock message)
    {
        Rect a = name.LayoutBounds, b = message.LayoutBounds;
        double height = Math.Max(a.Height, b.Height);
        var text = name.Text.Trim();
        return text.Length > 0 && text.Count(c => !char.IsWhiteSpace(c)) <= 15 && a.Width < b.Width * 0.75 &&
            !SpeakerLabelEnds.Contains(text[^1]) && !SpeakerLabelOpens.Contains(text[0]) &&
            b.Left - a.Left >= -height * 0.2 && b.Left - a.Left <= height * 0.6;
    }

    private static bool SameRow(Rect a, Rect b) =>
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) >= Math.Min(a.Height, b.Height) * 0.6 &&
        Math.Abs((a.Top + a.Bottom - b.Top - b.Bottom) / 2) <= Math.Max(a.Height, b.Height) * 0.35;

    private static bool Adjacent(Rect a, Rect b) =>
        SameRow(a, b) && LineMeasure.HeightsWithin(a, b, RowHeightRatio) &&
        b.Left - a.Right <= Math.Max(a.Height, b.Height) * 1.5;

    /// <summary>
    /// A row of separate short items — a status screen's tabs or slot labels — rather than one
    /// line the detector broke at its spaces: several short boxes, with another itemised row right
    /// above or below it.
    /// </summary>
    /// <remarks>
    /// <para>The row alone cannot tell. A menu's labels sit 0.95–1.50 heights apart, straddling the
    /// 1.5-height join in <see cref="Adjacent"/>, so the same screen joined different pairs at
    /// different scales. But a subtitle written with doubled full-width spaces,
    /// はい　　はい　　はい　　はい, is cut by the detector into four boxes 0.93–1.43 heights apart,
    /// each as short as a label: every gap, width and count threshold tried split it too. Single
    /// full-width spaces and English word gaps never reached the grouper as separate boxes.</para>
    ///
    /// <para>What a subtitle never has is a second row of items lined up with the first — the
    /// percentages under the slot labels, kept whether the detector read them as seven boxes or
    /// as one box of seven numbers. Over 410 corpus frames and 216 synthetic spaced subtitles this
    /// changed nothing.</para>
    ///
    /// <para>That second row has to be values, with no letters in it. A line under a spaced one can
    /// carry three numbers (10時30分に3番線へ), and the next line of a spaced subtitle has three
    /// boxes of its own; either would otherwise vouch for the line above. This gives up a web
    /// page's tab rows, whose neighbours are tabs too.</para>
    /// </remarks>
    private static bool ItemRow(List<OcrTextBlock> row, List<List<OcrTextBlock>> rows)
    {
        if (row.Count(b => ShortItem(b.LayoutBounds)) < 3) return false;
        var span = Span(row);
        return rows.Any(other => !ReferenceEquals(other, row) && Companion(span, other));
    }

    private static bool Companion(Rect items, List<OcrTextBlock> other)
    {
        var span = Span(other);
        return Math.Max(span.Top - items.Bottom, items.Top - span.Bottom) <= items.Height * 1.5 &&
            Math.Min(items.Right, span.Right) - Math.Max(items.Left, span.Left) >= items.Width * 0.5 &&
            !other.Any(b => HasLetter(b.Text)) && (other.Count >= 3 || other.Sum(b => DigitRuns(b.Text)) >= 3);
    }

    // Measured labels reach 1.95 heights; a 0.9-height gap is under the narrowest label gap, 0.95.
    private static bool ShortItem(Rect r) => r.Width <= r.Height * 2.2;

    // Only a word starts an item. A status row's HP and EP are short and set apart from their
    // numbers too (Dragon Quest XI, Trails of Cold Steel), and cutting a label from its value turns
    // one stat into two meaningless pieces; a value followed by the next label is where one stat
    // ends and the next begins.
    private static bool SpacedItems(OcrTextBlock left, OcrTextBlock right)
    {
        Rect a = left.LayoutBounds, b = right.LayoutBounds;
        return ShortItem(a) && ShortItem(b) && HasLetter(right.Text) &&
            b.Left - a.Right >= Math.Max(a.Height, b.Height) * 0.9;
    }

    private static Rect Span(List<OcrTextBlock> row) => row.Select(b => b.LayoutBounds).Aggregate(Rect.Union);

    /// <summary>
    /// The row of values that made a row of slot labels itemised (see <see cref="ItemRow"/>):
    /// numbers only, no letters, right beside those labels.
    /// </summary>
    /// <remarks>
    /// Such a row is wide enough to be stacked with the note under it as "two substantial rows",
    /// and then the note was translated with seven percentages in front of it. Refusing every
    /// letterless line instead would also part a Korean scoreboard's 26/3/19 from the TK / K / A
    /// under it, which nothing about slot labels said; tying it to an item row changed no corpus
    /// frame.
    /// <para>Only a row with words in it counts as the labels, and never the values' own row. A
    /// scoreboard's two rows of bare numbers itemise each other, and either row would otherwise
    /// count as labels beside the other, or beside itself, and be parted from the header under
    /// them.</para>
    /// </remarks>
    private static bool ValuesBesideItems(OcrTextBlock line, List<(Rect Span, List<OcrTextBlock> Row)> itemRows,
        Dictionary<OcrTextBlock, List<OcrTextBlock>> lineRows)
    {
        if (HasLetter(line.Text) || DigitRuns(line.Text) < 3) return false;
        Rect r = line.LayoutBounds;
        var own = lineRows.GetValueOrDefault(line);
        return itemRows.Where(i => !ReferenceEquals(i.Row, own)).Select(i => i.Span).Any(items =>
            Math.Max(items.Top - r.Bottom, r.Top - items.Bottom) <= Math.Max(items.Height, r.Height) * 1.5 &&
            Math.Min(items.Right, r.Right) - Math.Max(items.Left, r.Left) >= items.Width * 0.5);
    }

    /// <summary>
    /// The right column of a two-column list: the next box starts where, in at least three rows
    /// nearby, a box starts after a wide gap too.
    /// </summary>
    /// <remarks>
    /// A status screen's "label value" pairs sit 1.33–1.43 heights from the next column's, inside
    /// the 1.5-height join. One aligned row is not enough: a visual novel's quick menu has a G and
    /// a C icon above it, and the C lines up with QUIT, which would cut the menu in two. Three rows
    /// changed no corpus frame; two would have done as well.
    /// <para>The left column has to line up too, or it is not a list. Centred lyrics whose spaced
    /// breaks happen to fall at one x start each line somewhere else, and a HUD stacked at that x
    /// starts its rows at its own labels, not at the dialogue's left edge.</para>
    /// </remarks>
    private static bool ColumnStart(List<OcrTextBlock> row, int index, List<List<OcrTextBlock>> rows)
    {
        Rect a = row[index - 1].LayoutBounds, b = row[index].LayoutBounds;
        double height = Math.Max(a.Height, b.Height);
        if (b.Left - a.Right < height * 0.9) return false;
        double left = RunStart(row, index - 1);
        int aligned = 0;
        foreach (var other in rows)
        {
            if (ReferenceEquals(other, row)) continue;
            for (int i = 1; i < other.Count; i++)
            {
                Rect p = other[i - 1].LayoutBounds, c = other[i].LayoutBounds;
                if (Math.Abs(c.Left - b.Left) > height * 0.5) continue;
                if (Math.Max(c.Top - b.Bottom, b.Top - c.Bottom) > height * 4) continue;
                if (c.Left - p.Right < Math.Max(p.Height, c.Height) * 0.9) continue;
                if (Math.Abs(RunStart(other, i - 1) - left) > height * 0.5) continue;
                aligned++;
                break;
            }
        }
        return aligned >= 3;
    }

    // Where the column holding row[index] starts: back from it until a gap as wide as a gutter.
    private static double RunStart(List<OcrTextBlock> row, int index)
    {
        while (index > 0)
        {
            Rect p = row[index - 1].LayoutBounds, c = row[index].LayoutBounds;
            if (c.Left - p.Right >= Math.Max(p.Height, c.Height) * 0.9) break;
            index--;
        }
        return row[index].LayoutBounds.Left;
    }

    private static bool HasLetter(string text) => text.Any(char.IsLetter);

    private static int DigitRuns(string text)
    {
        int runs = 0;
        for (int i = 0; i < text.Length; i++)
            if (char.IsDigit(text[i]) && (i == 0 || !char.IsDigit(text[i - 1]))) runs++;
        return runs;
    }

    private static bool NextRow(Rect a, Rect b, double maxStackGap, out string rule)
    {
        rule = "stacked";
        double height = Math.Max(a.Height, b.Height);
        // Long subtitle rows can have quite different detector padding even at the same font
        // size. Give two substantial, similarly wide rows extra height tolerance; a short name
        // or isolated symbol cannot borrow it from the dialogue beside it.
        bool substantialRows = SubstantialRows(a, b);
        // Named one test at a time rather than as a single condition, so a refusal can say which
        // test refused it. The order and the numbers are unchanged.
        if (height <= 0 || b.Y <= a.Y) { rule = "not-below"; return false; }
        if (SameRow(a, b)) { rule = "same-row"; return false; }
        if (!LineMeasure.HeightsWithin(a, b, substantialRows ? SubstantialStackHeightRatio : StackHeightRatio))
        { rule = "height-mismatch"; return false; }
        if (b.Top - a.Bottom > height * maxStackGap) { rule = "gap-too-wide"; return false; }
        if (b.Top - a.Bottom < -height * MaxStackOverlap) { rule = "overlaps-above"; return false; }
        var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (overlap < Math.Min(a.Width, b.Width) * 0.5) { rule = "no-column-overlap"; return false; }
        var alignment = Math.Min(Math.Abs(a.Left - b.Left), Math.Min(Math.Abs(a.Right - b.Right),
            Math.Abs((a.Left + a.Right - b.Left - b.Right) / 2)));
        // Two substantial, similarly wide rows get the same benefit of the doubt on alignment that
        // they already get on height. A dialogue row whose leading word the detector missed starts
        // well to the right of the row above it — measured at 138px on a 64px line, 2.2 heights —
        // and refusing to stack them there turns one missing word into two overlay boxes. A short
        // name or an isolated symbol still cannot borrow this.
        if (alignment > height * (substantialRows ? 2.5 : 1.25)) { rule = "alignment"; return false; }
        rule = substantialRows ? "stacked-substantial" : "stacked";
        return true;
    }

    /// <summary>
    /// 合併's stacking test (語句合併's last value): <see cref="NextRow"/> without the size and the
    /// alignment tests. The gap, the overlap above and the shared column still part rows.
    /// </summary>
    /// <remarks>
    /// <para>A size difference still parts a line under half as wide as the other: that is a name
    /// plate or a label over the dialogue (Lua, 月島まりな, エマ in the corpus), not a line of it.
    /// Without this, 合併 joined them on 3 more dialogue frames and changed nothing else there.</para>
    ///
    /// <para>The gap is 整段's. MEASURED (OcrHarness --auto-probe mergemax): at 1.25 times it the
    /// dialogue frames gained a choice menu's names (一条 美雲 / 大ドロボウ) and a reward list's
    /// entries joined to each other, and nothing that read better.</para>
    /// </remarks>
    private static bool NextRowMerging(Rect a, Rect b, double maxStackGap, out string rule)
    {
        rule = "merged";
        double height = Math.Max(a.Height, b.Height);
        if (height <= 0 || b.Y <= a.Y) { rule = "not-below"; return false; }
        if (SameRow(a, b)) { rule = "same-row"; return false; }
        if (!LineMeasure.HeightsWithin(a, b, StackHeightRatio))
        {
            Rect smaller = a.Height < b.Height ? a : b, larger = a.Height < b.Height ? b : a;
            if (smaller.Width < larger.Width * 0.5) { rule = "small-label"; return false; }
        }
        if (b.Top - a.Bottom > height * maxStackGap) { rule = "gap-too-wide"; return false; }
        if (b.Top - a.Bottom < -height * MaxStackOverlap) { rule = "overlaps-above"; return false; }
        var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (overlap < Math.Min(a.Width, b.Width) * 0.5) { rule = "no-column-overlap"; return false; }
        return true;
    }

    /// <summary>
    /// 合併: whether two stacked lines may join although a row of theirs holds something else — only
    /// when it is a short item off to the side (an arrow, Auto, a key hint) and only one of the two
    /// rows has one.
    /// </summary>
    /// <remarks>
    /// <para>整段 never stacks a line whose row is cut in pieces (see the class remarks), and a
    /// dialogue box's 》 or Auto sitting on the second line's row cuts it, which parts the box's two
    /// lines (ja-aa-nameplate ×3, en-uma-narration). An item short as <see cref="ShortItem"/> and
    /// clear of the other line's width does not.</para>
    ///
    /// <para>Not when both rows have one: that is a table, a label over a label with their values
    /// beside them, and the column of labels would be read as one sentence. MEASURED on the panel
    /// captures: with both allowed, 合併 joined 986 pairs 整段 parted, 756 without. Long items beside
    /// a line (a second list column, as in ja-va11-2line-list) are not taken either: allowing them
    /// joined that dialogue but also 6,601 pairs on the panel captures, the columns of every list.</para>
    /// </remarks>
    private static bool AsideOnly(List<OcrTextBlock> aboveRow, Rect below, List<OcrTextBlock> belowRow, Rect above)
    {
        if (aboveRow.Count > 0 && belowRow.Count > 0) return false;
        return Aside(aboveRow, below) && Aside(belowRow, above);

        static bool Aside(List<OcrTextBlock> others, Rect partner) =>
            others.All(other => ShortItem(other.LayoutBounds) &&
                Math.Min(other.LayoutBounds.Right, partner.Right) <= Math.Max(other.LayoutBounds.Left, partner.Left));
    }

    /// <summary>
    /// 整段: whether a line its row cut in pieces may still be stacked — when it is a line, not an
    /// item, and the only other thing on its row is one short item to its right (a page arrow,
    /// Auto) with no short item stacked over or under it.
    /// </summary>
    /// <remarks>
    /// <para>A dialogue box's 》 or Auto beside its last line cuts that row, and an unresolved row is
    /// never stacked (see the class summary), so the box's two lines were parted
    /// (ja-aa-nameplate ×3, en-uma-narration) where 逐項 and 自動 kept them. Whether the pair may
    /// join across the item is asked in <see cref="ItemBesideBoth"/>.</para>
    ///
    /// <para>MEASURED (OcrHarness --auto-probe replay, 字幕・漫畫): letting any short items clear
    /// of both lines through changed 40 frames, 29 of them interface captures — a city panel's
    /// column of 0s beside its "Amenities from …" rows, a forum header's login, chat names. A
    /// short item with another one in its column is a table's value, not a box's button; with
    /// that, the right side only, and the tests of <see cref="ItemBesideBoth"/>, the 11 frames
    /// that change are the arrow and Auto frames and nothing else. ja-va11-2line-list stays
    /// parted: what sits beside its line is the next list column's entry, not a short item.</para>
    /// </remarks>
    private static bool StacksBesideItems(OcrTextBlock line, List<OcrTextBlock> besideIt,
        List<(OcrTextBlock Block, bool Unresolved)> lines)
    {
        if (besideIt.Count != 1 || ShortItem(line.LayoutBounds)) return false;
        var item = besideIt[0];
        Rect r = item.LayoutBounds;
        if (!ShortItem(r) || r.Left < line.LayoutBounds.Right) return false;
        return !lines.Any(other => !ReferenceEquals(other.Block, item) && ShortItem(other.Block.LayoutBounds) &&
            Math.Min(other.Block.LayoutBounds.Right, r.Right) > Math.Max(other.Block.LayoutBounds.Left, r.Left) &&
            Math.Max(other.Block.LayoutBounds.Top - r.Bottom, r.Top - other.Block.LayoutBounds.Bottom) <= r.Height * 2);
    }

    /// <summary>
    /// 整段: whether two stacked lines may join across the item beside one of them (see
    /// <see cref="StacksBesideItems"/>) — only two substantial lines, only one of the two rows has
    /// one, and it starts right of both lines, within <see cref="ItemBesideMaxGap"/> of the longer.
    /// </summary>
    /// <remarks>
    /// <para>Narrower than 合併's <see cref="AsideOnly"/>, which measures each row's items against the
    /// other line only: an item under the end of a longer line above sits in the column of the
    /// text and may be part of it.</para>
    ///
    /// <para>MEASURED: the arrows and Auto sat 2.4–5.5 line heights right of the longer line. What
    /// sat further off was another part of the screen — a dialogue log's Log and Sparks buttons
    /// (6.8, 7.2), a menu's Other (14), a forum's login (13–27), a map's L:83 (25–37) — where
    /// joining was right about as often as wrong, all on interface captures, and changing them is
    /// not this rule's business. Short or unequal lines (a name over a
    /// figure, an item title over its description) are not dialogue lines and are left parted.</para>
    /// </remarks>
    private static bool ItemBesideBoth(List<OcrTextBlock> aboveRow, Rect above, List<OcrTextBlock> belowRow, Rect below)
    {
        if (aboveRow.Count == 0 && belowRow.Count == 0) return true;
        if (aboveRow.Count > 0 && belowRow.Count > 0) return false;
        if (!SubstantialRows(above, below)) return false;
        double right = Math.Max(above.Right, below.Right), height = Math.Max(above.Height, below.Height);
        return aboveRow.Concat(belowRow).All(other =>
            other.LayoutBounds.Left >= right && other.LayoutBounds.Left - right <= height * ItemBesideMaxGap);
    }

    /// <summary>
    /// Two rows long enough, and close enough in width, to be given extra tolerance on height and
    /// alignment — which a short name or an isolated symbol beside them must not borrow.
    /// </summary>
    private static bool SubstantialRows(Rect a, Rect b) =>
        LineMeasure.Spans(a, LineMeasure.SubstantialLineMinAspect) &&
        LineMeasure.Spans(b, LineMeasure.SubstantialLineMinAspect) &&
        Math.Min(a.Width, b.Width) >= Math.Max(a.Width, b.Width) * 0.5;

    /// <summary>
    /// How far right of two stacked lines the one short item beside them may sit for 整段 to stack
    /// them across it, in line heights — see <see cref="ItemBesideBoth"/>.
    /// </summary>
    private const double ItemBesideMaxGap = 6;

    /// <summary>How close in box height two pieces of one row have to be.</summary>
    private const double RowHeightRatio = 0.6;

    /// <summary>
    /// How close in box height two stacked rows have to be: the ordinary figure, and the one two
    /// substantial rows get (see <see cref="SubstantialRows"/>).
    /// </summary>
    private const double StackHeightRatio = 0.75;

    /// <inheritdoc cref="StackHeightRatio"/>
    private const double SubstantialStackHeightRatio = 0.6;

    /// <summary>The widest gap, and the most overlap, between two stacked rows, in line heights.</summary>
    internal const double MaxStackGap = 0.65;

    /// <inheritdoc cref="MaxStackGap"/>
    private const double MaxStackOverlap = 0.25;

    /// <summary>
    /// A name plate over indented dialogue, as Ren'Py draws it by default (issue #341): one short
    /// unpunctuated word sticking out to the left of a much longer line.
    /// </summary>
    /// <remarks>
    /// <para>The name's box height swings across the 0.75 height floor with the capture's scale and
    /// framing — 17 of 28 measured variants of the one screen got past it — so size alone cannot
    /// keep it apart.</para>
    ///
    /// <para>Sticking out, not merely left-aligned. A short line flush with the one below is also
    /// how a left-aligned paragraph starts: a dialogue game's two-line sentence broken after
    /// その音はどこかおぞましく、 has the same geometry as the name over a comment in a comic
    /// screenshot's comment section, and splitting either breaks a corpus result. The name measured
    /// 0.49–0.72 heights left of its dialogue; no short first line in the corpus stuck out more than
    /// 0.10. A centred speech bubble's short first line sits to the right instead. Plates flush with
    /// their dialogue are left joined on purpose: a missed name is the lesser harm.</para>
    ///
    /// <para>The punctuation and quote checks guard a hanging indent. エマもヒロも身を強張らせ、
    /// sticks out 1.25 heights over its own continuation and is stopped only by its width; a
    /// shorter clause would be stopped by its comma, and 「ねえ over an indented 聞いてる？」 by
    /// its bracket. Names carry neither.</para>
    /// </remarks>
    private static bool SpeakerLabel(OcrTextBlock previous, OcrTextBlock current)
    {
        Rect a = previous.LayoutBounds, b = current.LayoutBounds;
        double height = Math.Max(a.Height, b.Height);
        var text = previous.Text.Trim();
        return text.Length > 0 && a.Width < b.Width * 0.5 &&
            !SpeakerLabelEnds.Contains(text[^1]) && !SpeakerLabelOpens.Contains(text[0]) &&
            a.Left <= b.Left - height * 0.4 &&
            Math.Abs((a.Left + a.Right - b.Left - b.Right) / 2) >= height * 1.5;
    }

    private const string SpeakerLabelEnds = ".!?。！？…‥\"”」』)）~～♪♥,、，;；";
    private const string SpeakerLabelOpens = "「『“\"＂（(";

    // The two verdicts, in the record the harness already knows how to print. "row" puts the
    // HORIZONTAL gap in VerticalGap and the vertical overlap in LeftDelta, because that is what the
    // row printer labels those two columns; "next" fills the three alignment deltas and the one the
    // gate actually read. Anything left at zero is a quantity the dialogue rules never compute, and
    // the harness prints its own legend saying so.
    private static void RecordRow(
        List<OcrTextBlockGrouper.NextLineDecision>? decisions,
        OcrTextBlock previous, OcrTextBlock current, bool joined, string? refused = null)
    {
        if (decisions is null) return;
        Rect a = previous.LayoutBounds, b = current.LayoutBounds;
        double height = Math.Max(a.Height, b.Height);
        if (height <= 0) return;
        var rule = refused ?? (!SameRow(a, b) ? "not-same-row"
            : !LineMeasure.HeightsWithin(a, b, RowHeightRatio) ? "row-height-mismatch"
            : b.Left - a.Right > height * 1.5 ? "row-gap-too-wide"
            : "adjacent");
        decisions.Add(new OcrTextBlockGrouper.NextLineDecision(
            "row", previous.Text, current.Text, previous.LayoutScript, current.LayoutScript,
            VerticalGap: (b.Left - a.Right) / height,
            LeftDelta: (Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top))
                / Math.Max(1, Math.Min(a.Height, b.Height)),
            CenterDelta: 0, RightDelta: 0, AlignmentDelta: 0,
            TextSizeRatio: Math.Min(a.Height, b.Height) / height,
            WidthRatio: Math.Min(a.Width, b.Width) / Math.Max(1, Math.Max(a.Width, b.Width)),
            LineAdvance: 0, LeadingBar: 0, SolidBar: 0, Joined: joined, Rule: rule));
    }

    private static void RecordNext(
        List<OcrTextBlockGrouper.NextLineDecision>? decisions,
        OcrTextBlock previous, OcrTextBlock current, bool joined, string rule)
    {
        if (decisions is null) return;
        Rect a = previous.LayoutBounds, b = current.LayoutBounds;
        double height = Math.Max(a.Height, b.Height);
        if (height <= 0) return;
        double left = Math.Abs(a.Left - b.Left), right = Math.Abs(a.Right - b.Right);
        double centre = Math.Abs((a.Left + a.Right - b.Left - b.Right) / 2);
        bool substantialRows = SubstantialRows(a, b);
        decisions.Add(new OcrTextBlockGrouper.NextLineDecision(
            "next", previous.Text, current.Text, previous.LayoutScript, current.LayoutScript,
            VerticalGap: (b.Top - a.Bottom) / height,
            LeftDelta: left / height, CenterDelta: centre / height, RightDelta: right / height,
            AlignmentDelta: Math.Min(left, Math.Min(right, centre)) / height,
            TextSizeRatio: Math.Min(a.Height, b.Height) / height,
            WidthRatio: Math.Min(a.Width, b.Width) / Math.Max(1, Math.Max(a.Width, b.Width)),
            LineAdvance: (b.Y - a.Y) / height,
            // The two gates this grouper actually runs, in the columns the screenshot flow uses for
            // its own: the height floor it was judged against, and the alignment ceiling.
            LeadingBar: substantialRows ? SubstantialStackHeightRatio : StackHeightRatio,
            SolidBar: substantialRows ? 2.5 : 1.25,
            Joined: joined, Rule: rule));
    }
}
