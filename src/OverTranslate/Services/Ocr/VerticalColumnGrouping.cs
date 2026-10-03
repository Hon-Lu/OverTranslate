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
    /// <param name="language">
    /// The source language, normalised. With it the rows are set across as a horizontal capture sets
    /// them — see <see cref="WithRowsSetAcross"/>; without it they are handed on line by line, as
    /// they were before, which is what the tests that build columns by hand still ask for.
    /// </param>
    internal static List<OcrTextBlock> Group(
        List<OcrTextBlock> blocks, double frameWidth, bool realtime = false, Bitmap? bitmap = null,
        string? language = null)
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
        var groups = WithoutWordlessGroups(WithoutRowsOverColumns(WithoutRuby(merged), candidates));
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
        return language is null ? groups : WithRowsSetAcross(groups, language, realtime, pixels);
    }

    /// <summary>
    /// The rows that came through, read the way a horizontal capture reads them: measured as lines,
    /// joined into sentences, and given back their tilt.
    /// </summary>
    /// <remarks>
    /// <para>Before this a row was handed on exactly as the column pipeline prepared it — one line,
    /// one translation, a glyph size from its upright box's area. On a tilted card that is every
    /// line translated alone and drawn level over its neighbours, at three times the size of the
    /// type: what the horizontal pipeline stopped doing in #246–#248. A caption box of three level
    /// lines was three translations of a third of a sentence.</para>
    ///
    /// <para>LAST, and on the rows that survived, and that order is what keeps the columns where
    /// they were. Everything above reads the rows as they are prepared for columns — the ruby test
    /// takes a row as the body of a reading by its RenderGlyphHeight and Bounds, the row filters
    /// measure a row against the columns by its Bounds — and normalising a CJK line rewrites both.
    /// Doing it first would move readings and rows on pages where nothing runs across at an angle.
    /// So every decision about a column is made exactly as before, and only what is then drawn for
    /// the rows changes.</para>
    ///
    /// <para>Each row starts again from what the detector and recogniser gave — its text, box,
    /// score and quadrilateral — so that the chain it goes through is the horizontal one end to
    /// end, filters and all, rather than one that inherits half of the column preparation.</para>
    /// </remarks>
    private static List<OcrTextBlock> WithRowsSetAcross(
        List<OcrTextBlock> groups, string language, bool realtime, SkiaSharp.SKBitmap? pixels)
    {
        int first = groups.FindIndex(group => group.RunsAcross);
        if (first < 0)
            return groups;

        var rows = groups
            .Where(group => group.RunsAcross)
            .Select(row => new OcrTextBlock(row.Text, row.Bounds, Confidence: row.Confidence)
            {
                LineGeometry = row.LineGeometry,
            })
            .OrderBy(row => row.Bounds.Y)
            .ThenBy(row => row.Bounds.X)
            .ToList();

        var lines = OnnxOcrEngine.ApplyBlockFilters(
            rows,
            language,
            OcrLanguageRouter.UsesCjkOnnx(language),
            OcrLanguageRouter.UsesAutomaticLayout(language));

        // The live path has no block mode to honour here — a vertical block does not ask for one —
        // so its rows are grouped as a panel's are: the general rules, not the subtitle strip's.
        var set = realtime
            ? OcrService.GroupRealtime(lines, pixels?.Height ?? 0, Realtime.RealtimeBlockMode.Panel)
            : OcrService.GroupScreenshot(pixels, lines, GroupingProfile.General);

        var result = new List<OcrTextBlock>(groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            if (i == first)
                result.AddRange(set.Select(group => group with { RunsAcross = true }));
            if (!groups[i].RunsAcross)
                result.Add(groups[i]);
        }

        return result;
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
    internal static List<OcrTextBlock> WithoutRowsOverColumns(
        List<OcrTextBlock> groups, IReadOnlyList<OcrTextBlock>? columns = null) =>
        !groups.Any(group => group.RunsAcross) || !groups.Any(group => !group.RunsAcross)
            ? groups
            : [.. groups.Where(group => !group.RunsAcross || !(
                groups.Any(column =>
                    !column.RunsAcross && RunsDownThePage(column) &&
                    LiesOn(group.Bounds, column.Bounds) >= RowLyingOnColumns) ||
                (columns ?? []).Any(column =>
                    RunsDownThePage(column) && Near(group.Bounds, column.Bounds))))];

    /// <summary>
    /// Whether a row reaches a column once it is grown by its own height on every side.
    /// </summary>
    /// <remarks>
    /// <para>Asked of the COLUMNS as they were read, before any of them were joined, because on the
    /// pages this exists for the joined groups say nothing. MEASURED on
    /// <c>vertical-image-ja3/zang-songnofuriren-001-147hua</c>: the columns there are read in pieces —
    /// <c>空から降り注いだ</c> comes back as <c>空から</c> and single-character boxes — and the pieces
    /// joined make groups WIDER than they are tall (<c>空からそそりの</c> 132x94), so the rule above, which
    /// lets only a balloon overrule a row, never fires. Seven of the ten rows the screenshot flow
    /// returned lay on columns, and the page holds no writing across at all.</para>
    ///
    /// <para>Grown, because a row made of column heads does not always overlap what is left of those
    /// columns: the heads are gone from them, so each column's box starts just under the row.
    /// <c>調画</c> and <c>代遠</c> touch nothing and sit 0.7 of a row's height from the columns they were
    /// taken from. The whole height and not half: at half, <c>調画</c> and <c>俺流兄</c> stay.</para>
    ///
    /// <para>The user's terms: rows may be lost, the columns may not be touched. What it costs, over
    /// every vertical corpus: none of the eleven real rows on <c>vertical-image-ja2</c> (plates,
    /// narration boxes, scene labels); a series title on <c>vertical-image-ja3</c>, the magazine's
    /// cover titles and captions on <c>vertical-manga-web</c> and <c>vertical-image-ja</c>, and the one real
    /// row on the new pages (<c>次回、三度目の"魔法"</c>, in the realtime flow). No column group changes on
    /// any of them. Two narrower rules measured and left: touching without growing leaves four of the
    /// fakes, and "two columns start under it" catches none that growing does not and loses the same
    /// title.</para>
    /// </remarks>
    private static bool Near(Rect row, Rect column)
    {
        var grown = new Rect(
            row.X - row.Height, row.Y - row.Height, row.Width + 2 * row.Height, row.Height * 3);
        var shared = Rect.Intersect(grown, column);
        return !shared.IsEmpty && shared.Width > 0 && shared.Height > 0;
    }

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

            merged.AddRange(ReadingBlocks(group).Select(CombineColumns));
        }

        return merged;
    }

    /// <summary>
    /// How much of the shorter of two runs of columns has to lie alongside the other for the two to
    /// be one block, when a group is cut into the blocks a reader reads it as.
    /// </summary>
    /// <remarks>
    /// <para>A group of columns joined pair by pair can hold two blocks the page set apart: a remark
    /// and, stepped down to its left, the next one — 最後だからこそだ。 and 俺はロベルーア領総督の…
    /// (zang), だが、 and 誰かじゃ駄目なんだな。 Every column of the one runs alongside a column of the
    /// other, so the pairs join; the two RUNS, right and left of the step, hardly overlap. So each
    /// group is cut where the columns to the right of a cut and the columns to the left of it share
    /// least of their height, if they share less than this, and each side is asked again.</para>
    ///
    /// <para>MEASURED on the 58 vertical pages labelled block by block
    /// (<c>.ai/vertical-ja3-handoff/overmerge/blocks-*.json</c>), the best cut of every group: the
    /// ones between two blocks share 0.27–0.63 of the shorter run, and the ones through a block 0.71
    /// and more — a first column set higher than the rest, ちょっと悪いんだけど beside
    /// そこの魔法陣に入って頂戴 at 0.72, and ragged detections. The bar sits in the gap. One cut
    /// under it, at 0.46, goes through a column the detector ran across two blocks (below), which
    /// was two blocks in one group before it was cut. The manga
    /// models' bar for the same question (<see cref="Manga.MangaPageLayout.SideBySideShare"/>) is
    /// higher because it compares whole blocks the detector boxed, not columns with their ruby and
    /// fragments.</para>
    ///
    /// <para>No cut is made across the columns, for blocks one above the other. Pairs of columns
    /// one above the other do not join in the first place (<see cref="SideBySideAlongTheColumn"/>);
    /// where two such blocks came back as one group on those pages, the detector had read a column
    /// of the one and a column of the other as a single column (魔力を over この, 4px apart on zang
    /// 19 08 58), which no cut between columns can part.</para>
    /// </remarks>
    private const double RunsAlongside = 0.65;

    private static IEnumerable<List<OcrTextBlock>> ReadingBlocks(List<OcrTextBlock> group)
    {
        if (group.Count < 2)
        {
            yield return group;
            yield break;
        }

        // Right to left, as they are read.
        var ordered = group.OrderByDescending(column => (column.LayoutBounds.Left + column.LayoutBounds.Right) / 2).ToList();
        int cut = 0;
        double least = RunsAlongside;
        for (int k = 1; k < ordered.Count; k++)
        {
            var (rightTop, rightBottom) = Span(ordered.Take(k));
            var (leftTop, leftBottom) = Span(ordered.Skip(k));
            if (OneAboveTheOther(ordered.Take(k), ordered.Skip(k))) continue;

            double shared = Math.Min(rightBottom, leftBottom) - Math.Max(rightTop, leftTop);
            double shorter = Math.Min(rightBottom - rightTop, leftBottom - leftTop);
            if (shorter > 0 && shared < least * shorter)
            {
                least = shared / shorter;
                cut = k;
            }
        }

        if (cut == 0)
        {
            yield return group;
            yield break;
        }

        foreach (var part in ReadingBlocks(ordered.Take(cut).ToList())) yield return part;
        foreach (var part in ReadingBlocks(ordered.Skip(cut).ToList())) yield return part;

        static (double Top, double Bottom) Span(IEnumerable<OcrTextBlock> columns) =>
            (columns.Min(column => column.LayoutBounds.Top), columns.Max(column => column.LayoutBounds.Bottom));

        // Runs sharing more than half the narrower one's width are one above the other, not a step to
        // the left: a word whose column came back as two pieces, 霊 over 麻？ (ja3 432/002), 歌 over 吹
        // (472/007), their readings beside them nudging the pieces' centres apart. Parted, they were
        // two bubbles of half a word each; no cut between two blocks on the 58 labelled pages is lost.
        static bool OneAboveTheOther(IEnumerable<OcrTextBlock> right, IEnumerable<OcrTextBlock> left)
        {
            double rightLeft = right.Min(column => column.LayoutBounds.Left), rightRight = right.Max(column => column.LayoutBounds.Right);
            double leftLeft = left.Min(column => column.LayoutBounds.Left), leftRight = left.Max(column => column.LayoutBounds.Right);
            double shared = Math.Min(rightRight, leftRight) - Math.Max(rightLeft, leftLeft);
            return shared > 0.5 * Math.Min(rightRight - rightLeft, leftRight - leftLeft);
        }
    }

    private static bool IsColumnCandidate(OcrTextBlock column) => !RunsAcross(column);

    /// <summary>
    /// How much longer than thick a detector quadrilateral lying within 45° of level has to be for
    /// its writing to be taken as running across, whatever its upright box says.
    /// </summary>
    /// <remarks>
    /// <para>The upright box alone misreads a tilted line: turned 30°, a line of type has an upright
    /// box hardly wider than tall, and turned further it has one TALLER than wide. On the tilted
    /// cards of <c>region-comic-en-3</c> the narrowest long line was 310x200 — 1.55 against the bar
    /// of 1.4 below — and "TOO." at 55x41 went under it, was taken for a column and was not on the
    /// screen at all. The quadrilateral is not turned by the tilt: "TOO." measures 49x25 along
    /// itself.</para>
    ///
    /// <para>MEASURED over the 171 vertical pages, every box the two tests disagree on. Asked
    /// INSTEAD of the upright box, two to one demotes 20 boxes that are rows today — page numbers
    /// (184, 105), 読む, 剣聖, 心の壁, 1.7 to 1.9 along themselves — to columns, and still misses
    /// "TOO." at 1.96. So it is asked AS WELL, never instead: a box either test calls a row is one.
    /// At 1.6 that adds five boxes to the rows — "TOO.", びっいり and どど lettered at 36–38° on
    /// mokuro-001b, and two misreads, mn and 00 — and at 1.4 it adds twelve, the rest of them
    /// two-character readings and numbers that are squarer than any line.</para>
    /// </remarks>
    internal const double AcrossAlongItself = 1.6;

    /// <summary>
    /// Whether a block is writing that runs across the page — a name plate, a caption, a tilted
    /// line — rather than a column to be merged with the others.
    /// </summary>
    internal static bool RunsAcross(OcrTextBlock block)
    {
        int characters = block.Text.Count(character => !char.IsWhiteSpace(character));
        if (characters <= 1)
            return false;

        // Issue #132's Japanese corpus had six multi-character detections wider than 1.4: all six
        // were horizontal UI or signs, while none of the 188 vertical detections crossed it.
        const double maxWidthToHeightRatio = 1.4;
        return block.LayoutBounds.Width > block.LayoutBounds.Height * maxWidthToHeightRatio ||
               block.LineGeometry is { } line &&
               Math.Abs(line.AngleDegrees) <= OcrLineGeometry.TiltedToDegrees &&
               line.Length >= line.Thickness * AcrossAlongItself;
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

    /// <summary>
    /// How far apart two columns may be, in characters of the SMALLER type of the two.
    /// </summary>
    /// <remarks>
    /// <para>The distance bar is 1.6 characters of the larger type, which is right inside a balloon —
    /// a reading or a short column comes back with a pitch that is too small, and the larger one is
    /// the balloon's — and wrong between a line of big lettering and a balloon beside it. The big
    /// type stretches the bar to reach the next balloon: on ja3 432/002 the 134px column 火鉢の炭…！
    /// took in ご心配いただき恐縮ですわ／わたくしは無事です, 26px type, 4.1 of its characters away.</para>
    ///
    /// <para>MEASURED, every pair this method joins over the 175 vertical pages at hand, against the
    /// manga models' grouping of the same page: the pairs of one group are 1.6 of the smaller type
    /// apart or less in 1701 of 1904 and more than 4 in three: a reading over its word, a misread
    /// column, and 遠っ lettered beside a balloon, which the models' grouping had wrong. Taking the
    /// smaller pitch for the whole bar instead split balloons everywhere (zang, ja3 and ja2 lost 14,
    /// 10 and 17 whole sentences); capping it at 4 changed six pages of the 175, no sentence on the
    /// transcribed ones, and parted the lettering from the balloons on two.</para>
    /// </remarks>
    private const double SmallerTypeReach = 4;

    private static bool IsSameGroup(OcrTextBlock a, OcrTextBlock b)
    {
        // Detector padding is not character size. Compare centres and character pitch so
        // a generous quad cannot bridge a gutter into the next balloon or manga panel.
        double pa = VerticalOcrGeometry.GlyphPitch(a), pb = VerticalOcrGeometry.GlyphPitch(b);

        double shared = Math.Min(a.LayoutBounds.Bottom, b.LayoutBounds.Bottom) -
                        Math.Max(a.LayoutBounds.Top, b.LayoutBounds.Top);
        double shorter = Math.Min(a.LayoutBounds.Height, b.LayoutBounds.Height);
        if (shorter <= 0 || shared < shorter * SideBySideAlongTheColumn)
            return false;

        double distance = Math.Abs((a.LayoutBounds.Left + a.LayoutBounds.Right) / 2 -
                                   (b.LayoutBounds.Left + b.LayoutBounds.Right) / 2);
        return distance <= Math.Max(pa, pb) * 1.6 && distance <= Math.Min(pa, pb) * SmallerTypeReach;
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
            CombineGlyphSize(layoutScript, ordered))
        {
            // Only how the group is drawn and erased: what it says, where it is and how big its
            // letters are stay as they would be for a straight group — see TiltedColumns.
            Tilt = TiltedColumns.For(ordered),
        };
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
