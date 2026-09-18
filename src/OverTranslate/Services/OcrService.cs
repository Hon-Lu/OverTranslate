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
    /// Which way the region's text is written. Vertical turns the frame 270° and reads it with the
    /// vertical pipeline — see <see cref="TryRecognizeVerticalAsync"/>; the mode does not reach that
    /// path, for the reason <see cref="GroupingProfile.Vertical"/> gives.
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
                bitmap, OcrLanguageRouter.Normalize(sourceLanguage), maxDetectSize, cancellationToken);
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

    /// <summary>
    /// The live-screen half of the vertical pipeline: the same 270° turn and the same grouping the
    /// screenshot path uses, asked for a free inference slot rather than queued for one.
    /// </summary>
    /// <remarks>
    /// <para>It shares <see cref="RecognizeVerticalAsync"/>'s procedure and deliberately not the
    /// live path's <see cref="GroupRealtime"/>. Both of that method's branches judge rows against
    /// rows of the original picture — the dialogue grouper asks which reading completes a line,
    /// the panel grouper which line continues a paragraph — and here a "row" is a whole column, so
    /// neither question is the one in front of it. <see cref="GroupingProfile.Vertical"/> is what
    /// the turned picture is measured on, and it is the only profile that has been.</para>
    ///
    /// <para>The two live-path filters that are not about rows do run. The scenery filter is text
    /// and confidence only, and the collapse filter is applied here rather than by the caller
    /// because here the picture is still turned: a collapse is one box thrown across the block
    /// perpendicular to the writing, which in the turned frame is the same box height test the
    /// horizontal path makes, and after the mapping back it would be a test of something else.</para>
    /// </remarks>
    private async Task<List<OcrTextBlock>?> TryRecognizeVerticalAsync(
        Bitmap bitmap,
        string language,
        int? maxDetectSize,
        CancellationToken cancellationToken)
    {
        using var rotated = new Bitmap(bitmap);
        rotated.RotateFlip(RotateFlipType.Rotate270FlipNone);

        var blocks = await _engine.TryRecognizeAsync(
            rotated, language, maxDetectSize, cancellationToken);
        if (blocks is null)
            return null;

        var filtered = RejectUnconvincingBlocks(blocks)
            .Where(block => !Realtime.CollapsedDetection.IsCollapsed(
                block.Bounds.Height, rotated.Height, block.Text))
            .ToList();

        return MapVerticalColumnsBack(
            OcrTextBlockGrouper.Group(filtered, GroupingProfile.Vertical), bitmap.Width);
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
    /// Turns vertical writing anticlockwise for the horizontal detector, then maps the grouped
    /// results back to the original image. The rightmost source column becomes the first detected
    /// row, preserving Japanese reading order.
    /// </summary>
    /// <remarks>
    /// <para>Takes no profile, and that absence is the contract. Neither pass here is judging what
    /// the capture modes were measured on: the column merge compares column against column, and so
    /// — once the picture has been turned 270° — does the first pass, because every column of the
    /// original reaches the detector as a row. Handing either of them a relaxed threshold would be
    /// relaxing something nobody has measured, and the measurement says what that buys: the relaxed
    /// profile joined balloons rather than the lines inside them.</para>
    ///
    /// <para>Both passes therefore run on <see cref="GroupingProfile.Vertical"/>, which holds the
    /// conservative figures under its own name so that tightening the interface mode later cannot
    /// move vertical text with it.</para>
    /// </remarks>
    internal static async Task<List<OcrTextBlock>> RecognizeVerticalAsync(
        IOcrEngine engine,
        Bitmap bitmap,
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        using var rotated = new Bitmap(bitmap);
        rotated.RotateFlip(RotateFlipType.Rotate270FlipNone);

        var blocks = await RecognizeAndGroupAsync(
            engine, rotated, sourceLanguage, GroupingProfile.Vertical, cancellationToken);

        return MapVerticalColumnsBack(blocks, bitmap.Width);
    }

    /// <summary>
    /// Turns grouped rows of the rotated picture back into columns of the original, then reassembles
    /// the ones belonging to the same piece of writing.
    /// </summary>
    /// <remarks>
    /// Shared by the screenshot and live-screen entry points so there is one description of what a
    /// vertical reading is. What differs between them is how the recognition was asked for, which is
    /// settled before this runs.
    /// </remarks>
    internal static List<OcrTextBlock> MapVerticalColumnsBack(
        List<OcrTextBlock> blocks, int originalWidth)
    {
        var columns = blocks.Select(block => block with
        {
            Bounds = MapVerticalBoundsBack(block.Bounds, originalWidth),
            // The layout box turns with the picture. Without it the second pass below would be
            // reading a rectangle still in the rotated frame beside one that is not.
            LayoutBounds = MapVerticalBoundsBack(block.LayoutBounds, originalWidth),
            SourceLineBounds = null,
            // After mapping back, a column is tall and narrow. The rotated row height is the
            // original glyph width and is the useful reference for a square vertical cell. Only the
            // render metric: what the columns are grouped on is LayoutBounds, above.
            RenderGlyphHeight = block.Bounds.Height,
        }).ToList();

        return MergeVerticalColumns(columns);
    }

    internal static Rect MapVerticalBoundsBack(Rect rotated, int originalWidth) => new(
        originalWidth - (rotated.Y + rotated.Height),
        rotated.X,
        rotated.Height,
        rotated.Width);

    /// <summary>
    /// Reassembles adjacent right-to-left columns that share a top edge. A lone column is split
    /// into character cells so overlay layout still receives a usable vertical footprint.
    /// </summary>
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

    private static bool IsSameVerticalTextGroup(OcrTextBlock a, OcrTextBlock b)
    {
        double columnWidth = Math.Max(a.LayoutBounds.Width, b.LayoutBounds.Width);
        if (Math.Abs(a.LayoutBounds.Y - b.LayoutBounds.Y) > columnWidth * 0.6)
            return false;

        double gap = Math.Max(a.LayoutBounds.Left, b.LayoutBounds.Left) -
                     Math.Min(a.LayoutBounds.Right, b.LayoutBounds.Right);
        return gap <= columnWidth * 0.6;
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
    /// How big one cell of vertical writing is, taken from the area each character occupies rather
    /// than from how wide the detector drew the column.
    /// </summary>
    /// <remarks>
    /// <para>MEASURED. This used to be the median of the column widths, which is right whenever a
    /// detection box holds exactly one column — and silently doubles when one holds two. On the
    /// comic page at <c>.ai/test-images/vertical-image-ja/genshin-4koma-column-merge.png</c>,
    /// fourteen of the fifteen columns came back 22–26px wide with 21px of length per character,
    /// and the fifteenth was a single box thrown across two of them: 46px wide, 252px long, 22
    /// characters, 11.5px of length per character. The median of that group's two widths is the 46, so the balloon was drawn at twice
    /// the size of the text it replaced — the complaint this fixes, and it is on the screenshot path
    /// as much as the live one.</para>
    ///
    /// <para>Neither figure alone survives that box: its width is twice the truth and its length per
    /// character is half of it. Their product is not, and that is the whole of the rule. Vertical CJK
    /// sets on a square grid, so a column of <c>n</c> characters covers <c>width × length = n × g²</c>
    /// whatever the box did, and <c>g = sqrt(width × length / n)</c> falls out. Over the fifteen
    /// columns above it returns 22.6, 23.1, 24.6, 23.7 … for the good ones and <b>23.0</b> for the
    /// doubled one — the same answer, from a box that was wrong in both directions.</para>
    ///
    /// <para>The width is still consulted, as a ceiling. The area rule has its own failure — a box
    /// far longer than the few characters read out of it, which is what a stray mark or a dropped
    /// reading looks like — and there the width is the sober number. Taking the smaller of the two
    /// means each covers the other's failure, and it costs nothing on real columns: over those
    /// fifteen the area figure was already below the width every time, because a detection box
    /// carries a pixel or two of air on each side. The median across columns sits on top of both,
    /// so one badly read column cannot carry the group.</para>
    ///
    /// <para>Latin down the spine of a book is not on a square grid, and this returns something too
    /// small for it. That is already the assumption everywhere else: both overlays lay vertical text
    /// out in square cells (<see cref="Layout.VerticalTextGrid"/>), so a Latin column was never going
    /// to be drawn as one anyway — what changes here is only which of two wrong numbers it gets, and
    /// this one at least cannot double.</para>
    /// </remarks>
    private static double VerticalGlyphSize(List<OcrTextBlock> columns)
    {
        var sizes = columns
            .Select(column =>
            {
                var characters = column.Text.Count(character => !char.IsWhiteSpace(character));

                // Nothing read out of it: the box is all there is to go on.
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
