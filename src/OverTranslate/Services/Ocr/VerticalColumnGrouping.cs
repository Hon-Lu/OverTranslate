using System.Drawing;
using Rect = System.Windows.Rect;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// Assembles a page of vertical writing out of the columns the detector found on it.
/// </summary>
/// <remarks>
/// <para>Everything from the detector's columns to the groups the overlay draws. The stages either
/// side of it are in this namespace too — <see cref="VerticalColumnDetection"/> and
/// <see cref="VerticalColumnEnds"/> run inside the engine, <see cref="VerticalRepeatedColumns"/>,
/// <see cref="VerticalRubyColumns"/> and <see cref="VerticalOcrGeometry"/> run at the head of
/// <see cref="Group"/>, and <see cref="VerticalSecondLook"/> reconciles two readings of one
/// page afterwards.</para>
///
/// <para>Separate from <see cref="OcrService"/>, whose job is talking to the engine: both of the
/// vertical entry points there are two lines — recognise, then group — and this is the second
/// line.</para>
/// </remarks>
internal static class VerticalColumnGrouping
{
    /// <summary>
    /// Columns are assembled right to left; the horizontal writing on the same page is kept beside
    /// them rather than thrown away.
    /// </summary>
    /// <remarks>
    /// <para>MEASURED. Keeping the rows out of the column merge is necessary — a box thrown across
    /// two columns joins them into one block, which is what
    /// <see cref="IsColumnCandidate"/> is for. Dropping them from the OUTPUT as well was a
    /// separate thing the same filter did, and it cost real text: over the 15 comic pages in
    /// <c>.ai/test-images/vertical-image-ja2</c> it discarded 11 blocks across 6 of them — the name
    /// plates (<c>付与術士</c>, <c>オルン・ドゥーラ</c>), three lines of a narration box, a scene
    /// label, the chapter-end line. Every one had been read correctly, at 0.87 to 1.00 confidence.
    /// Not a column and not wanted are different statements, and only the first was measured.</para>
    ///
    /// <para>They are marked rather than merged into the column groups. Reading order between a
    /// caption and the columns around it is a question nothing here can answer, and guessing at it
    /// would put a name plate in the middle of somebody's dialogue.</para>
    /// </remarks>
    internal static List<OcrTextBlock> Group(
        List<OcrTextBlock> blocks, double frameWidth, bool realtime = false, Bitmap? bitmap = null)
    {
        // The collapse test only; the short-and-unsure test waits for the groups — see the remarks
        // on WithoutUnconvincingGroups for why asking it of a column throws balloons away.
        //
        // Asked of COLUMNS, because what it recognises is a box thrown across the columns and the
        // width is only that box's giveaway for something written down the page. A row spanning the
        // frame is a row spanning the frame: MEASURED on
        // .ai/test-images/vertical-manga-web/mokuro-000a.jpg, where the cover title うちの猫ず日記 is
        // read whole at 1.00 in a box 840 wide on an 827 wide page, and was thrown away every time
        // as a collapse holding "a character or two of nonsense" — seven characters being under the
        // ten that bar was measured at, on English subtitles, where ten characters is nothing and in
        // Japanese it is a sentence.
        if (realtime)
            blocks = blocks
                .Where(block => !IsColumnCandidate(block) ||
                    !Services.Realtime.CollapsedDetection.IsCollapsed(
                        block.Bounds.Width, frameWidth, block.Text)).ToList();

        // Before the readings, because a column read twice is two columns to judge rather than one,
        // and the second copy of it sits exactly where a reading would.
        blocks = VerticalRepeatedColumns.Drop(blocks);

        // Before anything joins or groups: a reading merged into a sentence cannot be taken back
        // out of it afterwards, which is what VerticalRubyColumns exists to say. What it is
        // sure about is gone; what it only suspects comes back at the end of this method, having
        // been kept out of every sentence on the page without being thrown away.
        var (writing, readings) = VerticalRubyColumns.Separate(blocks);
        blocks = writing;

        var candidates = new List<OcrTextBlock>();
        var across = new List<OcrTextBlock>();
        foreach (var block in blocks)
            (IsColumnCandidate(block) ? candidates : across).Add(block);

        using var pixels = bitmap is null ? null : OnnxOcrEngine.ConvertToSkBitmap(bitmap);

        var merged = MergeColumns(
            VerticalOcrGeometry.JoinColumnFragments(candidates, pixels));
        merged.AddRange(across.Select(block => block with { RunsAcross = true }));

        // Last, so that a reading sitting on a row is judged against it too — the furigana over a
        // sign reads the same way whether the sign runs down the page or across it. What keeps a
        // reading from being swallowed before it gets here is the size test in
        // IsSameGroup, not the order of these two lines.
        // The readings go first. What is left is then the page's real writing, which is what the row
        // test has to be asked against: a reading IS a column, and a reading sitting on a sign made
        // the row test drop the sign — 迷宮入り口 thrown away because ぐち lay on it.
        var groups = WithoutWordlessGroups(WithoutRowsOverColumns(WithoutRuby(merged)));
        if (realtime) groups = WithoutUnconvincingGroups(groups);

        // The suspected readings join HERE rather than above, and the position is the whole of it.
        // A column merely refused at the grouping seam becomes a group of one, and a group of one
        // that is small and was scored badly is precisely what WithoutUnconvincingGroups exists to
        // delete — so refusing it up there is not "kept apart", it is a slower way of losing it.
        // That is how 「が」 went the first time this seam was closed, and why the two earlier
        // attempts at it were reverted.
        //
        // Two of the three filters still get a say, because both of them mean what they say about a
        // reading. Wordless goes first: most of what lands here is the page number or a mark off the
        // artwork, read as 416 or L or 8. Then the group-level ruby test, which is the measured one
        // — asked only against the finished sentences, so a reading cannot rule another reading out.
        // MEASURED over the 15 comic pages it takes exactly three more: み世, の6 and AJ, the ones
        // that hold a letter and so survived the first, and it takes nothing else on any corpus.
        var aside = WithoutWordlessGroups(
            [.. readings.Select(reading => CombineColumns([reading]))]);
        groups.AddRange(aside.Where(reading =>
            SaysMoreThanOneCharacter(reading) && !groups.Any(body => IsRubyOver(reading, body))));
        return groups;
    }

    /// <summary>
    /// Whether a column set aside has enough in it to be worth showing on its own.
    /// </summary>
    /// <remarks>
    /// A column is set aside rather than dropped because ruby and dialogue cannot be told apart at
    /// that point — but with ONE character there is no dialogue to lose. The reason a column got
    /// there at all is that it is half the size of the kanji beside it and hard against it, which is
    /// ruby's geometry; a real one-character balloon stands on its own with nothing to be a reading
    /// of. MEASURED, every single-character aside on the three corpora is a mis-read reading:
    /// <c>一</c>, <c>L</c>, <c>上</c>, <c>大</c>.
    ///
    /// What this actually fixes is a difference BETWEEN THE TWO FLOWS. The group-level ruby test
    /// takes these on the live path and misses them on the screenshot path, because the two read at
    /// different detector sizes and the boxes land differently against its shared-area bar — so the
    /// same page showed a stray 一 beside the balloon in one flow and not the other.
    /// </remarks>
    private static bool SaysMoreThanOneCharacter(OcrTextBlock group) =>
        group.Text.Count(character => !char.IsWhiteSpace(character)) > 1;

    /// <summary>Drops what a translator would hand straight back.</summary>
    /// <remarks>
    /// <para>A group with no letter in it anywhere — digits, punctuation, dashes — has nothing to
    /// translate into anything. Sending it costs a call, and what comes back is painted over the
    /// page as a bubble, so the reader is shown a translation of nothing sitting on the artwork.</para>
    ///
    /// <para>MEASURED over the 15 comic pages in <c>.ai/test-images/vertical-image-ja2</c>: 16
    /// groups survive grouping without matching anything a reader would call text, and 14 of them
    /// are the PAGE NUMBER — 11, 34, 35, 37, 42, 45, 48, 58, 59, 61, 62, 63, 64, 65. A printed page
    /// carries its number in the margin in the same typeface as nothing else on it, the detector
    /// finds it every time, and it is the one thing on the page that is certainly not dialogue. The
    /// other two are a stray digit off the artwork.</para>
    ///
    /// <para>Deliberately not a test on length or on confidence. The page numbers are read perfectly
    /// — 1.00, every one of them — and at two characters they are longer than plenty of real
    /// balloons (<c>ん？</c>, <c>これが</c>). What is wrong with them is not that the reading is
    /// poor or short; it is that there is no word in it.</para>
    /// </remarks>
    internal static List<OcrTextBlock> WithoutWordlessGroups(List<OcrTextBlock> groups) =>
        [.. groups.Where(group => group.Text.Any(char.IsLetter))];

    /// <summary>
    /// Drops the groups the live path treats as scenery read as text — asked of the SENTENCE, not
    /// of the columns it is built from.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Realtime.ShortReadingDetection.IsUnconvincingShortText"/> throws away a
    /// reading of fewer than ten characters that the recogniser scored under 0.80, and it was
    /// measured on whole subtitle lines over video, where the short ones really were scenery:
    /// <c>605G0</c>, <c>DM</c>, <c>M'</c>. That reasoning does not survive being asked of a COLUMN.
    /// A column of vertical Japanese is a handful of characters by construction — that is what a
    /// column is — so the length half of the test is true of nearly every one of them, and what is
    /// left is "throw away any column the recogniser scored under 0.80".</para>
    ///
    /// <para>MEASURED on a frame the user captured: the balloon とはいえ／そいつらにとって is white
    /// text on a black panel, and the two columns come back at 0.74 and 0.68 — the whole balloon
    /// thrown away, both columns, while 俺が不要な存在なのはわかった beside it survives at 0.99.
    /// Dark panels score lower across the board, so this took whole balloons off the page and took
    /// the same ones every time, which is what the report of "always the same sentences missing"
    /// was.</para>
    ///
    /// <para>Asked of the group instead, the test means what it meant where it was measured: a
    /// group IS the line. The balloon above comes back as twelve characters at 0.71 and is kept,
    /// and the scenery the test exists for — a couple of characters off a wooden floor — is still
    /// a couple of characters after grouping, because there was nothing beside it to group with.</para>
    /// </remarks>
    internal static List<OcrTextBlock> WithoutUnconvincingGroups(List<OcrTextBlock> groups) =>
        OcrService.RejectUnconvincingBlocks(groups);

    /// <summary>
    /// How small a group has to be set, against the one it sits on, to be the ruby over it.
    /// </summary>
    /// <remarks>
    /// Ruby is set at about half the body size, and the corpus agrees with the typography. MEASURED
    /// over the 24 comic pages, on every pair of vertical groups whose boxes overlap: the ones where
    /// the small group is a reading — <c>じょうだん</c> over <c>冗談</c>, <c>まじゅっ</c> over
    /// <c>魔術</c>, <c>ぐち</c> over <c>口</c> — run from 0.22 to 0.58 of the body, and the ones
    /// where it is a piece of the sentence that grouping left behind — <c>間だろ</c>, <c>に礼をして
    /// ほしくて</c>, <c>の</c>, <c>べて</c> — run 0.65, 0.73, 0.86, 1.02. The bar sits in the gap,
    /// and it is the size rather than the position that separates them: both kinds sit right on top
    /// of the text they belong to.
    /// </remarks>
    private const double RubyMaxRelativeSize = 0.60;

    /// <summary>How much of the smaller group has to lie on the other one.</summary>
    /// <remarks>
    /// Half, where the measurement would allow a third: every ruby pair on the corpus but one is
    /// at 0.64 or above and most are wholly inside, so the looser bar buys one more case
    /// (<c>ちゃく</c>, at 0.41) and pays for it by reaching towards the small aside balloons that
    /// sit beside a big one. The one it misses is a reading printed twice; the ones it would start
    /// guessing at are sentences.
    ///
    /// AREA, deliberately, and not "runs alongside and nearly touches". That wider test was tried
    /// and reverted: it reaches the readings that sit just OUTSIDE a group's box as well, which is
    /// more of them — but it leans the whole decision on the pitch estimate, and that estimate is
    /// noisy for a column whose reading came back short or mis-read. It cost
    /// <c>俺とオリヴァーが結成したこのパーティは</c>, a whole line of dialogue dropped as a gloss.
    /// A reading left beside the text draws a small bubble of its own next to the balloon; a
    /// reading that eats a line of dialogue is the fault this pipeline exists to avoid.
    /// </remarks>
    private const double RubySharedArea = 0.50;

    /// <summary>
    /// Drops a group that is the reading printed over another one rather than a line of its own.
    /// </summary>
    /// <remarks>
    /// <para>Two translations drawn on top of each other, which is what this is here to stop, and
    /// what the user sees first: the balloon's own translation and a second bubble over it holding
    /// whatever the reading was read as. The reading is a pronunciation guide for text that is
    /// already in the group underneath, so there is nothing in it to lose — and it cannot be merged
    /// into that group either, because inserting a reading into the middle of a sentence is how
    /// <c>俺たちはあつかまじゅっ支援魔術を扱う</c> happens — a reading that HAS been merged is past
    /// saving here, and remains the open half of this problem.</para>
    ///
    /// <para>WHAT IS DROPPED must be a column, and that is not a detail: the pairs this would
    /// otherwise get wrong are all rows. A name plate sets its title smaller than the name —
    /// <c>剣聖</c> at 0.51 of <c>オリヴァー・カーディフ</c> — and the two boxes overlap, so on size
    /// and position alone the title reads exactly like a reading. Ruby in vertical writing is
    /// itself set in columns, so asking that of the candidate alone tells the two apart, and leaves
    /// the body it sits on free to be either: <c>ぐち</c> over the horizontal sign
    /// <c>迷宮入り口</c> is still a reading printed over the word it belongs to.</para>
    /// </remarks>
    internal static List<OcrTextBlock> WithoutRuby(List<OcrTextBlock> groups) =>
        groups.Count < 2
            ? groups
            : [.. groups.Where(group => !groups.Any(other =>
                !ReferenceEquals(other, group) && IsRubyOver(group, other)))];

    private static bool IsRubyOver(OcrTextBlock candidate, OcrTextBlock body)
    {
        if (candidate.RunsAcross ||
            candidate.RenderGlyphHeight is not { } reading ||
            body.RenderGlyphHeight is not { } text ||
            reading >= text * RubyMaxRelativeSize)
            return false;

        var shared = Rect.Intersect(candidate.Bounds, body.Bounds);
        if (shared.IsEmpty)
            return false;

        // Against the candidate's own area: the question is how much of the reading lies on the
        // text, not how much of a long sentence happens to be covered by a two-glyph gloss.
        var area = candidate.Bounds.Width * candidate.Bounds.Height;
        return area > 0 && shared.Width * shared.Height / area >= RubySharedArea;
    }

    /// <summary>
    /// How much of a row has to lie on a group of columns before the row is taken to be a misread
    /// of those columns rather than writing of its own.
    /// </summary>
    /// <remarks>
    /// Low, because there is nothing above it. MEASURED over both vertical corpora read at five
    /// capture scales — about ninety page readings — a row that is really a row NEVER touches a
    /// column at all: the name plates, the narration boxes, the scene labels and the signs all
    /// stand in their own space, and every single overlap found was this fault.
    /// </remarks>
    private const double RowLyingOnColumns = 0.15;

    /// <summary>
    /// Drops a row drawn across a group of columns, which is the head of those columns misread.
    /// </summary>
    /// <remarks>
    /// <para>The tops of two neighbouring columns sit side by side, and with a reading set between
    /// them they make a short wide patch of ink that the detector frames as one box running across.
    /// <see cref="IsColumnCandidate"/> then measures that box, finds it wider than it is
    /// tall, and correctly reports that it is not a column — so the heads of the balloon are set as
    /// a row, in the order a row is read, over the balloon they came from: 俺 and 剣 come back as
    /// <c>剣俺</c> laid across 俺の本職は剣士なんだから, which itself has lost them.</para>
    ///
    /// <para>Whether the capture is at exactly the right scale decides it. Over the 15 comic
    /// spreads this happens on none at native size, one at 0.85, none at 0.90 or 0.95 and two at
    /// 1.05 — so it cannot be tuned out of the detector, and a reader taking a screenshot has no
    /// way to know which scale they are on.</para>
    ///
    /// <para>WHAT IS LOST is the two characters, which were lost from the balloon anyway — the
    /// columns' own reading is missing them either way, and this changes nothing about that. What
    /// it removes is the second, worse failure on top of it: a bubble of Chinese laid across the
    /// middle of a balloon that already has its own. A row over a column is always one of these two
    /// and never a third thing, so dropping it cannot cost writing that would otherwise be read.</para>
    /// </remarks>
    internal static List<OcrTextBlock> WithoutRowsOverColumns(List<OcrTextBlock> groups) =>
        !groups.Any(group => group.RunsAcross) || !groups.Any(group => !group.RunsAcross)
            ? groups
            : [.. groups.Where(group => !group.RunsAcross || !groups.Any(column =>
                !column.RunsAcross && RunsDownThePage(column) &&
                LiesOn(group.Bounds, column.Bounds) >= RowLyingOnColumns))];

    /// <summary>
    /// Whether a group is writing that actually runs down the page, rather than something short
    /// that merely failed to be a row.
    /// </summary>
    /// <remarks>
    /// Asked of the group doing the overruling, and it is not a formality. A name plate sets its
    /// title above the name and the title is two characters — <c>剣聖</c> at 47x34 — which is
    /// inside <see cref="IsColumnCandidate"/>'s bar and so arrives here as a column. Read
    /// at the realtime size it lies on 0.44 of オリヴァー・カーディフ, and without this the plate
    /// loses the name. A row may only be overruled by a balloon, and a balloon is taller than it
    /// is wide.
    /// </remarks>
    private static bool RunsDownThePage(OcrTextBlock group) =>
        group.Bounds.Height > group.Bounds.Width;

    /// <summary>How much of the smaller of two boxes the two of them share.</summary>
    private static double LiesOn(Rect a, Rect b)
    {
        var shared = Rect.Intersect(a, b);
        if (shared.IsEmpty) return 0;
        var smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller <= 0 ? 0 : shared.Width * shared.Height / smaller;
    }

    internal static List<OcrTextBlock> MergeColumns(List<OcrTextBlock> columns)
    {
        var remaining = columns
            .Where(IsColumnCandidate)
            .OrderByDescending(column => column.LayoutBounds.X)
            .ToList();
        var merged = new List<OcrTextBlock>();

        while (remaining.Count > 0)
        {
            var group = new List<OcrTextBlock> { remaining[0] };
            remaining.RemoveAt(0);

            for (bool grew = true; grew;)
            {
                grew = false;
                for (int i = remaining.Count - 1; i >= 0; i--)
                {
                    if (!group.Any(member => IsSameGroup(member, remaining[i])))
                        continue;

                    group.Add(remaining[i]);
                    remaining.RemoveAt(i);
                    grew = true;
                }
            }

            merged.Add(CombineColumns(group));
        }

        return merged;
    }

    private static bool IsColumnCandidate(OcrTextBlock column)
    {
        // Issue #132's Japanese corpus had six multi-character detections wider than 1.4: all six
        // were horizontal UI or signs, while none of the 188 vertical detections crossed it.
        const double maxWidthToHeightRatio = 1.4;
        int characters = column.Text.Count(character => !char.IsWhiteSpace(character));
        return characters <= 1 ||
               column.LayoutBounds.Width <= column.LayoutBounds.Height * maxWidthToHeightRatio;
    }

    /// <summary>
    /// How much of the shorter column has to run alongside the other one for the two to be the
    /// same piece of writing.
    /// </summary>
    /// <remarks>
    /// <para>MEASURED, and it replaces a test on the TOP EDGES that said the same thing about a
    /// different shape. A balloon is an oval, so its columns do not start at one height: the ones
    /// at the edges are shorter and begin lower, by a fraction of their own length rather than by
    /// some number of characters. On <c>2026-09-20 19 14 56 (3).png</c> read at the size a
    /// two-page spread gives each page, the three columns of
    /// <c>これまで苦楽を共にしてきた仲間に対する態度か？</c> start 11px apart on a 14.6px pitch —
    /// 0.75 of a character, past the old bar of 0.6 — so the sentence came apart into three
    /// groups, and since their padded boxes overlap, three translations were drawn on top of one
    /// another. That is the doubled bubble, and it is a grouping failure rather than a doubled
    /// reading.</para>
    ///
    /// <para>What columns of one balloon do instead of starting together is RUN TOGETHER, and that
    /// survives the ragged top. It also keeps what the top test was there for: a balloon stacked
    /// above another shares no length with it at all, so the two still refuse each other, and the
    /// gutter is still held by the distance test below.</para>
    ///
    /// <para>IT COSTS SOMETHING, and the cost is known rather than guessed at: a reading runs
    /// alongside its column too, so more of them are now joined into the sentence instead of being
    /// left beside it for <see cref="WithoutRuby"/> to drop — about 68 characters of ruby across
    /// the fifteen pages. Two gates were built to refuse the reading at this seam and both were
    /// measured and removed: on <c>GlyphPitch</c>, which divides a column's length by what came
    /// out of it and so returns a whole box height for a one-character column — 「が」 at 53
    /// against its own sentence's 28.8, refused as a reading and lost; and on the box width, where
    /// adjacent columns vary enough that the overlapping pairs went from 23 to 50. Losing a line
    /// of dialogue is worse than carrying a reading into one, so the seam is left open and the
    /// readings are dealt with where the measurement holds.</para>
    /// </remarks>
    /// <remarks>
    /// <para>MEASURED at 0.6, 0.5, 0.35, 0.2 and 0.0 over the 15 comic pages, by labelling every
    /// column with the transcribed balloon it came out of and comparing the groups this produces
    /// against those balloons. 0.5 gets 111 of 138 balloons exactly right, 17 split apart and 8
    /// mixed with a neighbour; 0.35 and 0.2 both get 113 with the SAME 8 mixed, and 0.0 falls back
    /// to 111. So two balloons come back for nothing, and the floor sits at the higher of the two
    /// values that buy them.</para>
    ///
    /// <para>THE OTHER 25 ARE NOT A THRESHOLD PROBLEM, and this is worth writing down because the
    /// obvious next move is to keep turning this dial. Three separate signals were swept against
    /// those balloons and none of them beat 113: the distance bar below at every value from 1.6 to
    /// 2.8, the gutter between the boxes instead of their centres at every value from 0.3 to 2.5,
    /// and whether one run of the page's background connects the two columns — a flood fill from
    /// beside one box to beside the other, which is the balloon itself and separates the pairs
    /// 211/228 against 33/371 on its own. Every one of them trades split balloons for mixed ones
    /// at about one for one. Two balloons side by side in a panel put their columns as close
    /// together as one balloon does, so the pair geometry does not carry the answer, and the
    /// flood fill leaks wherever the writing is not inside a balloon at all — a narration box,
    /// a line lettered straight onto the artwork.</para>
    /// </remarks>
    private const double SideBySideAlongTheColumn = 0.35;

    private static bool IsSameGroup(OcrTextBlock a, OcrTextBlock b)
    {
        // Detector padding is not character size. Compare centres and character pitch so
        // a generous quad cannot bridge a gutter into the next balloon or manga panel.
        double pitch = Math.Max(VerticalOcrGeometry.GlyphPitch(a), VerticalOcrGeometry.GlyphPitch(b));

        double shared = Math.Min(a.LayoutBounds.Bottom, b.LayoutBounds.Bottom) -
                        Math.Max(a.LayoutBounds.Top, b.LayoutBounds.Top);
        double shorter = Math.Min(a.LayoutBounds.Height, b.LayoutBounds.Height);
        if (shorter <= 0 || shared < shorter * SideBySideAlongTheColumn)
            return false;

        double distance = Math.Abs((a.LayoutBounds.Left + a.LayoutBounds.Right) / 2 -
                                   (b.LayoutBounds.Left + b.LayoutBounds.Right) / 2);
        return distance <= pitch * 1.6;
    }

    private static OcrTextBlock CombineColumns(List<OcrTextBlock> group)
    {
        // Reading order is a layout question, so it is decided on the detector's boxes. Everything
        // built below — the coverage rectangle, the cell size, the character cells — is what the
        // overlay draws, and stays on Bounds.
        var ordered = group.OrderByDescending(column => column.LayoutBounds.X).ToList();
        var bounds = ordered.Select(column => column.Bounds).Aggregate(Rect.Union);
        var glyphSize = GroupGlyphSize(ordered);
        var lines = ordered.Count > 1
            ? ordered.Select(column => column.Bounds).ToList()
            : SplitIntoCharacterCells(bounds, glyphSize);

        var scored = ordered.Where(column => column.Confidence.HasValue).ToList();
        double? confidence = scored.Count == 0
            ? null
            : scored.Sum(column => column.Confidence!.Value * Math.Max(1, column.Text.Length)) /
              scored.Sum(column => Math.Max(1, column.Text.Length));

        var text = string.Concat(ordered.Select(column => column.Text));
        var layoutScript = LayoutScriptDetection.For(text);

        return new OcrTextBlock(
            text,
            bounds,
            lines,
            glyphSize,
            confidence,
            // From the text as read. A vertical frame is not required to be Japanese — a western
            // title down the spine of a book is still Latin.
            layoutScript,
            ordered.Select(column => column.LayoutBounds).Aggregate(Rect.Union),
            CombineGlyphSize(layoutScript, ordered));
    }

    /// <summary>
    /// Uses native-column pitch for the square overlay cells, independently of coverage bounds.
    /// Blocks without measured pitch retain the area-based fallback: sqrt(width * height / count)
    /// keeps an unresolved two-column detection from doubling the font size. The column width
    /// caps that estimate when recognition has read only a small part of a long box.
    /// </summary>
    private static double GroupGlyphSize(List<OcrTextBlock> columns)
    {
        var sizes = columns
            .Select(column =>
            {
                var characters = column.Text.Count(character => !char.IsWhiteSpace(character));

                // Native vertical OCR supplies pitch; legacy/unmeasured blocks fall back to area.
                if (column.RenderGlyphHeight is > 0)
                    return Math.Min(column.Bounds.Width, column.RenderGlyphHeight.Value);

                return characters > 0
                    ? Math.Min(
                        column.Bounds.Width,
                        Math.Sqrt(column.Bounds.Width * column.Bounds.Height / characters))
                    : column.Bounds.Width;
            })
            .Where(size => size > 0)
            .OrderBy(size => size)
            .ToList();

        return sizes.Count > 0 ? sizes[sizes.Count / 2] : 1;
    }

    /// <summary>
    /// One layout glyph size for a merged column group: the median of the columns', and nothing
    /// once the joined text is no longer of a single script.
    /// </summary>
    private static double? CombineGlyphSize(OcrLayoutScript script, List<OcrTextBlock> columns)
    {
        if (script is not (OcrLayoutScript.Latin or OcrLayoutScript.Cjk))
            return null;

        var sizes = columns
            .Where(column => column.LayoutGlyphHeight is > 0)
            .Select(column => column.LayoutGlyphHeight!.Value)
            .OrderBy(size => size)
            .ToList();

        return sizes.Count > 0 ? sizes[sizes.Count / 2] : null;
    }

    private static List<Rect> SplitIntoCharacterCells(Rect column, double glyphSize)
    {
        int cells = Math.Max(1, (int)Math.Round(column.Height / Math.Max(1, glyphSize)));
        return Enumerable.Range(0, cells)
            .Select(i => new Rect(
                column.X,
                column.Y + i * column.Height / cells,
                column.Width,
                column.Height / cells))
            .ToList();
    }
}
