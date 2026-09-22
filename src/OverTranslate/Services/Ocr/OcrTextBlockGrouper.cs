using System.Diagnostics;
using System.Windows;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Joins the lines the engine read into the blocks that get translated together.
/// </summary>
/// <remarks>
/// Every measurement a decision here is made on comes from <c>LayoutBounds</c>, the box the
/// detector drew, and never from <c>Bounds</c>. Bounds has been normalised per script by the time
/// it arrives — a CJK line is pulled in onto its glyphs and a Latin one is left whole — so reading
/// it made every threshold mean something different depending on which language the user had
/// selected. The horizontal figures are the same in both (normalisation only touches Y and height);
/// the vertical ones are not, and neither is anything derived from a line height.
///
/// What the group carries out is still Bounds: that is the area the overlay has to cover.
/// </remarks>
internal static class OcrTextBlockGrouper
{
    /// <param name="profile">
    /// The thresholds this pass runs on. Required rather than defaulted: which profile a call
    /// means is the one thing about it that must not be inherited by accident, and the screenshot
    /// and live-screen paths are supposed to be able to diverge (see <see cref="GroupingProfile"/>).
    /// </param>
    internal static List<OcrTextBlock> Group(
        IReadOnlyList<OcrTextBlock> blocks, GroupingProfile profile) => Group(blocks, profile, null);

    /// <param name="decisions">
    /// Collects one entry per line pair the next-line test was asked about, with the geometry it
    /// judged on and which rule decided. Null in the app; OcrHarness passes a list, because these
    /// thresholds cannot be tuned from the grouped output alone — it shows what was joined, never
    /// how close the rest came to being.
    /// </param>
    /// <inheritdoc cref="Group(IReadOnlyList{OcrTextBlock}, GroupingProfile)" path="/param[@name='profile']"/>
    internal static List<OcrTextBlock> Group(
        IReadOnlyList<OcrTextBlock> blocks,
        GroupingProfile profile,
        List<NextLineDecision>? decisions) => Group(blocks, profile, decisions, null);

    /// <param name="trace">
    /// Names each line this pass saw so a diagnostic can refer to them, and collects what the
    /// verdicts and the groups were made of. Null everywhere but the harness, and read by no rule:
    /// a pass with one and a pass without reach the same verdicts on the same input.
    /// </param>
    /// <inheritdoc cref="Group(IReadOnlyList{OcrTextBlock}, GroupingProfile, List{NextLineDecision})"/>
    internal static List<OcrTextBlock> Group(
        IReadOnlyList<OcrTextBlock> blocks,
        GroupingProfile profile,
        List<NextLineDecision>? decisions,
        GroupingTrace? trace)
    {
        AssertLayoutGeometryFilled(blocks);
        trace?.RegisterBlocks(blocks);

        if (blocks.Count <= 1)
        {
            foreach (var block in blocks)
                trace?.RegisterLine(block, [block]);
            trace?.OrderLines(blocks);
            trace?.RegisterGroups(blocks.Select(block => (IReadOnlyList<OcrTextBlock>)[block]).ToList());

            return blocks.ToList();
        }

        // The readings printed above the writing, out before anything measures a leading against
        // them: one sits in the gap between two lines of a paragraph, which is where
        // NothingLiesBetween looks. After the same-line merge, so that the several readings of one
        // line the detector framed separately are judged as the one box it hands on.
        var sameLineMerged = HorizontalRubyLines.Drop(
            MergeSameLineFragments(blocks, decisions, trace, profile));
        var sorted = sameLineMerged
            .OrderBy(block => block.LayoutBounds.Y)
            .ThenBy(block => block.LayoutBounds.X)
            .ToList();

        trace?.OrderLines(sorted);

        var groups = new List<List<OcrTextBlock>>();
        foreach (var block in sorted)
        {
            var target = GroupThisLineContinues(groups, block, sorted, profile, decisions, trace);
            if (target is not null)
                target.Add(block);
            else
                groups.Add([block]);
        }

        trace?.RegisterGroups(groups);

        return groups.Select(BuildGroup).ToList();
    }

    /// <summary>
    /// Every block reaching the grouper must carry the detection box the layout tests measure on.
    /// </summary>
    /// <remarks>
    /// Loud on purpose, and deliberately not a fallback to <c>Bounds</c>. A block that arrives with
    /// nothing in LayoutBounds is a construction path that forgot to fill it, and quietly reading
    /// Bounds instead would put the script-dependent geometry — the 0.820 this whole split exists
    /// to remove — back into grouping without anything saying so. Compiled out of Release, so a
    /// degenerate detection box cannot take the app down; the tests run Debug and will not miss it.
    /// </remarks>
    [Conditional("DEBUG")]
    private static void AssertLayoutGeometryFilled(IReadOnlyList<OcrTextBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block.LayoutBounds.Width > 0 && block.LayoutBounds.Height > 0)
                continue;

            throw new InvalidOperationException(
                $"Block \"{block.Text}\" reached the grouper with no LayoutBounds " +
                $"({block.LayoutBounds}). Fill it where the block is built.");
        }
    }

    /// <summary>
    /// One grouping verdict, with its geometry in line heights so numbers from captures of
    /// different sizes can be read side by side.
    /// </summary>
    /// <remarks>
    /// TWO KINDS SHARE THESE FIELDS AND DO NOT FILL THEM WITH THE SAME QUANTITIES. Anything
    /// aggregating a run has to split on <c>Kind</c> first; pooling the two mixes a horizontal
    /// measurement into a vertical distribution and reads as a pile of zeroes at one end.
    ///
    /// <code>
    ///   field           Kind "next" (line below)        Kind "row" (same line, left to right)
    ///   VerticalGap     vertical gap between lines      HORIZONTAL gap between neighbours
    ///   LeftDelta       left-edge misalignment          vertical overlap rate, 0..1
    ///   CenterDelta     centre misalignment             unused, 0
    ///   RightDelta      right-edge misalignment         unused, 0
    ///   AlignmentDelta  the smallest of those three,    unused, 0
    ///                   which is what the gate read
    ///   TextSizeRatio   glyph or box height ratio       unused, 0
    ///   LineAdvance     baseline advance                unused, 0
    ///   LeadingBar      the advance bar it was judged   unused, 0
    ///                   against                         
    ///   SolidBar        the set-solid limit this group  unused, 0
    ///                   was held to                     
    ///   WidthRatio      width ratio                     width ratio
    ///   SizeBasis       which quantity the size test    None
    ///                   compared, and why
    ///   PreviousSize    the two values it compared,     unused, 0
    ///   CurrentSize     whichever quantity that was
    ///   AdvancePixels   the advance before dividing,    unused, 0
    ///   AdvanceDenom    and what it was divided by
    /// </code>
    ///
    /// The trailing fields carry a default so the same-line kind, which has nothing to say about
    /// any of them, does not have to name them. All of them are diagnostics: no rule reads one.
    ///
    /// Renaming the fields per kind means two records and two code paths through the grouper for
    /// what is one diagnostic; naming them for the vertical case and documenting the reuse is the
    /// cheaper honest option. <c>OcrHarness --group-explain</c> prints each kind with its own
    /// labels, and must keep doing so.
    /// </remarks>
    /// <param name="Kind">"next" for a line-below verdict, "row" for a same-line one.</param>
    /// <param name="Rule">Which test decided, whether it joined or refused.</param>
    internal readonly record struct NextLineDecision(
        string Kind,
        string Previous,
        string Current,
        OcrLayoutScript PreviousScript,
        OcrLayoutScript CurrentScript,
        double VerticalGap,
        double LeftDelta,
        double CenterDelta,
        double RightDelta,
        double AlignmentDelta,
        double TextSizeRatio,
        double WidthRatio,
        double LineAdvance,
        double LeadingBar,
        double SolidBar,
        bool Joined,
        string Rule,
        string PreviousId = "",
        string CurrentId = "",
        TextSizeBasis SizeBasis = TextSizeBasis.None,
        double PreviousSizeValue = 0,
        double CurrentSizeValue = 0,
        double AdvancePixels = 0,
        double AdvanceDenominator = 0);

    private static List<OcrTextBlock> MergeSameLineFragments(
        IReadOnlyList<OcrTextBlock> blocks, List<NextLineDecision>? decisions, GroupingTrace? trace,
        GroupingProfile profile)
    {
        var rows = BuildVisualRows(blocks);
        var gaps = rows.SelectMany(AdjacentGaps).ToList();
        var threshold = SameLineGapThreshold.Estimate(gaps);

        return rows.SelectMany(row => SplitRowIntoLines(row, threshold.Value, decisions, trace, blocks, profile, rows)).ToList();
    }

    /// <summary>
    /// Gathers the boxes that sit on one line of the picture, left to right, whatever the spaces
    /// between them turn out to mean.
    /// </summary>
    /// <remarks>
    /// Membership only, with no gap test. Which spaces are wide enough to separate things cannot be
    /// decided one pair at a time, because it depends on the spacing this particular capture uses,
    /// and that cannot be measured until the rows are known. So the rows are built first and cut
    /// afterwards.
    ///
    /// Sorted by X and not by Y. Latin word boxes on one line have tops that vary with ascenders
    /// and descenders (a real line gave "Send" at y=32 beside "to" at y=37), so a Y-primary sort
    /// interleaves words from across the capture and a row built in that order ends up holding
    /// fragments of several. Reading order keeps a line's own boxes together, and the vertical
    /// overlap test routes each box to the right row when several are open at once.
    /// </remarks>
    private static List<List<OcrTextBlock>> BuildVisualRows(IReadOnlyList<OcrTextBlock> blocks)
    {
        var ordered = blocks
            .OrderBy(block => block.LayoutBounds.X)
            .ThenBy(block => block.LayoutBounds.Y)
            .ToList();
        var rows = new List<List<OcrTextBlock>>();

        foreach (var block in ordered)
        {
            var bestRow = -1;
            var bestOverlap = double.NegativeInfinity;

            for (var i = 0; i < rows.Count; i++)
            {
                // Against the rightmost box of the row, which is the one this would follow.
                var rightmost = rows[i][^1];
                if (!SharesVisualRow(rightmost, block)) continue;

                var overlap = VerticalOverlapRate(rightmost, block);
                if (overlap <= bestOverlap) continue;

                bestOverlap = overlap;
                bestRow = i;
            }

            if (bestRow >= 0) rows[bestRow].Add(block);
            else rows.Add([block]);
        }

        return rows;
    }

    /// <summary>Whether two boxes sit on the same line of the picture. Says nothing about joining.</summary>
    private static bool SharesVisualRow(OcrTextBlock previous, OcrTextBlock current)
    {
        // Vertical overlap — NOT height ratio — is what tells an in-line word from a distinct
        // neighbour. A short mid-line word ("to" h=25 on a h=31 line, heightRatio 0.81) sits on the
        // same baseline, so its box is fully nested in the line (overlap about 1.0). Two stacked
        // buttons ("Download…report" h=32 next to a vertically offset "Create key" h=38,
        // heightRatio 0.84) overlap only about 0.47. Their height ratios are inverted
        // (0.81 < 0.84), so any single height threshold either drops "to" or merges the buttons.
        // Keep height tolerant and let the strict overlap test do the discriminating.
        //
        // 0.5 rather than 0.6, measured: a subtitle reading "Let us pay CiRCLE a visit on the way
        // home." came back as an 888x88 box and a 141x51 one holding "home", a height ratio of
        // 0.58. They sit 2px apart with the shorter box entirely inside the taller one's rows, and
        // the guard rejected them anyway, so "home" was translated on its own and appeared as a
        // stray word to the right of a sentence missing its ending. The word's box is short because
        // the word has no descender, which is a property of the letters, not of whether they belong
        // to the line.
        var heightRatio = Math.Min(previous.LayoutBounds.Height, current.LayoutBounds.Height) /
                          Math.Max(previous.LayoutBounds.Height, current.LayoutBounds.Height);

        return heightRatio >= MinimumRowHeightRatio &&
               VerticalOverlapRate(previous, current) >= MinimumRowVerticalOverlap;
    }

    private static double VerticalOverlapRate(OcrTextBlock previous, OcrTextBlock current)
    {
        var overlap = Math.Max(
            0,
            Math.Min(previous.LayoutBounds.Bottom, current.LayoutBounds.Bottom) -
            Math.Max(previous.LayoutBounds.Top, current.LayoutBounds.Top));

        return overlap /
               Math.Max(1, Math.Min(previous.LayoutBounds.Height, current.LayoutBounds.Height));
    }

    /// <summary>The space before each box in a row, in line heights, neighbour to neighbour.</summary>
    private static IEnumerable<double> AdjacentGaps(List<OcrTextBlock> row) =>
        row.Zip(row.Skip(1), NormalizedGap);

    private static double NormalizedGap(OcrTextBlock previous, OcrTextBlock current) =>
        (current.LayoutBounds.X - previous.LayoutBounds.Right) /
        Math.Max(1, (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0);

    /// <summary>
    /// Cuts one row wherever the space between neighbours is too wide to be a space between words,
    /// and joins what is left.
    /// </summary>
    private static IEnumerable<OcrTextBlock> SplitRowIntoLines(
        List<OcrTextBlock> row, double threshold, List<NextLineDecision>? decisions, GroupingTrace? trace,
        IReadOnlyList<OcrTextBlock> blocks, GroupingProfile profile, IReadOnlyList<List<OcrTextBlock>> rows)
    {
        var lines = new List<OcrTextBlock>();
        var line = row[0];

        // What the line being built is made of, kept as it is built rather than worked out
        // afterwards: a merged line is a new instance, and nothing on it points back at the blocks
        // it came from.
        var members = new List<OcrTextBlock> { row[0] };

        for (var i = 1; i < row.Count; i++)
        {
            var (joined, rule) = JudgeSameLine(row[i - 1], row[i], threshold);
            if (!joined && rule == "horizontal gap" &&
                profile.SolidLineAdvanceWhenWrapped > SolidLineAdvance &&
                HasSpanningProseContinuation(row[i - 1], row[i], blocks))
                (joined, rule) = (true, "spanning prose continuation");
            if (!joined && rule == "horizontal gap" &&
                profile.SolidLineAdvanceWhenWrapped > SolidLineAdvance &&
                HasParagraphRowEvidence(row, i, rows, threshold))
                (joined, rule) = (true, "paragraph row evidence");
            decisions?.Add(SameLineDecision(row[i - 1], row[i], joined, rule, trace));

            if (joined)
            {
                line = MergeSameLine(line, row[i]);
                members.Add(row[i]);
                continue;
            }

            lines.Add(line);
            trace?.RegisterLine(line, members);
            line = row[i];
            members = [row[i]];
        }

        lines.Add(line);
        trace?.RegisterLine(line, members);
        return lines;
    }

    // A dash or another thin separator may not become an OCR block at all. Do not depend on
    // its recognised spelling: a following line spanning both fragments supplies the wrap
    // evidence that two independent buttons/columns lack. Keep the ordinary row guards.
    private static bool HasSpanningProseContinuation(
        OcrTextBlock left, OcrTextBlock right, IReadOnlyList<OcrTextBlock> blocks)
    {
        var height = (left.LayoutBounds.Height + right.LayoutBounds.Height) / 2;
        if (!SharesVisualRow(left, right) || NormalizedGap(left, right) > 1.2 ||
            left.LayoutBounds.Width < left.LayoutBounds.Height * 8 ||
            right.LayoutBounds.Width < right.LayoutBounds.Height * 8)
            return false;
        var row = Rect.Union(left.LayoutBounds, right.LayoutBounds);
        return blocks.Any(next =>
            next.LayoutBounds.Top >= row.Bottom - height * 0.2 &&
            next.LayoutBounds.Top - row.Top <= height * 1.65 &&
            Math.Abs(next.LayoutBounds.Left - row.Left) <= height * 0.35 &&
            Math.Abs(next.LayoutBounds.Right - row.Right) <= height * 0.75 &&
            next.LayoutBounds.Height >= height * 0.75 &&
            next.LayoutBounds.Height <= height * 1.35);
    }

    // Use several original visual rows, not the result of speculative merges. A complete
    // nearby row anchors the column; changing cut positions suggest missed inline punctuation.
    // A recurring gutter is counter-evidence even when a shared heading spans both columns.
    private static bool HasParagraphRowEvidence(List<OcrTextBlock> row, int rightIndex,
        IReadOnlyList<List<OcrTextBlock>> rows, double threshold)
    {
        var left = row[rightIndex - 1];
        var right = row[rightIndex];
        if (NormalizedGap(left, right) > 1.5 || !SharesVisualRow(left, right)) return false;
        var bounds = RowBounds(row);
        var height = bounds.Height;
        if (bounds.Width < height * 12 || row.Any(b => b.LayoutScript != OcrLayoutScript.Latin)) return false;
        var cut = (left.LayoutBounds.Right + right.LayoutBounds.Left) / 2;
        var supporting = new List<List<OcrTextBlock>>();
        foreach (var other in rows)
        {
            if (ReferenceEquals(row, other)) continue;
            var box = RowBounds(other);
            if (other.Any(b => b.LayoutScript != OcrLayoutScript.Latin) ||
                box.Height < height * 0.75 || box.Height > height * 1.35 ||
                Math.Abs(box.Top - bounds.Top) < height * 0.5 ||
                Math.Abs(box.Top - bounds.Top) > height * 2.5 ||
                Math.Abs(box.Left - bounds.Left) > height * 0.5 ||
                Math.Abs(box.Right - bounds.Right) > height * 0.75) continue;
            for (var i = 1; i < other.Count; i++)
            {
                var (joined, _) = JudgeSameLine(other[i - 1], other[i], threshold);
                var otherCut = (other[i - 1].LayoutBounds.Right + other[i].LayoutBounds.Left) / 2;
                if (!joined && Math.Abs(otherCut - cut) <= height) return false;
            }
            supporting.Add(other);
        }
        if (supporting.Count < 2 || !supporting.Any(r => r.Count == 1)) return false;
        var tops = supporting.Select(RowBounds).Append(bounds).OrderBy(b => b.Top).ToArray();
        for (var i = 1; i < tops.Length; i++)
            if (tops[i].Top - tops[i - 1].Top > Math.Min(tops[i].Height, tops[i - 1].Height) * 1.45)
                return false;
        return true;

        static Rect RowBounds(List<OcrTextBlock> r) => r.Select(b => b.LayoutBounds).Aggregate(Rect.Union);
    }

    private static (bool Joined, string Rule) JudgeSameLine(
        OcrTextBlock previous, OcrTextBlock current, double threshold)
    {
        var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;

        // The gap can be negative: on large captures the detector's unclip expansion enlarges big
        // heading word-boxes until adjacent ones overlap horizontally (e.g. "Translate" right=533
        // vs "your website" left=515, a gap of -18), which a non-negative guard rejected, scattering
        // one heading into word-by-word translations. Allow up to a line-height of overlap; the
        // checks in SharesVisualRow already keep stacked lines out.
        var horizontalGap = current.LayoutBounds.X - previous.LayoutBounds.Right;
        if (horizontalGap < -avgHeight)
            return (false, "overlaps too far");

        // The floor is for text too small for a ratio to mean much. It was 18px, which at the sizes
        // it applied to was itself wide enough to cross a menu.
        return horizontalGap <= Math.Max(avgHeight * threshold, MinimumRowGapPixels)
            ? (true, "same row")
            : (false, "horizontal gap");
    }

    /// <summary>
    /// How close in text size two lines have to be before one can be the other wrapping.
    /// </summary>
    /// <remarks>
    /// Named rather than written into the test because <see cref="GroupingProfile"/> quotes it: a
    /// profile that says "the ordinary figure" has to be equal to the ordinary figure, and two
    /// copies of 0.88 in two files is one of them being moved on its own eventually.
    /// </remarks>
    internal const double MinTextSizeRatio = 0.88;

    /// <inheritdoc cref="SharesVisualRow"/>
    private const double MinimumRowHeightRatio = 0.5;

    /// <inheritdoc cref="SharesVisualRow"/>
    private const double MinimumRowVerticalOverlap = 0.72;

    /// <inheritdoc cref="JudgeSameLine"/>
    private const double MinimumRowGapPixels = 6;

    private static NextLineDecision SameLineDecision(
        OcrTextBlock previous, OcrTextBlock current, bool joined, string rule, GroupingTrace? trace) =>
        new("row",
            previous.Text,
            current.Text,
            previous.LayoutScript,
            current.LayoutScript,
            NormalizedGap(previous, current),
            VerticalOverlapRate(previous, current),
            // Centre, right, the alignment figure taken from them, size, advance and the leading
            // bar are vertical quantities. A same-line verdict has no such thing to report, so they
            // stay at zero and the harness does not print them for this kind.
            0,
            0,
            0,
            0,
            previous.LayoutBounds.Width / Math.Max(1, current.LayoutBounds.Width),
            0,
            0,
            0,
            joined,
            rule,
            // The detected blocks, not the lines: this verdict is about what a line gets built out
            // of, and the line it ends up in does not exist yet.
            trace?.BlockId(previous) ?? "",
            trace?.BlockId(current) ?? "");

    private static OcrTextBlock MergeSameLine(OcrTextBlock previous, OcrTextBlock current)
    {
        var left = Math.Min(previous.Bounds.Left, current.Bounds.Left);
        var top = Math.Min(previous.Bounds.Top, current.Bounds.Top);
        var right = Math.Max(previous.Bounds.Right, current.Bounds.Right);
        var bottom = Math.Max(previous.Bounds.Bottom, current.Bounds.Bottom);
        var text = JoinInlineText(previous.Text, current.Text);
        var layoutScript = LayoutScriptDetection.For(text);

        return new OcrTextBlock(
            text,
            new Rect(left, top, right - left, bottom - top),
            RenderGlyphHeight: CombineGlyphHeight(previous.RenderGlyphHeight, current.RenderGlyphHeight),
            Confidence: CombineConfidence([previous, current]),
            LayoutScript: layoutScript,
            LayoutBounds: Rect.Union(previous.LayoutBounds, current.LayoutBounds),
            LayoutGlyphHeight: CombineLayoutGlyphHeight(layoutScript, [previous, current]));
    }

    internal static string JoinInlineText(string left, string right)
    {
        left = left.TrimEnd();
        right = right.TrimStart();
        if (left.Length == 0)
            return right;
        if (right.Length == 0)
            return left;

        var needsSpace = char.IsAsciiLetterOrDigit(left[^1]) && char.IsAsciiLetterOrDigit(right[0]);
        return needsSpace ? $"{left} {right}" : $"{left}{right}";
    }

    /// <summary>
    /// Which of the groups opened so far this line carries on, or null if it starts a new one.
    /// </summary>
    /// <remarks>
    /// <para>This used to ask only the group opened last, which is the right question for a single
    /// column and the wrong one for anything else. Reading order down a two-column page alternates
    /// between the columns, so a line's own previous line is often not the one immediately before it
    /// in this list — a Japanese event page has a title whose two halves sit at (1340,612) and
    /// (1339,650), one pixel apart on the left and about one line advance down, with a heading from
    /// the next column at (1912,615) sorted between them. That pair was never refused: it was never
    /// asked about at all.</para>
    ///
    /// <para>Asking every open group means several may answer yes, so the choice between them is
    /// fixed here rather than left to whichever the loop happened to see first. The order is part of
    /// the rule: closest alignment wins; a tie inside a hundredth of a line height goes to the
    /// vertically nearer; a tie there goes to the more recently opened group, which is the scan
    /// order. The last of those settles nothing on the evidence — it is there so that the same
    /// input always produces the same grouping.</para>
    ///
    /// <para>Only the group opened last is asked unconditionally. The rest have to be within reach
    /// vertically first, which changes no verdict — the reach test is the same vertical gap the
    /// judgement applies — and keeps the trace from filling with every pair of lines on the page.
    /// </para>
    /// </remarks>
    private static List<OcrTextBlock>? GroupThisLineContinues(
        List<List<OcrTextBlock>> groups,
        OcrTextBlock current,
        IReadOnlyList<OcrTextBlock> lines,
        GroupingProfile profile,
        List<NextLineDecision>? decisions,
        GroupingTrace? trace)
    {
        List<OcrTextBlock>? best = null;
        var bestAlignment = double.PositiveInfinity;
        var bestDistance = double.PositiveInfinity;

        // Back to front, so the group opened last is the first one seen and keeps a tie by having
        // been chosen already.
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var group = groups[i];
            var previous = group[^1];

            var isNearestGroup = i == groups.Count - 1;
            if (!isNearestGroup && !IsWithinContinuationReach(previous, current))
                continue;

            if (!NothingLiesBetween(previous, current, lines))
                continue;

            if (!CanJoinNextLine(group, current, profile, decisions, trace, lines))
                continue;

            var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;
            var alignment = AlignmentDelta(previous, current) / Math.Max(1, avgHeight);
            var distance = (current.LayoutBounds.Y - previous.LayoutBounds.Y) / Math.Max(1, avgHeight);

            if (best is null ||
                alignment < bestAlignment - AlignmentTieBand ||
                (Math.Abs(alignment - bestAlignment) <= AlignmentTieBand && distance < bestDistance))
            {
                best = group;
                bestAlignment = alignment;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// How close two alignment figures have to be before they count as the same, in line heights.
    /// </summary>
    /// <remarks>
    /// A hundredth of a line height is well under a pixel at the sizes this runs at, so two groups
    /// separated by less than it are not really being told apart by alignment and the next test
    /// should decide instead. If the later tie-breaks turn out to be doing much of the work, that is
    /// a sign the alignment figure is too coarse to choose on, and worth reporting rather than
    /// accepting.
    /// </remarks>
    private const double AlignmentTieBand = 0.01;

    /// <summary>
    /// Whether a group is close enough vertically to be worth judging at all.
    /// </summary>
    /// <remarks>
    /// The same limits <see cref="JudgeNextLine"/> applies, so a group filtered out here would have
    /// been refused there for the same reason. Its only job is to stop the work — and the trace —
    /// growing with every pair of lines on a page rather than with the lines themselves.
    /// </remarks>
    private static bool IsWithinContinuationReach(OcrTextBlock previous, OcrTextBlock current)
    {
        var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;
        var verticalGap = current.LayoutBounds.Y - previous.LayoutBounds.Bottom;

        return verticalGap >= -avgHeight * 0.5 && verticalGap <= Math.Max(avgHeight * 0.8, 10);
    }

    /// <summary>
    /// Whether the space between two lines, in the column they share, is empty.
    /// </summary>
    /// <remarks>
    /// <para>Now that every open group is asked, two lines can be judged as neighbours with a third
    /// line sitting between them on the page. A news front page is the shape this is for: a
    /// headline, its standfirst, then the next headline, all in one column and all aligned. Without
    /// this the first and third read as a plausible continuation of each other and get strung
    /// together with the standfirst left out of the middle of them.</para>
    ///
    /// <para>Only the horizontal span the two lines actually share is examined, and a third line
    /// counts as being in the way when its own middle falls in the gap. A line beside the column, or
    /// one clipping into it by a few pixels of unclipped detection box, is not in the way of
    /// anything.</para>
    /// </remarks>
    private static bool NothingLiesBetween(
        OcrTextBlock previous, OcrTextBlock current, IReadOnlyList<OcrTextBlock> lines)
    {
        var left = Math.Max(previous.LayoutBounds.Left, current.LayoutBounds.Left);
        var right = Math.Min(previous.LayoutBounds.Right, current.LayoutBounds.Right);
        if (right <= left)
            return true;

        var top = previous.LayoutBounds.Bottom;
        var bottom = current.LayoutBounds.Top;
        if (bottom <= top)
            return true;

        foreach (var line in lines)
        {
            if (ReferenceEquals(line, previous) || ReferenceEquals(line, current))
                continue;

            var box = line.LayoutBounds;
            if (box.Right <= left || box.Left >= right)
                continue;

            var middle = box.Y + box.Height / 2.0;
            if (middle > top && middle < bottom)
                return false;
        }

        return true;
    }

    // A rejected same-row fragment must not be skipped by a wrap into its horizontal span.
    // Check both ends: a full line may also take only half of the next visual row. Separate
    // columns remain independent when the continuation does not cross the neighbouring column.
    private static bool HasUnresolvedRowFragment(
        OcrTextBlock previous, OcrTextBlock current, IReadOnlyList<OcrTextBlock> lines)
    {
        foreach (var other in lines)
        {
            if (ReferenceEquals(other, previous) || ReferenceEquals(other, current)) continue;
            if (Conflicts(previous, current, other) || Conflicts(current, previous, other)) return true;
        }
        return false;

        static bool Conflicts(OcrTextBlock rowPart, OcrTextBlock continuation, OcrTextBlock other)
        {
            if (!SharesVisualRow(rowPart, other)) return false;
            var overlap = Math.Min(continuation.LayoutBounds.Right, other.LayoutBounds.Right) -
                          Math.Max(continuation.LayoutBounds.Left, other.LayoutBounds.Left);
            // Ignore a few pixels of detector expansion at touching column edges.
            return overlap > Math.Min(continuation.LayoutBounds.Height, other.LayoutBounds.Height) * 0.2;
        }
    }

    private static bool CanJoinNextLine(
        List<OcrTextBlock> group,
        OcrTextBlock current,
        GroupingProfile profile,
        List<NextLineDecision>? decisions,
        GroupingTrace? trace,
        IReadOnlyList<OcrTextBlock> lines)
    {
        var previous = group[^1];
        var paragraphFinal = IsEstablishedParagraphFinalLine(group, current, profile);
        var looseProse = IsLooselySetProse(previous, current, profile, lines);
        var (joined, rule) = JudgeNextLine(previous, current, profile, paragraphFinal, looseProse);
        if (joined && looseProse && LineAdvanceRatio(previous, current) > SolidBarFor(previous, profile))
            rule = "regular prose leading";
        if (joined && paragraphFinal &&
            TextSizeRatio(previous, current) < Math.Min(MinTextSizeRatio, profile.TightlySetMinTextSizeRatio))
            rule = "paragraph final line";
        if (joined && HasUnresolvedRowFragment(previous, current, lines))
            (joined, rule) = (false, "unresolved row fragment");
        if (decisions is null)
            return joined;

        var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;
        var sizeRatio = TextSizeRatio(previous, current, out var sizeBasis, out var previousSize, out var currentSize);
        LineAdvanceRatio(previous, current, out var advancePixels, out var advanceDenominator);
        decisions.Add(new NextLineDecision(
            "next",
            previous.Text,
            current.Text,
            previous.LayoutScript,
            current.LayoutScript,
            (current.LayoutBounds.Y - previous.LayoutBounds.Bottom) / Math.Max(1, avgHeight),
            Math.Abs(previous.LayoutBounds.X - current.LayoutBounds.X) / Math.Max(1, avgHeight),
            Math.Abs(CenterX(previous) - CenterX(current)) / Math.Max(1, avgHeight),
            Math.Abs(previous.LayoutBounds.Right - current.LayoutBounds.Right) / Math.Max(1, avgHeight),
            // From the same function the rule read, not recomputed here: a trace that works out the
            // verdict a second way is a trace that can disagree with the verdict.
            AlignmentDelta(previous, current) / Math.Max(1, avgHeight),
            sizeRatio,
            previous.LayoutBounds.Width / Math.Max(1, current.LayoutBounds.Width),
            LineAdvanceRatio(previous, current),
            WrappedFinalLineAdvance,
            // The bar this pair was judged on, not the constant. Which of the two set-solid limits
            // applies depends on the pair, so printing one of them unconditionally would put a
            // number in the trace the rule never read — and mislabelled columns in this tool have
            // already cost a corpus rerun twice.
            SolidBarFor(previous, profile),
            joined,
            rule,
            trace?.LineId(previous) ?? "",
            trace?.LineId(current) ?? "",
            // Which quantity the size test compared and why, from the test itself. Reading it back
            // off the ratio cannot tell the three apart, and two of them are refused for reasons
            // that need opposite fixes.
            sizeBasis,
            previousSize,
            currentSize,
            advancePixels,
            advanceDenominator));

        return joined;
    }

    /// <summary>
    /// The leading: how far the second line sits below the first, in detection-box heights. One
    /// line advance is 1.0, so a paragraph reads a little under it and anything laid out on purpose
    /// reads well over.
    /// </summary>
    /// <remarks>
    /// Measured on <see cref="OcrTextBlock.LayoutBounds"/>, which is what makes a leading test
    /// possible at all. Against the normalised box the same typographic leading comes back as two
    /// different fractions — a Japanese Wikipedia paragraph read 0.37 between its lines where an
    /// English one read 0.09 — so the two populations overlapped and no threshold separated them.
    /// On the detector's own box they agree.
    /// </remarks>
    private static double LineAdvanceRatio(OcrTextBlock previous, OcrTextBlock current)
        => LineAdvanceRatio(previous, current, out _, out _);

    /// <param name="advancePixels">How far down the second line sits, before the division.</param>
    /// <param name="denominator">
    /// What that was divided by. Printed because this is the quantity under suspicion: the signal
    /// is the advance and the noise is here, and a ratio alone does not say which of the two moved.
    /// </param>
    /// <inheritdoc cref="LineAdvanceRatio(OcrTextBlock, OcrTextBlock)"/>
    private static double LineAdvanceRatio(
        OcrTextBlock previous, OcrTextBlock current, out double advancePixels, out double denominator)
    {
        var box = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;

        advancePixels = current.LayoutBounds.Y - previous.LayoutBounds.Y;
        denominator = box;

        return box > 0 ? advancePixels / box : -1;
    }

    /// <summary>
    /// The horizontal centre of a line's detection box.
    /// </summary>
    private static double CenterX(OcrTextBlock line) =>
        line.LayoutBounds.X + line.LayoutBounds.Width / 2.0;

    /// <summary>
    /// How far out of line two lines are, in pixels: the closest of their left edges, their right
    /// edges and their centres.
    /// </summary>
    /// <remarks>
    /// <para>This used to read the left edges and nothing else, which is a test for text set flush
    /// left and a coin toss for anything else. Centred speech moves its left edge by half the
    /// difference in line length, so across the ten comic pages twenty-one pairs were refused as
    /// misaligned while their centres sat within a twentieth of a line of each other — including
    /// every bubble that opens on a short line, which is most of them.</para>
    ///
    /// <para>The right edge earns its place separately, and it is not symmetry for its own sake: a
    /// stat panel's body text is set flush right, so its consecutive lines read 6.35 and 7.60 line
    /// heights apart on the left and 0.00 on the right. Those pairs are ones the hand-marked
    /// grouping says belong together. What must stay apart there — a short label above that body —
    /// is far out on all three edges (3.18 / 6.33 / 9.49), so taking the smallest does not put it
    /// at risk.</para>
    ///
    /// <para>The thresholds this feeds do not move. The measurement was wrong for anything not set
    /// flush left; the limits on it were never the problem.</para>
    /// </remarks>
    private static double AlignmentDelta(OcrTextBlock previous, OcrTextBlock current) =>
        Math.Min(
            Math.Abs(previous.LayoutBounds.X - current.LayoutBounds.X),
            Math.Min(
                Math.Abs(previous.LayoutBounds.Right - current.LayoutBounds.Right),
                Math.Abs(CenterX(previous) - CenterX(current))));

    private static (bool Joined, string Rule) JudgeNextLine(
        OcrTextBlock previous, OcrTextBlock current, GroupingProfile profile,
        bool paragraphFinal = false, bool looseProse = false)
    {
        var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;

        // Two size gates, not one. This is the floor — below it nothing can join on any evidence —
        // and it is the lower of the ordinary figure and whatever the profile allows a set-solid
        // pair. Everything between the two is decided further down, once the geometry has said
        // whether the pair is set solid at all, because that is the only path a profile may relax.
        var sizeRatio = TextSizeRatio(previous, current);
        // A shorter, visibly larger heading above a different-script body.
        if (profile.SolidLineAdvanceWhenWrapped > SolidLineAdvance &&
            previous.LayoutScript != current.LayoutScript &&
            previous.LayoutBounds.Width < current.LayoutBounds.Width &&
            previous.LayoutInkHeight is > 0 && current.LayoutInkHeight is > 0 &&
            current.LayoutInkHeight < previous.LayoutInkHeight * 0.85)
            return (false, "visible heading size");
        if (sizeRatio < Math.Min(MinTextSizeRatio, profile.TightlySetMinTextSizeRatio) && !paragraphFinal)
            return (false, "text size");

        // The gap can be negative, for exactly the reason it can horizontally in JudgeSameLine:
        // the detector's unclip expansion grows every box a little past its glyphs, so two lines of
        // one wrapped sentence routinely come back overlapping by a few pixels. A `< 0` guard threw
        // those apart, and each half was then translated on its own — which is not a cosmetic
        // problem, because half a sentence is not half a translation. A game panel reading "Shots
        // deal more damage for each bullet remaining in the" / "magazine" overlapped by 3px, and
        // came back as 「每發子彈在」 and 「雜誌」; the whole sentence gives 「彈匣內每發子彈的傷害會增加」.
        //
        // Half a line of overlap, from measuring 54 candidate pairs across the logged captures. The
        // two populations do not touch: wrapped continuations land at -0.4 to -0.1 of a line, while
        // boxes that share a row (an "E" beside "PICK UP", an "X" beside "HOLD TO SALVAGE") land at
        // -0.9 to -1.0. Nothing at all falls between, so the threshold sits in empty space rather
        // than on either edge. Those row-mates are what this really has to keep out; the same-line
        // merge takes most of them first, and this is the backstop for the rest.
        var verticalGap = current.LayoutBounds.Y - (previous.LayoutBounds.Y + previous.LayoutBounds.Height);
        if (verticalGap < -avgHeight * 0.5 || verticalGap > Math.Max(avgHeight * 0.8, 10))
            return (false, "vertical gap");

        var japaneseProse = IsJapaneseProseContinuation(previous, current, profile);
        var alignmentDelta = AlignmentDelta(previous, current);
        if (alignmentDelta > Math.Max(avgHeight * 1.2, 18) && !japaneseProse)
            return (false, "alignment");

        var overlap = Math.Max(
            0,
            Math.Min(previous.LayoutBounds.Right, current.LayoutBounds.Right) -
            Math.Max(previous.LayoutBounds.Left, current.LayoutBounds.Left));
        var overlapRate = overlap / Math.Max(1, Math.Min(previous.LayoutBounds.Width, current.LayoutBounds.Width));
        var isAlignedContinuation =
            overlapRate >= 0.35 || alignmentDelta <= Math.Max(avgHeight * 0.7, 12);
        if (!isAlignedContinuation)
            return (false, "not aligned enough to continue");

        return SentenceContinuationEvidence(previous, current, profile, sizeRatio, looseProse, japaneseProse);
    }

    /// <summary>
    /// How close two lines are in text size, for deciding whether the second can be the first
    /// wrapping. Glyph heights when both sides carry one, the detection boxes otherwise.
    /// </summary>
    /// <remarks>
    /// <para>Comparing boxes was measuring the wrong thing. A Latin box runs about half again as
    /// tall as the letters inside it, and how much of that slack there is depends on which letters
    /// the word happens to contain — "magazine" has no ascender, so its box came back 26px under a
    /// 30px line it belonged to, a ratio of 0.867 that this gate refused by 0.013. The same
    /// sentence, re-read as the picture moved, gave anything from 0.68 to 1.00 across eight passes:
    /// text that merged or did not depending on the frame.</para>
    ///
    /// <para>This is the argument <see cref="SharesVisualRow"/> already makes about its own height
    /// test — box height is a property of the letters, not of whether they belong together — and the
    /// answer there was to keep the test tolerant. Here there is a better one available: the engine
    /// already measures the ink for Latin lines, because both overlays need it to size their font.
    /// On that number the same pair reads 0.937, and it is steady.</para>
    ///
    /// <para>Measured over the corpus, switching the comparison admits three pairs — the sentence
    /// above and one more wrapped English line — and separates none that were already joined. The
    /// threshold does not move; it was never the problem. See issue #79.</para>
    ///
    /// <para>Glyph heights are compared only within one script. A Latin box carries ascender and
    /// descender room a CJK one does not, so the two numbers do not mean the same thing, and no
    /// constant converts between them — that was measured, and the ratio turned out to be a
    /// property of the particular words rather than of the scripts.</para>
    ///
    /// <para>Everything else falls back to the raw detection boxes, which is coarse but at least
    /// impartial: one detector, one procedure, whatever the text is. It is what the old code did
    /// too, except that it compared the *normalised* boxes — and those are 0.820 of each other
    /// across scripts before a single letter is read, so every Latin line beside a CJK one was
    /// refused by a size test it could not have passed. That is issue #164's 「OPTIONS／ゲーム設定」.</para>
    /// </remarks>
    internal static double TextSizeRatio(OcrTextBlock previous, OcrTextBlock current)
        => TextSizeRatio(previous, current, out _, out _, out _);

    /// <param name="basis">
    /// Which quantity was compared, and which of the two reasons sent it to the boxes. The
    /// distinction is the whole diagnostic value: a pair that fell back because one side carries no
    /// estimate is fixed by estimating it, and a pair whose scripts differ would still fall back
    /// with both estimates in hand, because the scripts are the first thing this asks about.
    /// </param>
    /// <param name="previousCompared">The two values compared, in whichever quantity that was.</param>
    /// <inheritdoc cref="TextSizeRatio(OcrTextBlock, OcrTextBlock)"/>
    internal static double TextSizeRatio(
        OcrTextBlock previous,
        OcrTextBlock current,
        out TextSizeBasis basis,
        out double previousCompared,
        out double currentCompared)
    {
        if (previous.LayoutScript == current.LayoutScript &&
            previous.LayoutGlyphHeight is { } previousGlyph and > 0 &&
            current.LayoutGlyphHeight is { } currentGlyph and > 0)
        {
            basis = TextSizeBasis.Glyph;
            previousCompared = previousGlyph;
            currentCompared = currentGlyph;

            return Math.Min(previousGlyph, currentGlyph) / Math.Max(previousGlyph, currentGlyph);
        }

        // Named in the order the condition above asks: differing scripts alone send a pair here
        // whether or not both sides carry an estimate.
        basis = previous.LayoutScript != current.LayoutScript
            ? TextSizeBasis.BoxDifferentScript
            : TextSizeBasis.BoxNoGlyphHeight;
        previousCompared = previous.LayoutBounds.Height;
        currentCompared = current.LayoutBounds.Height;

        return Math.Min(previous.LayoutBounds.Height, current.LayoutBounds.Height) /
               Math.Max(previous.LayoutBounds.Height, current.LayoutBounds.Height);
    }

    private static (bool Joined, string Rule) SentenceContinuationEvidence(
        OcrTextBlock previous, OcrTextBlock current, GroupingProfile profile, double sizeRatio,
        bool looseProse = false, bool japaneseProse = false)
    {
        var previousText = previous.Text.Trim();
        var currentText = current.Text.Trim();
        if (previousText.Length == 0 || currentText.Length == 0)
            return (false, "empty text");

        // The two refusals below are asked before any evidence for joining, because both of the
        // shapes they describe also look like evidence. Asked afterwards they would never be
        // reached: a colon is already read as a clause carrying on, and a bulleted line under a
        // longer one is already the shape of a paragraph's last line.
        if (StartsWithListBullet(currentText))
            return (false, "list bullet");

        // The same argument as the bullet, for a chat log — see GroupingProfile.RefuseSpeakerLineStarts.
        if (profile.RefuseSpeakerLineStarts && SpeakerLineStart.Opens(currentText))
            return (false, "speaker line start");

        if (profile.RefuseSpeakerLineStarts && SpeakerLineStart.IsBareLabel(previousText))
            return (false, "speaker label");

        if (IsNumericRow(currentText))
            return (false, "numeric row");

        if (EndsWithLabelColon(previousText) &&
            LineAdvanceRatio(previous, current) > SolidLineAdvance)
            return (false, "label colon");

        if (japaneseProse)
            return (true, "Japanese prose rows");

        // The ordinary size gate, for every pair that is not set solid. A profile may lower what it
        // asks of lines sharing one leading and one edge; it may not lower what it asks of a pair
        // that has neither — so a pair below the ordinary ratio only gets past here by being set
        // solid, and then only the set-solid rule below can join it. Under a profile that lowers
        // nothing this line never fires: the caller has already refused everything under the
        // ordinary ratio, which is what keeps the standard mode's verdicts exactly as they were.
        if (sizeRatio < MinTextSizeRatio && !IsSetSolidUnder(previous, current, profile))
            return (false, "text size");

        if (HasUnclosedDelimiter(previousText) ||
            EndsWithContinuationPunctuation(previousText) ||
            StartsWithContinuationPunctuation(currentText))
            return (true, "punctuation");

        if (EndsWithSentenceTerminator(previousText))
            return (false, "sentence terminator");

        // Lines set solid under one another: same leading, same edge, no width difference to read.
        // Asked before the shape test below because the shape test cannot see them — a paragraph's
        // middle lines are all about as wide as each other, which is the one thing that rule takes
        // as proof that nothing wrapped.
        if (IsSetSolidUnder(previous, current, profile) || looseProse)
            return (true, "set solid");

        // A much shorter following line is a common natural wrap shape.
        // Similar-width lines without linguistic evidence are kept separate
        // so title/body pairs are not merged just because they align.
        //
        // Guarded by the length test below, because on its own that shape is also what a stack of
        // menu entries looks like — a longer label above a shorter one, aligned, evenly spaced. It
        // read 「アビリティ」over「召喚石」and 「캐릭터강화」over「소지품」as wrapped sentences, glued
        // each pair into one string for the translator, and squeezed both into one bubble. See #75.
        if (!IsLongEnoughToHaveWrapped(previous) ||
            previous.LayoutBounds.Width < current.LayoutBounds.Width * 1.35)
            return (false, "no continuation evidence");

        // A paragraph's last line is set at the paragraph's leading, so a pair spaced further apart
        // than that is not a paragraph ending — it is a heading over the thing it labels, or the
        // next entry in a list. This rule had no leading test at all, which is how a settings panel
        // joined "Reveal all rooms before proceeding to next floor" to the unrelated checkbox under
        // it purely because that one was shorter.
        return LineAdvanceRatio(previous, current) <= WrappedFinalLineAdvance
            ? (true, "shorter final line")
            : (false, "leading");
    }

    /// <summary>
    /// Whether a line is long enough that running out of room — and so wrapping — is plausible.
    /// </summary>
    /// <remarks>
    /// <para>Measured in line heights rather than characters so that one number serves every script:
    /// a CJK line runs about one character per line height, a Latin one about two. So this asks for
    /// roughly eight CJK characters, or sixteen Latin ones.</para>
    ///
    /// <para>Across the 329-image corpus the two populations are far apart: every genuinely wrapped
    /// sentence reached 16.6 and every stacked label stopped at 6.8, with nothing in between. The
    /// threshold sits at the low end of that empty band on purpose. Letting a label through costs
    /// two crowded words in one bubble; keeping a real wrapped sentence apart costs the translator
    /// half a sentence, which is the far worse failure — it is the whole of #74.</para>
    /// </remarks>
    private const double WrappedLineMinAspect = 8.0;

    /// <summary>
    /// The most leading a shorter following line can have and still be the end of the paragraph
    /// above it.
    /// </summary>
    /// <remarks>
    /// <para>Measured over the image corpus on LayoutBounds, after the row rule had stopped side by
    /// side things being joined: every pair that is really one sentence wrapping sits at or under
    /// 1.33 — a Korean panel's instruction at 1.26 and 1.33, an English page's "Resources for
    /// Developers," / "by Developers" at 1.32 — while the pairs that must come apart start at 1.40:
    /// three checkbox entries of the settings panel at 1.40 and 1.43, and a documentation site's
    /// breadcrumb over its filter box at 1.55. Nothing falls between 1.33 and 1.40.</para>
    ///
    /// <para>1.38 is where the branch this was ported from put it, on a different corpus and a
    /// reconstructed detection box; landing inside the empty band measured here as well is the
    /// reason to keep the number rather than move it to the midpoint.</para>
    ///
    /// <para>Every script, not Latin only. The port gated this on Latin because it had to rebuild
    /// the detector's box from the normalised one and that reconstruction was only approximate for
    /// CJK. LayoutBounds is the box, so there is nothing to approximate and nothing to gate.</para>
    /// </remarks>
    private const double WrappedFinalLineAdvance = 1.38;

    /// <summary>
    /// Whether the second line is set solid under the first: one leading, one edge, no gap in the
    /// setting that a reader would take as a break.
    /// </summary>
    /// <remarks>
    /// <para>The evidence rule below it only recognises a paragraph's LAST line, because the shape
    /// it looks for is a much shorter line under a longer one. Every line in the middle of a
    /// paragraph is about as wide as the one above it, so a three-line subtitle came back as three
    /// separate requests to the translator — thirteen refusals across the ten comic pages, all
    /// reading "no continuation evidence" over pairs whose width ratio was 0.92 to 1.22.</para>
    ///
    /// <para>What replaces the width reading is the setting itself. Two conditions, both strict:
    /// the leading is no looser than text set solid, and the two lines share an edge to within a
    /// third of a line height. The alignment figure here is far tighter than the ordinary gate
    /// (0.35 against 1.2), and it is what keeps a stat panel's label off its body — those pairs sit
    /// at 2.90, 5.07 and 7.42 — so it is a threshold to leave alone rather than one to tune.</para>
    ///
    /// <para>Size is not re-tested here. The caller has already held the pair to the ordinary size
    /// ratio, and measuring it a second time on the earlier branch pushed back out the very
    /// sentences this exists to join.</para>
    ///
    /// <para>The length test is the caller's own: a line too short to have run out of room is not
    /// wrapping, whatever its spacing. A speech bubble opening on one or two words is exactly that
    /// shape and exactly not that case, which is what <see cref="GroupingProfile"/> waives — for
    /// the mode where the user has said the capture is speech, and only there.</para>
    ///
    /// <para>The full wrap aspect, not the half the earlier branch used here. That was measured on
    /// this corpus: halving it joins twenty-four more pairs, of which eight are comic speech and
    /// thirteen are not — and ten of those thirteen are wrong. They are all one shape, a game
    /// panel's control labels stacked in a column ("Move Camera" over "Open Map (Hold)"), which is
    /// short, aligned, evenly spaced and not a sentence. The argument for halving it was that a
    /// script writing eight characters where English writes twenty would be refused unfairly; the
    /// Japanese and Korean subtitle sets say otherwise — they do not move at all when it is halved.
    /// The eight comic pairs it would buy are bought instead by the mode that waives this test
    /// altogether, without charging the panels for them.</para>
    /// </remarks>
    private static bool IsSetSolidUnder(
        OcrTextBlock previous, OcrTextBlock current, GroupingProfile profile)
    {
        var avgHeight = (previous.LayoutBounds.Height + current.LayoutBounds.Height) / 2.0;

        return LineAdvanceRatio(previous, current) <= SolidBarFor(previous, profile) &&
               AlignmentDelta(previous, current) <= Math.Max(avgHeight * SetSolidMaxAlignment, 6) &&
               (IsLongEnoughToHaveWrapped(previous) ||
                (profile.WaiveLengthTestWhenSetSolid && IsCentredAgainst(previous, current, avgHeight)));
    }

    // Japanese dialogue can contain several complete sentences within one paragraph. Require
    // two substantial prose rows, punctuation and compact leading before crossing a sentence
    // boundary. Kana keeps this measured exception away from Latin UI and Chinese headings.
    // A missing OCR prefix can shift one row by more than the ordinary alignment allowance;
    // substantial overlapping rows tolerate that without admitting short speaker labels.
    private static bool IsJapaneseProseContinuation(
        OcrTextBlock previous, OcrTextBlock current, GroupingProfile profile)
    {
        if (profile.SolidLineAdvanceWhenWrapped <= SolidLineAdvance ||
            !previous.Text.Any(LayoutScriptDetection.IsKana) ||
            !current.Text.Any(LayoutScriptDetection.IsKana) ||
            !IsLongEnoughToHaveWrapped(previous) ||
            current.LayoutBounds.Width < current.LayoutBounds.Height * 6 ||
            !(EndsWithSentenceTerminator(previous.Text.Trim()) ||
              EndsWithContinuationPunctuation(previous.Text.Trim())))
            return false;

        var a = previous.LayoutBounds;
        var b = current.LayoutBounds;
        var height = (a.Height + b.Height) / 2;
        var overlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        return TextSizeRatio(previous, current) >= profile.TightlySetMinTextSizeRatio &&
            LineAdvanceRatio(previous, current) <= profile.SolidLineAdvanceWhenWrapped &&
            AlignmentDelta(previous, current) <= height * 2 &&
            overlap >= Math.Min(a.Width, b.Width) * 0.5;
    }

    // Long, left-aligned body lines can have more leading than compact UI text.
    private static bool IsLooselySetProse(
        OcrTextBlock previous, OcrTextBlock current, GroupingProfile profile,
        IReadOnlyList<OcrTextBlock> lines)
    {
        if (!(profile.SolidLineAdvanceWhenWrapped > SolidLineAdvance &&
        previous.LayoutScript == OcrLayoutScript.Latin && current.LayoutScript == OcrLayoutScript.Latin &&
        previous.LayoutBounds.Width >= previous.LayoutBounds.Height * 20 &&
        current.LayoutBounds.Width >= current.LayoutBounds.Height * 8 &&
        Math.Abs(previous.LayoutBounds.Left - current.LayoutBounds.Left) <=
            Math.Max(3, Math.Min(previous.LayoutBounds.Height, current.LayoutBounds.Height) * 0.2) &&
        TextSizeRatio(previous, current) >= MinTextSizeRatio &&
        LineAdvanceRatio(previous, current) <= 1.65))
            return false;

        // Learn the body leading from comparable neighbours in this column. A larger gap
        // between independent news headlines must not borrow a paragraph's relaxed limit.
        var column = lines.Where(b => b.LayoutScript == OcrLayoutScript.Latin &&
            TextSizeRatio(previous, b) >= MinTextSizeRatio &&
            Math.Abs(b.LayoutBounds.Left - previous.LayoutBounds.Left) <= previous.LayoutBounds.Height * 0.2)
            .OrderBy(b => b.LayoutBounds.Top).ToArray();
        var advances = new List<double>();
        for (var i = 1; i < column.Length; i++)
        {
            var upper = column[i - 1];
            var lower = column[i];
            if (upper.LayoutBounds.Width < upper.LayoutBounds.Height * 8 ||
                LineAdvanceRatio(upper, lower) < 0.8 || LineAdvanceRatio(upper, lower) > 1.8)
                continue;
            advances.Add(lower.LayoutBounds.Top - upper.LayoutBounds.Top);
        }
        if (advances.Count < 3) return false;
        advances.Sort();
        var typical = advances[(advances.Count - 1) / 4];
        return current.LayoutBounds.Top - previous.LayoutBounds.Top <= typical * 1.2;
    }

    // Use the established paragraph, not just an ambiguous isolated pair.
    private static bool IsEstablishedParagraphFinalLine(
        IReadOnlyList<OcrTextBlock> group, OcrTextBlock current, GroupingProfile profile)
    {
        if (profile.SolidLineAdvanceWhenWrapped <= SolidLineAdvance || group.Count < 3 ||
            current.LayoutScript != OcrLayoutScript.Latin)
            return false;
        var text = current.Text.Trim();
        if (text.Length == 0 || !char.IsLower(text[0]) || !EndsWithSentenceTerminator(text))
            return false;
        var previous = group[^1];
        var before = group[^2];
        if (previous.LayoutScript != OcrLayoutScript.Latin || before.LayoutScript != OcrLayoutScript.Latin ||
            EndsWithSentenceTerminator(previous.Text.Trim()) ||
            previous.LayoutBounds.Width < group.Max(b => b.LayoutBounds.Width) * 0.85 ||
            current.LayoutBounds.Width > previous.LayoutBounds.Width * 0.5 ||
            Math.Abs(previous.LayoutBounds.Left - current.LayoutBounds.Left) > previous.LayoutBounds.Height * 0.2)
            return false;
        var heightRatio = Math.Min(previous.LayoutBounds.Height, current.LayoutBounds.Height) /
            Math.Max(previous.LayoutBounds.Height, current.LayoutBounds.Height);
        var lastAdvance = previous.LayoutBounds.Top - before.LayoutBounds.Top;
        var advance = current.LayoutBounds.Top - previous.LayoutBounds.Top;
        return heightRatio >= 0.85 && TextSizeRatio(before, previous) >= MinTextSizeRatio &&
            lastAdvance > 0 && advance >= lastAdvance * 0.85 && advance <= lastAdvance * 1.15;
    }

    /// <summary>
    /// Whether two lines are set centred on one another, rather than stacked flush against one
    /// edge.
    /// </summary>
    /// <remarks>
    /// <para>What the waived length test was really keeping out. A line too short to have run out of
    /// room is not wrapping — true of speech, and true of a label sitting over the thing it labels,
    /// and the profile that waives it for the first was letting the second through as well. Measured
    /// on the seven non-comic sets, that waiver joined 75 pairs, and the ones that were wrong are all
    /// one shape: control labels and HUD readouts stacked flush left, "Move Camera" over
    /// "Open Map (Hold)", "RTX" over "VSR". A stack like that shares an edge, which is exactly what
    /// the set-solid geometry is looking for, so nothing earlier in the chain can tell it from
    /// text.</para>
    ///
    /// <para>Centring can. Setting text in a balloon centres it, so its lines run past one another
    /// at both ends; stacking labels aligns one edge, so they cannot. That is the whole test, and it
    /// is asked of whichever line is the narrower — which is the correction the corpus forced. A
    /// balloon's opening line is the short one ("WHY ARE" over "YOU PICKING ON"), but its middle
    /// lines are not: "YOU PICKING ON" over "AN INNOCENT" is the wide line first. Asking only
    /// whether the first line is inset — the shape the opening line has — refused nine of the comic
    /// corpus's twenty-one joins, all of them balloon interiors, because it was testing the
    /// direction rather than the centring.</para>
    ///
    /// <para>Both detector sizes read the comic pairs identically: 0.00 and below on two of them,
    /// then nothing until 0.28, then 0.55 and up. The threshold sits in that gap. It is the figure
    /// the earlier branch used, re-measured here on LayoutBounds rather than carried over — the
    /// geometry changed under it, and a number nobody re-measured is a number nobody knows.</para>
    ///
    /// <para>The pixel floor is for text small enough that a fifth of a line height is a pixel or
    /// two, where the detector's own jitter would otherwise decide this.</para>
    ///
    /// <para>THE INSET READING GOES BLIND WHERE SPEECH IS MOST OBVIOUSLY SPEECH, which is the
    /// second half of this. A letterer breaks a balloon's lines to about the same length, so the
    /// better balanced the setting is the less either line runs past the other — and the inset,
    /// which is what that running-past measures, falls to nothing. Measured on the two comic sets:
    /// 「NICE TO SEE」over「YOU AGAIN.」is inset 9px where 10.4 is asked, 「OR IS IT WI」over
    /// 「SEOL-AH?」8px where 10.5 is asked, and 「THAT GUY'S FAULT」over「YOU ENDED UP IN」— 418 and
    /// 414 pixels wide, one leading apart, no edge out by more than 0.07 of a line — reads 0. Seven
    /// pairs sat between 0 and 9px against a 10 to 15px requirement, every one of them a balloon
    /// interior.</para>
    ///
    /// <para>So the second question is asked directly: do the two lines share a centre. That is
    /// what the paragraph above says the test is about, and it stays true as the widths converge —
    /// the seven read 0.00 to 0.11 line heights apart on their centres. The inset reading is kept
    /// rather than replaced, because it is the one that still answers for a pair set well off
    /// centre with both ends inset, and replacing it lost a Japanese page's wrapped heading.</para>
    ///
    /// <para>Measured with both halves in, over 196 captures: the two comic sets go from 34 of
    /// their 45 hand-marked groups exactly right to 40, and from 23 spurious groups to 10. Across
    /// the other 174 captures five pair verdicts move — two of them right (a two-line subtitle,
    /// 「like an explosive force」/「hurtling into the sky!」, and a wrapped sentence on an English
    /// documentation page), three of them wrong and all the same mild shape: two labels of a list
    /// read as one line. Those three have no mode to escape to — the screenshot flow ships one
    /// profile — so they are a cost the default carries, taken because half a sentence handed to
    /// the translator is the worse failure.</para>
    ///
    /// <para>The live path cannot reach any of this, and that is the containment that matters.
    /// Nothing here runs without <see cref="GroupingProfile.WaiveLengthTestWhenSetSolid"/>, which
    /// only the screenshot flow's profile sets, and a dialogue region does not come through this
    /// file's set-solid path at all — <see cref="Realtime.DialogueTextGrouper"/> has its own rules.
    /// Measured rather than argued: 178 subtitle, chat and panel captures replayed through the
    /// realtime profiles group identically either side of this change.</para>
    /// </remarks>
    private static bool IsCentredAgainst(OcrTextBlock previous, OcrTextBlock current, double avgHeight)
    {
        var inset = Math.Max(avgHeight * SetSolidMinCentringInset, 4);
        if (InsetWithin(previous, current) >= inset || InsetWithin(current, previous) >= inset)
            return true;

        return Math.Abs(CenterX(previous) - CenterX(current)) <=
                   Math.Max(avgHeight * SetSolidMaxCentringOffset, 4) &&
               WouldHaveWrappedTogether(previous, current);
    }

    /// <summary>How far <paramref name="inner"/> sits inside <paramref name="outer"/> at its nearer end.</summary>
    private static double InsetWithin(OcrTextBlock inner, OcrTextBlock outer) =>
        Math.Min(
            inner.LayoutBounds.Left - outer.LayoutBounds.Left,
            outer.LayoutBounds.Right - inner.LayoutBounds.Right);

    /// <summary>
    /// The waived length test, asked of the pair rather than of the line: together, are these two
    /// long enough that one line of them would have run out of room?
    /// </summary>
    /// <remarks>
    /// <para>The centring test above cannot stand alone, because a pair of equally wide lines is
    /// centred on one another whatever they say — and the shape the length test exists to refuse is
    /// exactly two short tokens in a column. Measured over the corpus, asking the centres alone
    /// moved 47 pair verdicts across the 174 non-comic captures instead of five, the great majority
    /// of them wrong — among them nine captures of 「RTX」over「VSR」, the pair the paragraph above
    /// names as the thing this must keep out.</para>
    ///
    /// <para>What is left to ask them is length, and it is not a new threshold: it is
    /// <see cref="WrappedLineMinAspect"/>, asked of the two lines added together. A balloon's line
    /// is short because the balloon is narrow, so no single line of one reaches that figure; the
    /// text as a whole does. Measured, the two populations are on opposite sides of it without the
    /// number being chosen to put them there — every pair the comic sets and the subtitle and prose
    /// wins need runs 8.30 to 15.12, while 「RTX」/「VSR」reads 3.25, 「TD01」/「TD02」3.72,
    /// 「Move」/「Dash」4.03, 「出版社」/「場栽誌」4.73, and a calculator's 「Targets &gt;」/「Average &gt;」
    /// 7.13.</para>
    ///
    /// <para>The populations do touch at the top: the three wrong joins this takes sit at 8.28,
    /// 8.54 and 10.81, and the lowest right one at 8.30. They are not separable on length — a
    /// two-entry list of long entries is a pair of long lines — so this is a cost trade in the
    /// sense <see cref="SolidLineAdvance"/> sets out, and what makes it a cheap one is that the
    /// three are all two labels read as one line, against half a sentence handed to the translator
    /// on the other side.</para>
    /// </remarks>
    private static bool WouldHaveWrappedTogether(OcrTextBlock previous, OcrTextBlock current) =>
        Aspect(previous) + Aspect(current) >= WrappedLineMinAspect;

    /// <summary>
    /// The most leading two lines can have and still read as set solid, one under the other.
    /// </summary>
    /// <remarks>
    /// <para>Measured over the whole image corpus at both detector sizes, on the pairs that reach
    /// this test at all — through the size gate and the shared-edge gate, and long enough to have
    /// wrapped. The two populations are NOT separated by a gap, and that is the first thing to know
    /// about this number. Lines that must join run from 0.65 to 1.25; lines that must not run from
    /// 0.86 to 1.58 — a settings panel's checkboxes at 1.47 to 1.58, but also a news page's
    /// consecutive headlines at 1.12 and 1.25, an event listing's two dates at 1.17, and a game's
    /// character rows at 0.86 to 1.03. They overlap for the whole of that range. No value of this
    /// constant separates them, so it is not chosen to.</para>
    ///
    /// <para>What it is chosen on is the cost of being wrong in each direction, which this codebase
    /// has already settled: joining two labels puts two extra words in one bubble, while splitting
    /// one sentence hands the translator half of it. So the number sits high in the overlap rather
    /// than below it — 36 of the corpus's 37 correct joins, against 6 wrong ones. Tightening to
    /// 1.05, just above the comic pages' loosest real wrap, removes 3 of those 6 and costs 7 of the
    /// correct ones, among them three Japanese Wikipedia paragraphs and a game's tutorial text. The
    /// three it cannot remove at any setting are a game's character rows, which sit at 0.86 to 1.03,
    /// below every candidate.</para>
    ///
    /// <para>It is one number for every layout, and an adaptive version was tried: let a group that
    /// has already been set at some leading be judged against its own. It was measured over the
    /// whole corpus and changed exactly one verdict, which was a wrong one — two unrelated news
    /// headlines strung together — so it was taken out again. The reason it bought nothing is worth
    /// keeping: a group's own leading only exists once it has two lines, so the earliest it can
    /// apply is a third line, while nearly every pair that needs the limit relaxed is a second one.
    /// A list of stacked rows cannot exploit such a rule either, for the same reason — its entries
    /// never join, so its groups never reach a second line to establish anything with.</para>
    ///
    /// <para>The ceiling is not from the corpus. A fixture written before any of this — a Chinese
    /// heading over its byline, one line apart, similar widths — is set at 1.25, and it is there to
    /// say that pair must not join. Three corpus cases agree with it: a news page's consecutive
    /// headlines at 1.12 and 1.25, and an event listing's two dates at 1.17. So the limit stays
    /// under that fixture rather than over it, which costs one Japanese Wikipedia paragraph at
    /// 1.24 and keeps a guard that was written deliberately.</para>
    ///
    /// <para>Both detector sizes agree on the figures that matter here: the comic pages read
    /// identically at 2048 and 1600 (they are small enough not to be resized either way), and the
    /// settings panel's list sits at 1.47 and above at both. The one pair that moves is the event
    /// listing, 1.17 native and 0.95 downscaled, which is inside the joining population at the
    /// downscaled end — one more reason no gap exists to aim at.</para>
    /// </remarks>
    internal const double SolidLineAdvance = 1.20;

    /// <summary>
    /// Which set-solid leading limit a pair is judged on: the mode's relaxed one when the line
    /// above was long enough to have run out of room, and <see cref="SolidLineAdvance"/> otherwise.
    /// </summary>
    /// <remarks>
    /// <para>Only a line long enough to have wrapped earns the relaxed figure, and that condition
    /// is what bounds the cost of relaxing at all. Measured over 118 captures, moving the constant
    /// itself instead joined six pairs of interface labels — "Continue" over "Game Options",
    /// 「オプション」over 「Cygames ID連携」, a stat row over "Wish List", two of a Japanese portal's
    /// shortcuts — and gating it on the length test refused all six while keeping every one of the
    /// paragraph joins.</para>
    ///
    /// <para>Those six are worth reading carefully, because the reason they reach this test at all
    /// is not obvious: they are centred, so the general mode's waiver
    /// (<see cref="GroupingProfile.WaiveLengthTestWhenSetSolid"/>) already lets a short line be set
    /// solid, and the leading is then the only thing left between them. A stack of flush-left
    /// labels never gets this far — the length test refuses it whatever this returns — so a fixture
    /// written flush left cannot tell the two limits apart.</para>
    ///
    /// <para>The relaxed figure lives on <see cref="GroupingProfile"/> rather than here because it
    /// is not free: see <see cref="GroupingProfile.SolidLineAdvanceWhenWrapped"/> for what it buys,
    /// what it costs, and why only one mode takes it.</para>
    /// </remarks>
    private static double SolidBarFor(OcrTextBlock previous, GroupingProfile profile) =>
        IsLongEnoughToHaveWrapped(previous) ? profile.SolidLineAdvanceWhenWrapped : SolidLineAdvance;

    /// <summary>
    /// How far apart two set-solid lines' nearest edges may be, in line heights.
    /// </summary>
    /// <remarks>
    /// Much tighter than the ordinary alignment gate's 1.2, and deliberately so: this is the one
    /// test standing between a stat panel's label and the body under it once the width reading is
    /// no longer being consulted. Measured on the comic corpus, the pairs that must stay apart sit
    /// at 2.90, 5.07 and 7.42 line heights, and the pairs that must join sit at 0.01 to 0.10. The
    /// band between those is enormous; this number is in it, not on either edge.
    /// </remarks>
    private const double SetSolidMaxAlignment = 0.35;

    /// <summary>
    /// How far the narrower line must sit inside the wider one, in line heights, before the two
    /// count as centred rather than stacked.
    /// </summary>
    /// <remarks>
    /// <para>Measured over the eight image sets at both detector sizes, on the pairs the waiver
    /// admits and nothing else. The comic pairs read the same at 2048 and 1600 — they are small
    /// enough not to be resized — and they fall in two groups with a gap between: two at 0.00 and
    /// below, then nothing at all until 0.28, then 0.55, 0.66, 0.78 and up to 3.50. This sits in
    /// the gap with room on both sides of it.</para>
    ///
    /// <para>The stacked labels have no such gap; they are a continuum from -0.34 up, with more
    /// than half of them within a hundredth of zero. That is the point — flush left is flush left,
    /// and what separates them from balloons is not where the threshold goes but that they are on
    /// the wrong side of any threshold above zero. At this figure the seven non-comic sets keep 17
    /// of 75 such joins at 2048 and 13 of 93 at 1600, while the comic set keeps 19 of 21.</para>
    /// </remarks>
    private const double SetSolidMinCentringInset = 0.20;

    /// <summary>
    /// How far two lines' centres may sit apart, in line heights, and still be set centred on one
    /// another.
    /// </summary>
    /// <remarks>
    /// The same figure as <see cref="SetSolidMinCentringInset"/>, and for the same reason rather
    /// than for tidiness: both are asking how far this pair is from being laid out symmetrically,
    /// one by the width it has to spare and one by where the middle of it lands. The balloon pairs
    /// this admits read 0.00 to 0.11, and the stacked labels it must keep out are a continuum
    /// upwards from there — so, as with the inset, what separates the two populations is which side
    /// of any figure above zero they fall on, not where the figure goes. The pixel floor is the
    /// same detector jitter allowance.
    /// </remarks>
    private const double SetSolidMaxCentringOffset = 0.20;

    private static bool IsLongEnoughToHaveWrapped(OcrTextBlock line) =>
        Aspect(line) >= WrappedLineMinAspect;

    /// <summary>A line's length in its own line heights, which is the unit the length test uses.</summary>
    private static double Aspect(OcrTextBlock line) =>
        line.LayoutBounds.Height > 0 ? line.LayoutBounds.Width / line.LayoutBounds.Height : 0;

    private static bool HasUnclosedDelimiter(string text) =>
        Count(text, '「') > Count(text, '」') ||
        Count(text, '『') > Count(text, '』') ||
        Count(text, '（') > Count(text, '）') ||
        Count(text, '(') > Count(text, ')') ||
        Count(text, '【') > Count(text, '】') ||
        Count(text, '《') > Count(text, '》') ||
        Count(text, '〈') > Count(text, '〉') ||
        Count(text, '"') % 2 == 1;

    private static int Count(string text, char value) => text.Count(c => c == value);

    private static bool EndsWithContinuationPunctuation(string text) =>
        text[^1] is '、' or '，' or ',' or '：' or ':' or '；' or ';' or '「' or '『' or '（' or '(' or '【' or '《' or '〈';

    private static bool StartsWithContinuationPunctuation(string text) =>
        text[0] is '」' or '』' or '）' or ')' or '】' or '》' or '〉' or '、' or '，' or ',' or '。' or '！' or '？' or '!' or '?';

    private static bool EndsWithSentenceTerminator(string text) =>
        text[^1] is '。' or '！' or '？' or '!' or '?' or '.';

    /// <summary>
    /// Whether a line opens with the mark that makes it an item in a list.
    /// </summary>
    /// <remarks>
    /// News portals set their headline lists at the same leading as running prose — 1.22 and 1.23
    /// line heights on the pages measured, against a paragraph's 1.30 to 1.40 — so no spacing rule
    /// can tell one from the other. The mark can: nothing writes a bullet in the middle of a
    /// sentence it is continuing. This refuses whatever the geometry says, which is the point of
    /// it — the geometry has already been asked and had nothing to offer.
    /// </remarks>
    private static bool StartsWithListBullet(string text) =>
        text[0] is '·' or '・' or '•' or '‧' or '●' or '○' or '▪' or '◆' or '※';

    /// <summary>
    /// Whether a line ends on a colon, which reads two ways and cannot be told apart by the
    /// punctuation alone.
    /// </summary>
    /// <remarks>
    /// <para>A colon at the end of a line is either a clause that has not finished — "as follows:"
    /// — or a form label standing over its value. Both are common, and the continuation rule reads
    /// every one of them as the first. A settings dialog gave "Column type:" over "Standard" a line
    /// and three quarters below, which is a label and its value on two rows of a form, and it was
    /// joined into one string for the translator.</para>
    ///
    /// <para>So the colon stops being evidence on its own and becomes evidence only when the two
    /// lines are set as close as a wrapped sentence is. The limit is the same one a paragraph's
    /// last line is held to today; the step that measures a leading of its own will move this onto
    /// it.</para>
    /// </remarks>
    private static bool EndsWithLabelColon(string text) => text[^1] is ':' or '：';

    /// <summary>
    /// Whether a line is all figures: a row of a table or a card, not the rest of a sentence.
    /// </summary>
    /// <remarks>
    /// <para>A game's character rows are the measured case — a name and level over a hit-point
    /// count, flush right, one text size, a line apart — and the same shape turns up as a
    /// spreadsheet's header over its first data row and an invoice's field names over their values.
    /// Six of the corpus's wrong joins are this, and none of them can be reached by any leading
    /// limit: they sit at 0.86 to 1.03 line heights, well inside where real wrapped text lives.
    /// Nothing about the geometry says they are rows; only the content does.</para>
    ///
    /// <para>Asks for no letters at all rather than for a leading digit, because a wrapped sentence
    /// often continues on a figure ("1.81 stabilizes the trait…") and would be refused by the
    /// looser test. A digit somewhere is required too, so that a line of nothing but punctuation —
    /// a stray closing bracket the detector split off — is left to the rules written for it.</para>
    ///
    /// <para>Asked BEFORE the colon rule below, and the order matters: a label ending in a colon
    /// over a date ("Updated:" over "07/24/2026 18:13") satisfies both, and they disagree. Whoever
    /// reorders these is changing that verdict, so the order is a decision rather than an
    /// accident — the two are kept apart because the label and the date are better translated
    /// apart, and the standard mode's placement lays each back on its own source line.</para>
    ///
    /// <para>What this refuses is "this line is figures", not "these two lines belong to different
    /// cards". The second is the real fault — a character card's name row and the row under it are
    /// two records, and would be even if the second held words — and this rule only happens to
    /// cover most of its symptoms. A card whose second row carries a letter escapes it, and one
    /// did: blocking the figures on one card sent the name row off to join the card below. Do not
    /// read a green corpus here as the card problem being solved.</para>
    /// </remarks>
    private static bool IsNumericRow(string text)
    {
        var hasDigit = false;

        foreach (var character in text)
        {
            if (char.IsLetter(character))
                return false;
            hasDigit |= char.IsDigit(character);
        }

        return hasDigit;
    }

    // Aggregation only, shared with dialogue. No joining decision is made by this method.
    internal static OcrTextBlock BuildGroup(List<OcrTextBlock> blocks)
    {
        if (blocks.Count == 1)
            return blocks[0];

        var x = blocks.Min(block => block.Bounds.X);
        var y = blocks.Min(block => block.Bounds.Y);
        var right = blocks.Max(block => block.Bounds.Right);
        var bottom = blocks.Max(block => block.Bounds.Bottom);
        var text = string.Join(" ", blocks.Select(block => block.Text.Trim()).Where(text => text.Length > 0));

        // Aggregate the per-line glyph heights (Latin only; null for CJK) so the group's overlay
        // font is sized from the real glyph height, while Bounds remains the full coverage area.
        var glyphHeights = blocks
            .Where(block => block.RenderGlyphHeight.HasValue)
            .Select(block => block.RenderGlyphHeight!.Value)
            .OrderBy(height => height)
            .ToList();
        double? groupGlyphHeight = glyphHeights.Count > 0 ? glyphHeights[glyphHeights.Count / 2] : null;

        var layoutScript = LayoutScriptDetection.For(text);

        return new OcrTextBlock(
            text,
            new Rect(x, y, right - x, bottom - y),
            blocks.Select(block => block.Bounds).ToList(),
            groupGlyphHeight,
            CombineConfidence(blocks),
            // Re-read rather than folded together: the field's contract is "the script of this
            // block's own text", and a group whose lines are Latin and CJK is exactly the Mixed
            // that the joined text reports.
            layoutScript,
            blocks.Select(block => block.LayoutBounds).Aggregate(Rect.Union),
            CombineLayoutGlyphHeight(layoutScript, blocks));
    }

    /// <summary>
    /// One confidence for several fragments, weighted by how much text each contributes.
    /// </summary>
    /// <remarks>
    /// A plain average lets a two-character fragment beside a confidently-read line pull the whole
    /// group down as far as the line itself pulls it up, which would make the group's score depend
    /// on how the detector happened to split the line rather than on how well it was read.
    /// </remarks>
    private static double? CombineConfidence(IReadOnlyList<OcrTextBlock> blocks)
    {
        double weighted = 0;
        double weight = 0;

        foreach (var block in blocks)
        {
            if (block.Confidence is not { } confidence) continue;

            var characters = Math.Max(1, block.Text.Trim().Length);
            weighted += confidence * characters;
            weight += characters;
        }

        return weight > 0 ? weighted / weight : null;
    }

    /// <summary>
    /// One layout glyph height for several lines: the median of theirs, and nothing at all once
    /// the joined text is no longer of a single script.
    /// </summary>
    private static double? CombineLayoutGlyphHeight(OcrLayoutScript script, IReadOnlyList<OcrTextBlock> blocks)
    {
        if (script is not (OcrLayoutScript.Latin or OcrLayoutScript.Cjk))
            return null;

        var heights = blocks
            .Where(block => block.LayoutGlyphHeight is > 0)
            .Select(block => block.LayoutGlyphHeight!.Value)
            .OrderBy(height => height)
            .ToList();

        return heights.Count > 0 ? heights[heights.Count / 2] : null;
    }

    private static double? CombineGlyphHeight(double? a, double? b) =>
        (a, b) switch
        {
            (double ha, double hb) => (ha + hb) / 2.0,
            (double ha, null) => ha,
            (null, double hb) => hb,
            _ => null,
        };
}
