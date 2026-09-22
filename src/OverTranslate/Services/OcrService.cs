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
        return blocks is null
            ? null
            : Ocr.VerticalColumnGrouping.Group(
                blocks, bitmap.Width, realtime: true, bitmap: bitmap);
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
        return Ocr.VerticalColumnGrouping.Group(blocks, bitmap.Width, bitmap: bitmap);
    }
}
