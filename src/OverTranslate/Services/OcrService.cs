using System.Drawing;
using System.Windows;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Ocr.Manga;

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
    /// The glyph height (physical px) the overlay font is sized from, where that differs in meaning
    /// from <see cref="RenderGlyphHeight"/>.
    /// </summary>
    /// <remarks>
    /// The two are the same number on a level line. On a tilted one they part: the font wants the
    /// height of the letters, measured along the line (see <see cref="Ocr.OcrLineGeometry"/>), while
    /// <see cref="RenderGlyphHeight"/> is also what the realtime overlay erases and covers with, and
    /// a tilted line's letters reach the full height of its upright box. So this one carries the
    /// letters and that one keeps the coverage. Null wherever no font height was estimated; the
    /// overlays then size from <see cref="RenderGlyphHeight"/>, as they always did.
    /// </remarks>
    public double? FontGlyphHeight { get; init; }

    /// <summary>
    /// The detector's own box measured along and across the line — see <see cref="Ocr.OcrLineGeometry"/>.
    /// Set on conversion, read by normalisation, and not carried through grouping: a group has no
    /// single quadrilateral.
    /// </summary>
    internal Ocr.OcrLineGeometry? LineGeometry { get; init; }

    /// <summary>
    /// The box the detector drew, held aside while <see cref="LayoutBounds"/> carries a level one for
    /// the grouping rules — see <see cref="Ocr.TiltedLayout"/>. Null on every level line, and null
    /// again once grouping is done and the upright box is back.
    /// </summary>
    internal System.Windows.Rect? UprightLayoutBounds { get; init; }

    /// <summary>
    /// The tilted lines this block was made of, as <see cref="Ocr.TiltedLayout"/> levelled them —
    /// held while grouping runs, and turned into <see cref="Tilt"/> once it is done. Null on every
    /// level line, and on a group any of whose pieces was level.
    /// </summary>
    internal IReadOnlyList<Ocr.TiltedLine>? TiltedLines { get; init; }

    /// <summary>
    /// How a group read off a tilted card is to be drawn — see <see cref="Ocr.TiltedText"/>. Null on
    /// everything else, which is drawn exactly as it always was.
    /// </summary>
    internal Ocr.TiltedText? Tilt { get; init; }

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

    /// <summary>
    /// What a live block in 自動 took this group for: a passage read straight through, or one of a
    /// set of separate items — see <see cref="Realtime.RealtimeAutoLayout"/>.
    /// </summary>
    /// <remarks>
    /// Null everywhere else. A block the user set to one mode reads every group as that mode, and
    /// the screenshot flow has no such question to answer.
    /// </remarks>
    public Realtime.RealtimeBlockMode? ReadAs { get; init; }
}

public class OcrService : IDisposable
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly OnnxOcrEngine _engine = new();
    private readonly MangaOcrEngine? _manga;

    public OcrService()
    {
    }

    /// <param name="mangaModels">
    /// Where the downloadable manga models are. With them, Japanese vertical text is read by those
    /// models on the GPU whenever they can be used — see <see cref="RecognizeVerticalAsync"/>.
    /// </param>
    internal OcrService(MangaModelStore mangaModels) => _manga = new MangaOcrEngine(mangaModels);

    /// <summary>For tests, which hand in a manga engine whose adapter and loads they control.</summary>
    internal OcrService(MangaOcrEngine manga) => _manga = manga;

    /// <summary>The manga models' engine, or null in a build that was not given them.</summary>
    internal MangaOcrEngine? Manga => _manga;

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
        return LevelCrowdedTilts(verticalText
            ? RecognizeVerticalAsync(_engine, bitmap, language, cancellationToken, _manga)
            : RecognizeAndGroupAsync(_engine, bitmap, language, GroupingProfile.For(layoutMode), cancellationToken));
    }

    // Here, where every path's groups are final — columns and the lines across a vertical page
    // alike — because the groups that collide need not have been grouped together. See
    // Ocr.TiltedOverlap.
    private static async Task<List<OcrTextBlock>> LevelCrowdedTilts(Task<List<OcrTextBlock>> groups) =>
        TiltedOverlap.Level(await groups);

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
    public Task<List<OcrTextBlock>?> TryRecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        int? maxDetectSize = null,
        CancellationToken cancellationToken = default,
        Realtime.RealtimeBlockMode mode = Realtime.RealtimeBlockMode.Panel,
        Realtime.RealtimeTextOrientation orientation = Realtime.RealtimeTextOrientation.Horizontal) =>
        TryRecognizeAsync(
            bitmap, sourceLanguage, maxDetectSize, cancellationToken,
            Realtime.ResolvedProfile.ForLayout(mode), orientation);

    /// <inheritdoc cref="TryRecognizeAsync(Bitmap, string, int?, CancellationToken, Realtime.RealtimeBlockMode, Realtime.RealtimeTextOrientation)"/>
    /// <param name="profile">
    /// The live region's mode, as the pipeline runs it. Read only by text written across: the
    /// column path groups on its own figures whatever the mode says.
    /// </param>
    public async Task<List<OcrTextBlock>?> TryRecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        int? maxDetectSize,
        CancellationToken cancellationToken,
        Realtime.ResolvedProfile profile,
        Realtime.RealtimeTextOrientation orientation = Realtime.RealtimeTextOrientation.Horizontal)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        if (orientation == Realtime.RealtimeTextOrientation.Vertical)
        {
            var columns = await TryRecognizeVerticalAsync(
                _engine, bitmap, OcrLanguageRouter.Normalize(sourceLanguage), maxDetectSize, cancellationToken,
                _manga);
            return columns is null ? null : TiltedOverlap.Level(columns);
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
        return TiltedOverlap.Level(GroupRealtime(blocks, bitmap.Height, profile, edges: new Ocr.PanelEdges(bitmap)));
    }

    /// <summary>
    /// The column detector alone, for the manga path to tell which of its blocks are tilted — see
    /// <see cref="Ocr.Manga.MangaColumnAngles"/>. Null for an engine that is not the ONNX one.
    /// </summary>
    private static Func<Bitmap, IReadOnlyList<SkiaSharp.SKPointI[]>>? ColumnDetector(IOcrEngine engine, string language) =>
        engine is OnnxOcrEngine onnx ? mosaic => onnx.DetectQuads(mosaic, language) : null;

    /// <summary>
    /// The column detector as it detects a vertical page, for the manga path to tell which way each
    /// block's lines run — see <see cref="Ocr.Manga.MangaLineGeometry"/>. Null for an engine that is
    /// not the ONNX one, which leaves every block to its shape.
    /// </summary>
    private static Func<Bitmap, IReadOnlyList<SkiaSharp.SKPointI[]>>? LineDetector(IOcrEngine engine, string language) =>
        engine is OnnxOcrEngine onnx ? mosaic => onnx.DetectLineQuads(mosaic, language) : null;

    /// <summary>Reads original-frame columns without queueing a busy realtime engine.</summary>
    /// <remarks>
    /// With <paramref name="manga"/> usable the page goes to the manga models instead, as on the
    /// screenshot path — see <see cref="RecognizeVerticalAsync"/>. A GPU busy with another page is
    /// null here, like a busy column engine: the next poll asks again.
    /// </remarks>
    internal static async Task<List<OcrTextBlock>?> TryRecognizeVerticalAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string language,
        int? maxDetectSize,
        CancellationToken cancellationToken,
        MangaOcrEngine? manga = null)
    {
        if (manga is not null && ReadsWithMangaModels(language))
        {
            var (outcome, read) = await MangaVerticalReader.ReadAsync(
                manga, bitmap, wait: false, engine, language, maxDetectSize, cancellationToken,
                ColumnDetector(engine, language), LineDetector(engine, language));
            if (outcome == MangaReadOutcome.Busy) return null;
            if (outcome == MangaReadOutcome.Read) return read;
        }

        var blocks = await engine.TryRecognizeAsync(
            bitmap, language, maxDetectSize, cancellationToken, verticalText: true);
        return blocks is null
            ? null
            : Ocr.VerticalColumnGrouping.Group(
                blocks, bitmap.Width, realtime: true, bitmap: bitmap, language: language);
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
        List<OcrTextBlockGrouper.NextLineDecision>? decisions = null) =>
        GroupRealtime(blocks, frameHeight, Realtime.ResolvedProfile.ForLayout(mode), trace, decisions);

    /// <inheritdoc cref="GroupRealtime(List{OcrTextBlock}, double, Realtime.RealtimeBlockMode, GroupingTrace?, List{OcrTextBlockGrouper.NextLineDecision}?)"/>
    /// <param name="profile">The block's mode: its layout, and the figures each grouper runs on.</param>
    /// <param name="edges">
    /// The frame the blocks were read from, for the panel grouper to tell a panel's frame between
    /// two boxes on a row — see <see cref="Ocr.PanelEdges"/>. Only the panel grouper asks: the
    /// dialogue grouper joins whole rows into passages and is left as it was measured.
    /// </param>
    internal static List<OcrTextBlock> GroupRealtime(List<OcrTextBlock> blocks, double frameHeight,
        Realtime.ResolvedProfile profile, GroupingTrace? trace = null,
        List<OcrTextBlockGrouper.NextLineDecision>? decisions = null, Ocr.PanelEdges? edges = null)
    {
        // Tilted cards are levelled for the grouping rules and given back upright afterwards — see
        // Ocr.TiltedLayout. Before the filters, which read only text, confidence and Bounds, so that
        // the harness trace below registers the same instances the groupers then see.
        blocks = TiltedLayout.Straighten(blocks);
        return TiltedLayout.Restore(GroupStraightened(blocks, frameHeight, profile, trace, decisions, edges));
    }

    private static List<OcrTextBlock> GroupStraightened(List<OcrTextBlock> blocks, double frameHeight,
        Realtime.ResolvedProfile profile, GroupingTrace? trace,
        List<OcrTextBlockGrouper.NextLineDecision>? decisions, Ocr.PanelEdges? edges)
    {
        // 自動 runs both groupers and keeps, cluster by cluster, the one its text calls for. Both
        // are given what the dialogue grouper is given below: a box thrown across the whole block
        // is thrown out by the session whichever mode read it, and taking it out first is what
        // keeps it from being joined to a real line on the way.
        if (profile.Merge == Realtime.RealtimeMerge.Auto)
            return Realtime.RealtimeAutoLayout.Group(FilterForRealtime(blocks, frameHeight, profile), profile.Auto, edges);

        // 不合併 is filtered as 逐項 is and 合併 as 整段 is, being drawn and read as them.
        var filtered = IgnoreSmallText(
            RejectUnconvincingBlocks(blocks, profile.ConfidenceFloor), profile.IgnoreSmallFraction);
        switch (profile.Merge)
        {
            case Realtime.RealtimeMerge.Lines:
                return OcrTextBlockGrouper.GroupLines(filtered, profile.PanelGrouping, decisions, trace, edges);
            case Realtime.RealtimeMerge.Items:
                return OcrTextBlockGrouper.Group(filtered, profile.PanelGrouping, decisions, trace, edges);
        }
        // Remove scene-sized noise before it can contaminate a real dialogue row. Confident
        // single letters (such as a split "I") may still join; isolated ones are filtered later.
        filtered = filtered.Where(b => !Realtime.CollapsedDetection.IsCollapsed(b.Bounds.Height, frameHeight, b.Text)).ToList();
        trace?.RegisterBlocks(blocks);
        // The dialogue grouper has rules of its own and does not go through OcrTextBlockGrouper,
        // so the readings have to be taken out for it separately. See Ocr.HorizontalRubyLines.
        return Realtime.DialogueTextGrouper.Group(
            Ocr.HorizontalRubyLines.Drop(filtered), trace, decisions, profile.MaxStackGap,
            merge: profile.Merge == Realtime.RealtimeMerge.Merged);
    }

    /// <summary>
    /// A live frame's detected lines with the two kinds of scenery taken out: readings the
    /// recogniser was not sure of, and boxes thrown across the whole block.
    /// </summary>
    internal static List<OcrTextBlock> FilterForRealtime(List<OcrTextBlock> blocks, double frameHeight) =>
        FilterForRealtime(blocks, frameHeight, Realtime.ResolvedProfile.Standard);

    /// <inheritdoc cref="FilterForRealtime(List{OcrTextBlock}, double)"/>
    /// <remarks>
    /// With the block's mode's confidence floor, and without the lines it says are too small to
    /// read (過濾小字) — measured after the boxes across the block are gone, which would otherwise
    /// count towards the frame's main text.
    /// </remarks>
    internal static List<OcrTextBlock> FilterForRealtime(
        List<OcrTextBlock> blocks, double frameHeight, Realtime.ResolvedProfile profile) =>
        IgnoreSmallText(
            [.. RejectUnconvincingBlocks(blocks, profile.ConfidenceFloor)
                .Where(b => !Realtime.CollapsedDetection.IsCollapsed(b.Bounds.Height, frameHeight, b.Text))],
            profile.IgnoreSmallFraction);

    /// <summary>
    /// 過濾小字: the lines set lower than <paramref name="fraction"/> of the frame's main text,
    /// taken out before grouping. The same list when the fraction is zero, which is every preset.
    /// </summary>
    /// <remarks>
    /// <para>The main text is the size most of the frame's prose is set in — the characters of the
    /// lines that hold more than a word or two — not its tallest line: one heading would otherwise
    /// make the dialogue under it "small". Heights within
    /// <see cref="Layout.RealtimeTextSizes.SameSizeReach"/> of each other are one size, as the
    /// overlay sorts them, because a line's measured height wanders by that much on its own.</para>
    ///
    /// <para>Prose rather than every character because of a subtitle over a game's interface:
    /// sixteen labels, scores and timers outnumber one subtitle line in characters, so counting
    /// them all made the interface the main text and 50% kept every label. Counting prose, the
    /// subtitle is the main text and the labels go. A frame with no prose at all counts everything.
    /// MEASURED with OcrHarness --auto-probe drops over the 自動 corpus (6,620 lines): the dialogue
    /// captures lose exactly the same lines either way at every percentage, the mixed ones lose
    /// 147 lines at 50% rather than 83 — the labels around a subtitle — and the panel captures 10
    /// rather than 9.</para>
    ///
    /// <para>What goes, by percentage, on the dialogue captures: nothing at 20%; an FPS counter, a
    /// watermark, key hints and the odd name plate by 50%; the name plates and buttons (Auto,
    /// Comment, SKIP) by 80%, the dialogue itself never. The readings printed over kanji (ja-ruby)
    /// measure 0.3–0.75 of the line they
    /// sit on, the detector's boxes being looser around small text than around large, so the
    /// smallest go at 50% and all of them by 80%. On a page of prose under a large heading
    /// (web-cjk-prose) the body is the main text and stays.</para>
    /// </remarks>
    internal static List<OcrTextBlock> IgnoreSmallText(List<OcrTextBlock> blocks, double fraction)
    {
        if (fraction <= 0 || blocks.Count < 2) return blocks;

        double main = MainTextHeight(blocks);
        if (main <= 0) return blocks;
        double floor = main * fraction;
        return blocks.All(b => b.LayoutBounds.Height >= floor)
            ? blocks
            : [.. blocks.Where(b => b.LayoutBounds.Height >= floor)];
    }

    // The middle height of the size that holds the most characters of prose, or of anything when
    // no line reads as prose; a tie goes to the taller size.
    internal static double MainTextHeight(List<OcrTextBlock> blocks)
    {
        var sizes = new List<List<(double Height, int Characters, bool Prose)>>();
        foreach (var block in blocks.OrderByDescending(b => b.LayoutBounds.Height))
        {
            double height = block.LayoutBounds.Height;
            if (height <= 0) continue;
            int characters = Math.Max(1, block.Text.Count(c => !char.IsWhiteSpace(c)));
            var line = (height, characters, ReadsAsProse(block.Text));
            if (sizes.Count > 0 && sizes[^1][0].Height <= height * Layout.RealtimeTextSizes.SameSizeReach)
                sizes[^1].Add(line);
            else
                sizes.Add([line]);
        }
        if (sizes.Count == 0) return 0;

        bool anyProse = sizes.Any(size => size.Any(line => line.Prose));
        var main = sizes.MaxBy(size => size.Sum(line => anyProse && !line.Prose ? 0 : line.Characters))!;
        return main[main.Count / 2].Height;
    }

    // More than a word or two on one line: three words, or six characters of writing set without
    // spaces — RealtimeAutoLayout's "wordy", for a single line. Korean counts in words.
    private static bool ReadsAsProse(string text)
    {
        int solid = 0, unspaced = 0;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c)) continue;
            solid++;
            if (c is >= '぀' and <= 'ヿ' or >= '一' and <= '鿿') unspaced++;
        }
        if (unspaced * 2 >= Math.Max(1, solid)) return unspaced >= 6;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Count(word => word.Any(char.IsLetter)) >= 3;
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
    //
    // The floor is the block's mode's (短句辨識門檻), the measured one unless the user moved it.
    internal static List<OcrTextBlock> RejectUnconvincingBlocks(
        List<OcrTextBlock> blocks, double floor = Realtime.ShortReadingDetection.ShortTextConfidenceFloor)
    {
        List<OcrTextBlock>? kept = null;

        for (var index = 0; index < blocks.Count; index++)
        {
            if (!Realtime.ShortReadingDetection.IsUnconvincingShortText(
                    blocks[index].Text, blocks[index].Confidence, floor) ||
                EndsALine(blocks, index, floor))
            {
                kept?.Add(blocks[index]);
                continue;
            }

            kept ??= [.. blocks.Take(index)];
        }

        return kept ?? blocks;
    }

    // The one exception, and narrower than the one measured and declined above: a fragment that is
    // nothing but the symbols a line starts or ends with — ……！？） — sitting against a line that
    // was read with conviction. The recogniser is less sure of a run of punctuation than of words
    // (0.70-0.74 for ……！？） and ！？） cut off the end of user-6's line, against 0.93-0.97 for the
    // same symbols read inside the line), so the floor took exactly the part of the sentence the
    // user saw left behind untranslated and unerased. Scenery read as text is letters, digits and
    // stray glyphs (see ShortReadingDetection), and on its own row it still goes: the line beside
    // it is what says these symbols belong to a sentence. See Ocr.HorizontalLineEnds.
    private static bool EndsALine(List<OcrTextBlock> blocks, int index, double floor)
    {
        var fragment = blocks[index];
        if (!Ocr.HorizontalLineEnds.Enabled ||
            !Realtime.ShortReadingDetection.IsLineEdgeSymbols(fragment.Text)) return false;
        for (var other = 0; other < blocks.Count; other++)
        {
            if (other == index ||
                Realtime.ShortReadingDetection.IsUnconvincingShortText(blocks[other].Text, blocks[other].Confidence, floor))
                continue;
            if (Realtime.ShortReadingDetection.ContinuesLine(fragment.Bounds, blocks[other].Bounds))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Keeps the loaded model in memory while a continuous caller is running — see
    /// <see cref="OnnxOcrEngine.SetKeepWarm"/>.
    /// </summary>
    public void SetKeepWarm(bool keepWarm)
    {
        _engine.SetKeepWarm(keepWarm);
        _manga?.SetKeepWarm(keepWarm);
    }

    /// <summary>
    /// What a realtime session holds as it starts: the recogniser always; the manga models only when
    /// a block is vertical and they would read the language. Whichever of them is not in memory yet
    /// is loaded now rather than by the first frame with text on it.
    /// </summary>
    /// <returns>
    /// The loads, done when every one has ended, failed ones included; already done when there was
    /// nothing to load. For 載入模型中… on the bar.
    /// </returns>
    /// <remarks>
    /// Decided once for the session, not per block: every block reads with the same two engines.
    /// Not needed this time, the manga models are let go at once — a session keeps what it holds
    /// through pauses and framing until 結束, and 330MB of video memory held for blocks that no
    /// longer read with them is memory a game beside it could have used.
    /// </remarks>
    internal Task KeepWarmForRealtime(string sourceLanguage, bool vertical)
    {
        var loads = new List<Task>(2);

        _engine.SetKeepWarm(true);
        if (!_engine.IsLoaded(sourceLanguage))
        {
            Log.Info("Loading the OCR model ahead of a realtime session");
            loads.Add(_engine.PreloadAsync(sourceLanguage));
        }

        if (_manga is not null && vertical && ReadsVerticalWithMangaModels(sourceLanguage))
        {
            _manga.SetKeepWarm(true);
            if (!_manga.InMemory)
            {
                Log.Info("Loading the manga models ahead of a realtime session");
                loads.Add(_manga.PreloadAsync());
            }
        }
        else if (_manga is not null)
        {
            if (_manga.IsLoaded) Log.Info("Manga models not needed by this realtime session: released");
            _manga.ReleaseNow();
        }

        return Task.WhenAll(loads);
    }

    /// <summary>Whether either engine is loading its models right now; never waits for them.</summary>
    internal bool IsLoadingModels => _engine.IsLoading || _manga?.IsLoading == true;

    /// <summary>
    /// Releases the loaded model immediately rather than after the inactivity delay — see
    /// <see cref="OnnxOcrEngine.ReleaseNow"/>.
    /// </summary>
    public void ReleaseModel()
    {
        _engine.ReleaseNow();
        _manga?.ReleaseNow();
    }

    /// <summary>
    /// Whether a vertical page in this language would be read by the manga models right now.
    /// </summary>
    /// <remarks>
    /// For the realtime loop's confirmation pass. The column pipeline's answer moves with the detector
    /// size, which is what makes a second read at another size worth having (see
    /// <see cref="Ocr.VerticalSecondLook"/>); the manga detector always sees the page at 640×640, so
    /// reading the same frame again returns the same page.
    /// </remarks>
    internal bool ReadsVerticalWithMangaModels(string sourceLanguage) =>
        _manga is not null && ReadsWithMangaModels(sourceLanguage) && _manga.UnavailableReason is null;

    /// <summary>
    /// How many recognitions may run at once. Exposed so a caller that was turned away can say how
    /// many slots there were, which is the number that makes the refusal mean anything.
    /// </summary>
    public static int ConcurrentRecognitions => OnnxOcrEngine.ConcurrentRecognitions;

    public void Dispose()
    {
        _manga?.Dispose();
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
        // After the ink is measured, which has to read the picture inside the upright box.
        return TiltedLayout.Restore(OcrTextBlockGrouper.Group(TiltedLayout.Straighten(blocks), profile));
    }

    /// <summary>
    /// The same grouping for lines already read, on pixels already converted: what a vertical
    /// capture does with the lines it reads across — see <see cref="Ocr.VerticalColumnGrouping"/>.
    /// </summary>
    /// <remarks>
    /// The pixels rather than the bitmap because the column grouping has converted the page once
    /// already, and converting it again cost more than everything else this does: 4ms of a 4.2ms
    /// total on an 1820x1298 page holding three lines.
    /// </remarks>
    /// <param name="pixels">The capture, for the ink measurement; null skips it.</param>
    internal static List<OcrTextBlock> GroupScreenshot(
        SkiaSharp.SKBitmap? pixels, List<OcrTextBlock> blocks, GroupingProfile profile)
    {
        if (pixels is not null && MeasuresInk(profile))
            blocks = TextInkMetrics.Annotate(pixels, blocks);
        return TiltedLayout.Restore(OcrTextBlockGrouper.Group(TiltedLayout.Straighten(blocks), profile));
    }

    internal static List<OcrTextBlock> PrepareScreenshotGrouping(
        Bitmap bitmap, List<OcrTextBlock> blocks, GroupingProfile profile) =>
        MeasuresInk(profile)
            ? TextInkMetrics.Annotate(bitmap, blocks)
            : blocks;

    private static bool MeasuresInk(GroupingProfile profile) => profile.TakesProseEvidence;

    /// <summary>
    /// Detects columns in the original frame; only recognition crops change orientation.
    /// Screenshot and realtime share the same source-coordinate grouping.
    /// </summary>
    /// <remarks>
    /// <para>Japanese goes to the manga models (RT-DETR bubble and text detection, manga-ocr
    /// recognition, on the GPU) when they are downloaded and a hardware GPU can run them — see
    /// <see cref="MangaVerticalReader"/>. Everything else — another language, no models, no GPU, a
    /// load that failed — is read with the column pipeline, exactly as it was before they existed.</para>
    ///
    /// <para>manga-ocr reads Japanese only; a Chinese or Korean page stays on the columns.</para>
    /// </remarks>
    internal static async Task<List<OcrTextBlock>> RecognizeVerticalAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken,
        MangaOcrEngine? manga = null)
    {
        if (manga is not null && ReadsWithMangaModels(sourceLanguage))
        {
            var (outcome, read) = await MangaVerticalReader.ReadAsync(
                manga, bitmap, wait: true, engine, OcrLanguageRouter.Normalize(sourceLanguage),
                maxDetectSize: null, cancellationToken,
                ColumnDetector(engine, sourceLanguage), LineDetector(engine, sourceLanguage));
            if (outcome == MangaReadOutcome.Read) return read!;
        }

        return await ReadColumnsAsync(engine, bitmap, sourceLanguage, cancellationToken);
    }

    private static async Task<List<OcrTextBlock>> ReadColumnsAsync(
        IOcrEngine engine, Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken)
    {
        var blocks = await engine.RecognizeAsync(
            bitmap, sourceLanguage, cancellationToken, verticalText: true);
        return Ocr.VerticalColumnGrouping.Group(blocks, bitmap.Width, bitmap: bitmap, language: sourceLanguage);
    }

    private static bool ReadsWithMangaModels(string language) => OcrLanguageRouter.Normalize(language) == "JA";
}
