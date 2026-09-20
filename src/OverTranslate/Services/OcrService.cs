using System.Drawing;
using System.Windows;
using OverTranslate.Services.Ocr;

namespace OverTranslate.Services;

public record OcrTextBlock(
    string Text,
    System.Windows.Rect Bounds,
    IReadOnlyList<System.Windows.Rect>? SourceLineBounds = null,
    // Visual glyph height (physical px) used to size the overlay font, kept separate from
    // Bounds. For Latin source the detection box is much taller than the rendered CJK font,
    // so Bounds stays full (for background coverage) while this drives only the font size.
    // Null for CJK, where Bounds already matches the glyph height.
    double? RenderGlyphHeight = null,
    // Mean per-character recognition confidence, 0–1. Reading the same unchanged text twice
    // gives two slightly different answers, and this is what says which to believe; null when
    // the engine reported no scores.
    double? Confidence = null,
    // Writing system of this block's own text, for the grouping geometry to reason about. Never
    // derived from the source language the user picked — that is the whole point of it existing.
    OcrLayoutScript LayoutScript = OcrLayoutScript.Unknown,
    // The detector's own box, before any script-specific normalisation. Bounds is not comparable
    // across scripts — a CJK one is pulled in onto its glyphs and a Latin one is not, a ratio of
    // 0.820 that refused every mixed-script pair on size alone. This is what grouping measures
    // with: one detector, one procedure, whatever the text turns out to be.
    System.Windows.Rect LayoutBounds = default,
    // Estimated glyph body height for LayoutScript, from LayoutBounds. Comparable within one
    // script only: a Latin box carries ascender and descender room a CJK one does not, and no
    // constant converts between them — that was measured, and it is a property of the text
    // rather than of the scripts. Null for Mixed and Unknown, which have no single answer.
    double? LayoutGlyphHeight = null)
{
    public IReadOnlyList<System.Windows.Rect> Lines => SourceLineBounds ?? [Bounds];

    // Optional screenshot-only evidence; never used to size the rendered translation.
    public double? LayoutInkHeight { get; init; }

    /// <summary>
    /// This block's own text runs across the page rather than down it.
    /// </summary>
    /// <remarks>
    /// <para>Only the vertical pipeline sets this, and only for what it found that is not a column:
    /// a name plate, a caption box, a scene label, the chapter-end line. A page of vertical writing
    /// is not made only of vertical writing, and the overlay cannot tell the two apart once they
    /// are side by side — a short column and a short row are the same rectangle. So the stage that
    /// does know says so here.</para>
    ///
    /// <para>Left false by the horizontal pipeline, where everything runs across and nothing needs
    /// telling. Read only on the vertical branch of either overlay, which is the one place where
    /// "not a column" is the thing it means.</para>
    /// </remarks>
    public bool RunsAcross { get; init; }
}

public class OcrService : IDisposable
{
    private readonly OnnxOcrEngine _engine = new();

    /// <param name="layoutMode">
    /// What the user said this capture holds. The only thing it decides here is which thresholds
    /// grouping runs on; where the translation is then placed is decided by the caller, which is
    /// the one layer that reads the mode itself.
    /// </param>
    public Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken = default,
        bool verticalText = false,
        CaptureLayoutMode layoutMode = CaptureLayoutMode.General)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        var language = OcrLanguageRouter.Normalize(sourceLanguage);

        // The mode picks the thresholds for horizontal text only. Vertical has its own profile and
        // does not take a parameter for one — see RecognizeVerticalAsync.
        return verticalText
            ? RecognizeVerticalAsync(_engine, bitmap, language, cancellationToken)
            : RecognizeAndGroupAsync(_engine, bitmap, language, GroupingProfile.For(layoutMode), cancellationToken);
    }

    /// <summary>
    /// Recognises only if the engine has a free slot right now, returning null instead of queueing.
    /// For callers watching a live screen, where a queued pass would be answering a frame that has
    /// already been replaced — see <see cref="IOcrEngine.TryRecognizeAsync"/>.
    /// </summary>
    /// <param name="mode">The live region's mode. Panel preserves the historical path for callers
    /// that do not specify one; the application always passes its region's explicit mode.</param>
    /// <param name="orientation">
    /// Which way the region's text is written. Vertical detects in source coordinates and uses the
    /// vertical pipeline — see <see cref="TryRecognizeVerticalAsync"/>; the mode does not reach that
    /// path because horizontal row thresholds do not describe column geometry.
    /// </param>
    public async Task<List<OcrTextBlock>?> TryRecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        int? maxDetectSize = null,
        CancellationToken cancellationToken = default,
        Realtime.RealtimeBlockMode mode = Realtime.RealtimeBlockMode.Panel,
        Realtime.RealtimeTextOrientation orientation = Realtime.RealtimeTextOrientation.Horizontal)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        if (orientation == Realtime.RealtimeTextOrientation.Vertical)
        {
            return await TryRecognizeVerticalAsync(
                _engine, bitmap, OcrLanguageRouter.Normalize(sourceLanguage), maxDetectSize, cancellationToken);
        }

        var blocks = await _engine.TryRecognizeAsync(
            bitmap, OcrLanguageRouter.Normalize(sourceLanguage), maxDetectSize, cancellationToken);

        if (blocks is null)
            return null;

        // Before grouping, and that is the whole point of doing it here rather than in the caller.
        // Grouping merges boxes that overlap, and a scenery box sitting across a subtitle is merged
        // into it: measured on a 1623x206 region, a 220px box reading "EIN" was joined to the real
        // 136px line "Arisa's a big meanie.", and the merged box — 220px in a 206px block — was
        // then thrown out as a collapse, taking the subtitle with it. Filtered afterwards the
        // subtitle is already tied to the noise and cannot be recovered.
        //
        // The live-screen path's own profile, and deliberately not the screenshot side's Standard.
        // There is no toolbar in front of a running video, so there is no CaptureLayoutMode to
        // honour here; taking one would mean a mode the user chose for a still capture silently
        // steering frames it was never asked about.
        return GroupRealtime(blocks, bitmap.Height, mode);
    }

    /// <summary>Reads original-frame columns without queueing a busy realtime engine.</summary>
    internal static async Task<List<OcrTextBlock>?> TryRecognizeVerticalAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string language,
        int? maxDetectSize,
        CancellationToken cancellationToken)
    {
        var blocks = await engine.TryRecognizeAsync(
            bitmap, language, maxDetectSize, cancellationToken, verticalText: true);
        return blocks is null ? null : GroupVertical(blocks, bitmap.Width, realtime: true, bitmap: bitmap);
    }

    /// <summary>
    /// Columns are assembled right to left; the horizontal writing on the same page is kept beside
    /// them rather than thrown away.
    /// </summary>
    /// <remarks>
    /// <para>MEASURED. Keeping the rows out of the column merge is necessary — a box thrown across
    /// two columns joins them into one block, which is what
    /// <see cref="IsVerticalColumnCandidate"/> is for. Dropping them from the OUTPUT as well was a
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
    internal static List<OcrTextBlock> GroupVertical(
        List<OcrTextBlock> blocks, double frameWidth, bool realtime = false, Bitmap? bitmap = null)
    {
        if (realtime)
            blocks = RejectUnconvincingBlocks(blocks)
                .Where(block => !Realtime.CollapsedDetection.IsCollapsed(
                    block.Bounds.Width, frameWidth, block.Text)).ToList();

        // Before anything joins or groups: a reading merged into a sentence cannot be taken back
        // out of it afterwards, which is what Ocr.VerticalRubyColumns exists to say.
        blocks = Ocr.VerticalRubyColumns.Drop(blocks);

        var candidates = new List<OcrTextBlock>();
        var across = new List<OcrTextBlock>();
        foreach (var block in blocks)
            (IsVerticalColumnCandidate(block) ? candidates : across).Add(block);

        using var pixels = bitmap is null ? null : OnnxOcrEngine.ConvertToSkBitmap(bitmap);

        var merged = MergeVerticalColumns(
            VerticalOcrGeometry.JoinColumnFragments(candidates, pixels));
        merged.AddRange(across.Select(block => block with { RunsAcross = true }));

        // Last, so that a reading sitting on a row is judged against it too — the furigana over a
        // sign reads the same way whether the sign runs down the page or across it. What keeps a
        // reading from being swallowed before it gets here is the size test in
        // IsSameVerticalTextGroup, not the order of these two lines.
        return WithoutRuby(merged);
    }

    /// <summary>
    /// Whether this frame holds anything worth recognising, asked with detection alone at a
    /// fraction of the usual size — see <see cref="Realtime.RealtimeGate"/>.
    /// </summary>
    /// <returns>
    /// The boxes that cleared the score bar, in the bitmap's own coordinates; an empty list when the
    /// frame looks empty; null when no inference slot was free, which is not an answer either way.
    /// </returns>
    public Task<IReadOnlyList<System.Windows.Rect>?> TryDetectTextAsync(
        Bitmap bitmap,
        string sourceLanguage,
        int maxDetectSize,
        float minimumScore,
        CancellationToken cancellationToken = default) =>
        _engine.TryDetectTextAsync(
            bitmap, OcrLanguageRouter.Normalize(sourceLanguage), maxDetectSize, minimumScore,
            cancellationToken);

    /// <param name="decisions">
    /// Diagnostic only, and null everywhere but OcrHarness. Passed to whichever grouper this mode
    /// really uses, so <c>--group-explain --realtime</c> reports the branch that ran rather than
    /// the other one.
    /// </param>
    internal static List<OcrTextBlock> GroupRealtime(List<OcrTextBlock> blocks, double frameHeight,
        Realtime.RealtimeBlockMode mode, GroupingTrace? trace = null,
        List<OcrTextBlockGrouper.NextLineDecision>? decisions = null)
    {
        var filtered = RejectUnconvincingBlocks(blocks);
        if (mode != Realtime.RealtimeBlockMode.Subtitle)
            return OcrTextBlockGrouper.Group(filtered, GroupingProfile.Realtime, decisions, trace);
        trace?.RegisterBlocks(blocks);
        // Remove scene-sized noise before it can contaminate a real dialogue row. Confident
        // single letters (such as a split "I") may still join; isolated ones are filtered later.
        filtered = filtered.Where(b => !Realtime.CollapsedDetection.IsCollapsed(b.Bounds.Height, frameHeight, b.Text)).ToList();
        return Realtime.DialogueTextGrouper.Group(filtered, trace, decisions);
    }

    // Scenery the recogniser was not sure about. Only on this path: it is the realtime one, where
    // the floor was measured, and where the next frame is a poll away so losing a doubtful reading
    // costs nothing. The screenshot path keeps everything — its user framed that capture once and
    // is waiting for it.
    //
    // "Costs nothing" is not quite free, and issue #85 is where the exception was measured. When the
    // detector splits one subtitle line horizontally, the tail fragment can come back short and
    // unconfident — a real ending, judged by a rule written for scenery — and because this runs
    // before grouping, MergeSameLineFragments never gets to put it back on its sentence. The line
    // reaches the screen a few characters short for one pass, then the next read finds it whole.
    //
    // The obvious fix is to filter after grouping, and the paragraph above is why that is worse. The
    // narrower one — keep a doubtful fragment when the grouper would merge it into a line long
    // enough to be real — was measured instead, with OcrHarness --reject-audit over 163 frames of
    // the subtitle corpus that read anything:
    //
    //   fragments dropped        54
    //     would have merged       1   conf=0.79, a 5-character tail joining a 19-character line
    //     isolated               53   noise, and the narrowed rule would still drop every one
    //
    // So it would admit no noise here and rescue one fragment in 163 frames — 0.6%, matching the
    // 2-in-307 measured on a live session in #85. That is not enough to move a rule standing on 45
    // measured readings (see ShortReadingDetection: everything from 0.60 to 0.79 was scenery, no
    // exceptions), and the corpus cannot say the carve-out is safe either: it contains no instance
    // of the failure this ordering exists to prevent, so "no noise admitted" is an absence of the
    // test case, not a pass. Left alone deliberately. What would reopen it is a corpus with scenery
    // sitting on a subtitle's own row.
    internal static List<OcrTextBlock> RejectUnconvincingBlocks(List<OcrTextBlock> blocks)
    {
        List<OcrTextBlock>? kept = null;

        for (var index = 0; index < blocks.Count; index++)
        {
            if (!Realtime.ShortReadingDetection.IsUnconvincingShortText(
                    blocks[index].Text, blocks[index].Confidence))
            {
                kept?.Add(blocks[index]);
                continue;
            }

            kept ??= [.. blocks.Take(index)];
        }

        return kept ?? blocks;
    }

    /// <summary>
    /// Keeps the loaded model in memory while a continuous caller is running — see
    /// <see cref="OnnxOcrEngine.SetKeepWarm"/>.
    /// </summary>
    public void SetKeepWarm(bool keepWarm) => _engine.SetKeepWarm(keepWarm);

    /// <summary>
    /// Releases the loaded model immediately rather than after the inactivity delay — see
    /// <see cref="OnnxOcrEngine.ReleaseNow"/>.
    /// </summary>
    public void ReleaseModel() => _engine.ReleaseNow();

    /// <summary>
    /// How many recognitions may run at once. Exposed so a caller that was turned away can say how
    /// many slots there were, which is the number that makes the refusal mean anything.
    /// </summary>
    public static int ConcurrentRecognitions => OnnxOcrEngine.ConcurrentRecognitions;

    public void Dispose()
    {
        _engine.Dispose();
    }

    private static async Task<List<OcrTextBlock>> RecognizeAndGroupAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        GroupingProfile profile,
        CancellationToken cancellationToken)
    {
        var blocks = await engine.RecognizeAsync(bitmap, sourceLanguage, cancellationToken);
        blocks = PrepareScreenshotGrouping(bitmap, blocks, profile);
        return OcrTextBlockGrouper.Group(blocks, profile);
    }

    internal static List<OcrTextBlock> PrepareScreenshotGrouping(
        Bitmap bitmap, List<OcrTextBlock> blocks, GroupingProfile profile) =>
        profile.SolidLineAdvanceWhenWrapped > OcrTextBlockGrouper.SolidLineAdvance
            ? TextInkMetrics.Annotate(bitmap, blocks)
            : blocks;

    /// <summary>
    /// Detects columns in the original frame; only recognition crops change orientation.
    /// Screenshot and realtime share the same source-coordinate grouping.
    /// </summary>
    internal static async Task<List<OcrTextBlock>> RecognizeVerticalAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        var blocks = await engine.RecognizeAsync(
            bitmap, sourceLanguage, cancellationToken, verticalText: true);
        return GroupVertical(blocks, bitmap.Width, bitmap: bitmap);
    }

    /// <summary>
    /// Reassembles adjacent right-to-left columns that share a top edge. A lone column is split
    /// into character cells so overlay layout still receives a usable vertical footprint.
    /// </summary>
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

    internal static List<OcrTextBlock> MergeVerticalColumns(List<OcrTextBlock> columns)
    {
        var remaining = columns
            .Where(IsVerticalColumnCandidate)
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
                    if (!group.Any(member => IsSameVerticalTextGroup(member, remaining[i])))
                        continue;

                    group.Add(remaining[i]);
                    remaining.RemoveAt(i);
                    grew = true;
                }
            }

            merged.Add(CombineVerticalColumns(group));
        }

        return merged;
    }

    private static bool IsVerticalColumnCandidate(OcrTextBlock column)
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
    private const double SideBySideAlongTheColumn = 0.5;

    private static bool IsSameVerticalTextGroup(OcrTextBlock a, OcrTextBlock b)
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

    private static OcrTextBlock CombineVerticalColumns(List<OcrTextBlock> group)
    {
        // Reading order is a layout question, so it is decided on the detector's boxes. Everything
        // built below — the coverage rectangle, the cell size, the character cells — is what the
        // overlay draws, and stays on Bounds.
        var ordered = group.OrderByDescending(column => column.LayoutBounds.X).ToList();
        var bounds = ordered.Select(column => column.Bounds).Aggregate(Rect.Union);
        var glyphSize = VerticalGlyphSize(ordered);
        var lines = ordered.Count > 1
            ? ordered.Select(column => column.Bounds).ToList()
            : SplitIntoVerticalCharacterCells(bounds, glyphSize);

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
            CombineVerticalGlyphSize(layoutScript, ordered));
    }

    /// <summary>
    /// Uses native-column pitch for the square overlay cells, independently of coverage bounds.
    /// Blocks without measured pitch retain the area-based fallback: sqrt(width * height / count)
    /// keeps an unresolved two-column detection from doubling the font size. The column width
    /// caps that estimate when recognition has read only a small part of a long box.
    /// </summary>
    private static double VerticalGlyphSize(List<OcrTextBlock> columns)
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
    private static double? CombineVerticalGlyphSize(OcrLayoutScript script, List<OcrTextBlock> columns)
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

    private static List<Rect> SplitIntoVerticalCharacterCells(Rect column, double glyphSize)
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
