using System.Windows;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Dialogue-box geometry, independent of screenshot paragraph, punctuation and heading rules.
/// Horizontal rows are completed first. An unresolved row is never partially joined vertically.
/// </summary>
internal static class DialogueTextGrouper
{
    /// <param name="decisions">
    /// Collects why each pair was joined or refused, for <c>OcrHarness --group-explain --realtime</c>.
    /// Null everywhere but the harness, and read by no rule: a pass with one and a pass without
    /// reach the same verdicts. The record is shared with the screenshot grouper, so several of its
    /// fields have no dialogue meaning and are left at zero; the ones filled are named in the
    /// harness's own legend.
    /// </param>
    internal static List<OcrTextBlock> Group(
        IReadOnlyList<OcrTextBlock> input,
        GroupingTrace? trace = null,
        List<OcrTextBlockGrouper.NextLineDecision>? decisions = null)
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
        // The rows ItemRow took for slot labels; see ValuesBesideItems.
        var itemRows = new List<Rect>();
        foreach (var row in rows)
        {
            var segments = new List<List<OcrTextBlock>>();
            var items = ItemRow(row, rows);
            if (items) itemRows.Add(Span(row));
            foreach (var block in row)
            {
                var joinsRow = segments.Count > 0 && Adjacent(segments[^1][^1].LayoutBounds, block.LayoutBounds);
                string? refused = null;
                if (joinsRow && items && SpacedItems(segments[^1][^1].LayoutBounds, block.LayoutBounds))
                {
                    joinsRow = false;
                    refused = "row-items";
                }
                if (segments.Count > 0) RecordRow(decisions, segments[^1][^1], block, joinsRow, refused);
                if (!joinsRow) segments.Add([block]);
                else segments[^1].Add(block);
            }
            // Single-character detections need a real text fragment as an anchor. Joining only
            // isolated symbols would manufacture enough length to evade the downstream filter.
            segments = segments.SelectMany(s => s.Count > 1 && s.All(b => b.Text.Count(c => !char.IsWhiteSpace(c)) < 2)
                ? s.Select(b => new List<OcrTextBlock> { b }) : new[] { s }).ToList();
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
            }
        }
        var groups = new List<List<OcrTextBlock>>();
        var open = new List<bool>();
        trace?.OrderLines(lines.OrderBy(l => l.Block.LayoutBounds.Y).ThenBy(l => l.Block.LayoutBounds.X).Select(l => l.Block).ToList());
        foreach (var line in lines.OrderBy(l => l.Block.LayoutBounds.Y).ThenBy(l => l.Block.LayoutBounds.X))
        {
            int target = -1;
            double nearest = double.MaxValue;
            for (int i = 0; !line.Unresolved && i < groups.Count; i++)
            {
                if (!open[i]) continue;
                var previous = groups[i][^1].LayoutBounds;
                var current = line.Block.LayoutBounds;
                var joins = NextRow(previous, current, out var rule);
                // Only a group of that one line can be a name: the last line of a paragraph that
                // happens to be short and unpunctuated is still that paragraph's.
                if (joins && groups[i].Count == 1 && !joinedLines.Contains(groups[i][^1]) &&
                    SpeakerLabel(groups[i][^1], line.Block))
                {
                    joins = false;
                    rule = "speaker-label";
                }
                if (joins && HasLetter(groups[i][^1].Text) != HasLetter(line.Block.Text) &&
                    (ValuesBesideItems(groups[i][^1], itemRows) || ValuesBesideItems(line.Block, itemRows)))
                {
                    joins = false;
                    rule = "numeric-line";
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
            if (target < 0) { groups.Add([line.Block]); open.Add(!line.Unresolved); }
            else groups[target].Add(line.Block);
        }
        trace?.RegisterGroups(groups);
        return groups.Select(OcrTextBlockGrouper.BuildGroup).ToList();
    }

    private static bool SameRow(Rect a, Rect b) =>
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) >= Math.Min(a.Height, b.Height) * 0.6 &&
        Math.Abs((a.Top + a.Bottom - b.Top - b.Bottom) / 2) <= Math.Max(a.Height, b.Height) * 0.35;

    private static bool Adjacent(Rect a, Rect b) =>
        SameRow(a, b) && Math.Min(a.Height, b.Height) >= Math.Max(a.Height, b.Height) * 0.6 &&
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
            (other.Count >= 3 || other.Sum(b => DigitRuns(b.Text)) >= 3);
    }

    // Measured labels reach 1.95 heights; a 0.9-height gap is under the narrowest label gap, 0.95.
    private static bool ShortItem(Rect r) => r.Width <= r.Height * 2.2;

    private static bool SpacedItems(Rect a, Rect b) =>
        ShortItem(a) && ShortItem(b) && b.Left - a.Right >= Math.Max(a.Height, b.Height) * 0.9;

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
    /// </remarks>
    private static bool ValuesBesideItems(OcrTextBlock line, List<Rect> itemRows)
    {
        if (HasLetter(line.Text) || DigitRuns(line.Text) < 3) return false;
        Rect r = line.LayoutBounds;
        return itemRows.Any(items =>
            Math.Max(items.Top - r.Bottom, r.Top - items.Bottom) <= Math.Max(items.Height, r.Height) * 1.5 &&
            Math.Min(items.Right, r.Right) - Math.Max(items.Left, r.Left) >= items.Width * 0.5);
    }

    private static bool HasLetter(string text) => text.Any(char.IsLetter);

    private static int DigitRuns(string text)
    {
        int runs = 0;
        for (int i = 0; i < text.Length; i++)
            if (char.IsDigit(text[i]) && (i == 0 || !char.IsDigit(text[i - 1]))) runs++;
        return runs;
    }

    private static bool NextRow(Rect a, Rect b, out string rule)
    {
        rule = "stacked";
        double height = Math.Max(a.Height, b.Height);
        // Long subtitle rows can have quite different detector padding even at the same font
        // size. Give two substantial, similarly wide rows extra height tolerance; a short name
        // or isolated symbol cannot borrow it from the dialogue beside it.
        bool substantialRows = a.Width >= a.Height * 6 && b.Width >= b.Height * 6 &&
            Math.Min(a.Width, b.Width) >= Math.Max(a.Width, b.Width) * 0.5;
        // Named one test at a time rather than as a single condition, so a refusal can say which
        // test refused it. The order and the numbers are unchanged.
        if (height <= 0 || b.Y <= a.Y) { rule = "not-below"; return false; }
        if (SameRow(a, b)) { rule = "same-row"; return false; }
        if (Math.Min(a.Height, b.Height) < height * (substantialRows ? 0.6 : 0.75))
        { rule = "height-mismatch"; return false; }
        if (b.Top - a.Bottom > height * 0.65) { rule = "gap-too-wide"; return false; }
        if (b.Top - a.Bottom < -height * 0.25) { rule = "overlaps-above"; return false; }
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
            : Math.Min(a.Height, b.Height) < height * 0.6 ? "row-height-mismatch"
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
        bool substantialRows = a.Width >= a.Height * 6 && b.Width >= b.Height * 6 &&
            Math.Min(a.Width, b.Width) >= Math.Max(a.Width, b.Width) * 0.5;
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
            LeadingBar: substantialRows ? 0.6 : 0.75,
            SolidBar: substantialRows ? 2.5 : 1.25,
            Joined: joined, Rule: rule));
    }
}
