namespace OverTranslate.Services.Realtime;

/// <summary>
/// What size to hand the text detector when reading a watched region.
/// </summary>
/// <remarks>
/// The detector has a size of text it is good at, and subtitles are well outside it. Measured on two
/// captured frames of a 1226x196 subtitle strip, sweeping the detector's input size in steps of 64:
///
/// <list type="bullet">
/// <item>at 0.37–0.78 of native, both frames read correctly every time;</item>
/// <item>at 0.84 and above, both collapsed — the detector returned one box spanning the whole strip
/// (measured at 201 and 256 pixels tall against a real line height of 88–117), and recognising that
/// stretched crop produced either nothing at all or "Mainasm ar you lay" for "Marina-san, are you
/// okay?".</item>
/// </list>
///
/// The glyphs in those frames are around 60px tall; halving puts them near 30px, which is where that
/// model was trained. That was why a subtitle strip was read at half scale — but only a strip: an
/// interface panel holds much smaller text and needs the opposite correction. That last part still
/// holds, and which of the two a block is comes from <see cref="RealtimeBlockMode"/>, i.e. from the
/// user; see <see cref="StripFraction"/> and <see cref="PanelFraction"/>. The halving itself is
/// gone — see the closing paragraph.
///
/// None of this reaches the screenshot flow, which asks for no downscale at all and is right to:
/// its text is interface-sized, already inside the detector's range, and downscaling it would
/// destroy the detail that <c>ImgResize = 2048</c> exists to keep.
///
/// EVERYTHING ABOVE WAS MEASURED ON PP-OCRv5_mobile_det, WHICH IS NO LONGER THE DETECTOR. The
/// collapse it describes was real, and the reason for it — a size the model dislikes returns one
/// box across the whole strip — was the diagnosis issue #22 spent three rounds confirming. What
/// changed is the model: swept over 15 frames a live session had failed to read, PP-OCRv5 read 8
/// at 0.40, 4 at 0.50, 6 at 0.55 and 8 at 0.60, while PP-OCRv6_det_tiny reads 9 across that whole
/// band and 9 at native. There is no dead band left to steer around.
///
/// The fractions did not change when the model did, and for a while the reason given for keeping
/// them was cost: a strip read at 0.50 and at native read the same 9 of those 15 frames, for 89ms
/// against 186ms, so halving looked like the same answer for half the work. That reasoning was
/// unsound, and this type's own remarks said why at the time — every one of those 15 frames was one
/// the primary size had FAILED to read, and a fraction cannot be judged on those, because it is
/// never asked about the frames it reads WRONGLY rather than not at all. The note ended: "a sweep on
/// the control group (frames the primary size already read) is what would move them."
///
/// That sweep is issue #81, and it moved them. On 109 frames the primary size did read, 0.50 got 78%
/// of them right and 0.85 got 94%, so a fifth of what halving read, it read wrong — silently, since
/// the fallback chain only runs when a read finds NOTHING and a wrong answer is still an answer.
/// <see cref="StripFraction"/> carries the full table and what the losses looked like.
///
/// Two things there are worth keeping in mind before touching these numbers again. Native is not the
/// answer either — 1.00 reads worse than 0.85, so both directions away from the detector's preferred
/// scale cost accuracy, which is why these are fractions at all rather than "no downscale, like the
/// screenshot flow". And the whole corpus is regions about 1820 wide, so fraction and absolute size
/// cannot be told apart in it.
///
/// Issue #89 broke that tie by cropping whole screens at five margins and sweeping every scale on
/// each. Two candidates died there and stay dead: absolute detector size predicts nothing
/// (non-monotone, 15 points end to end) and neither does how much of the region the text occupies.
///
/// What survived is that TWO things degrade a read and they compound — how many pixels tall the
/// glyph is once resized (region glyph height x the actual downscale), and how hard that downscale
/// was. The second round separated them by shrinking the source images to 70/50/35%, which makes
/// the glyphs genuinely small without heavily resampling anything, and graded every set against the
/// full-size reading of the same text:
///
/// <code>
///   glyph px after resize:      0–12   12–16   16–20   20–25
///   full-size sources            37%     89%     99%     98%
///   sources shrunk to 35%        76%     91%     91%    100%
/// </code>
///
/// Same glyph pixels, opposite outcomes. Small text read at a mild downscale is fine; big text
/// crushed to the same pixel height is not. So neither number is a rule on its own, and in
/// particular there is no floor to enforce: a scale rule derived from glyph height alone would
/// spend its budget raising the detector size for small text, which is the case that was already
/// reading well. Issue #89 was closed on this rather than carried into an implementation.
///
/// EVERY TABLE ABOVE WAS MEASURED WHILE THE FRACTION WAS NOT WHAT THE DETECTOR GOT. Until the
/// geometry fix (<c>OnnxOcrEngine.CreateDetectorFrame</c>) the size asked for here was a request the
/// library rounded down twice, by a different amount on each axis: a strip asked for at 0.85 reached
/// the detector at 0.84 across and 0.73 down. So "1.00 reads worse than 0.85" compared two distorted
/// readings, and the row labelled 1.00 was never a native read.
///
/// The fractions themselves survive it. Re-swept on region-subtitle-mixed-boxshape (123 frames, the
/// corpus whose distortion ranged widest — 1.15x to 1.34x) with the geometry exact, at 0.85x, 1.0x
/// and 1.15x of what this type returns: 97.7%, 99.8% and 98.5% character F1. 1.0x — that is, leaving
/// <see cref="StripFraction"/> at 0.85 — is the peak with both neighbours worse, so there is nothing
/// to re-tune here and no reason to spend another sweep on it. What did change is that the fraction
/// now means the same thing whatever size the user's box happens to be.
///
/// Three stories were tried and did not survive, which is why this note is longer than its
/// conclusion: "the model wants ~30px" is PP-OCRv5 folklore; "bigger keeps helping, saturating at
/// 40–50px" is what the strictest success criterion alone shows on one corpus, and the same data
/// turns down past 40 once it is loosened; and "glyph height is THE predictor, with a floor near
/// 15px" — written here after the first round — held only while every sample came from one source
/// scale. See OcrHarness --margin-scale-grid, and grade across source scales, not within one.
/// </remarks>
internal static class RealtimeDetectorSize
{
    /// <summary>
    /// Below this, a region is read at native size with no fallbacks at all.
    /// </summary>
    /// <remarks>
    /// Downscaling only helps text the detector finds too large. A region this small cannot hold
    /// 60px subtitles and much text besides, so anything in it is already at or below the scale the
    /// detector wants, and shrinking it further could only take it out of range from the other side
    /// — which is also why there is nothing to fall back TO here, and why
    /// <see cref="WhileNothingIsShown"/> has nothing to save.
    ///
    /// Was named HalfScaleMinSide, from the era when the downscale was a halving. The 800 itself is
    /// unchanged and predates PP-OCRv6: it has never been re-measured against this detector, and a
    /// sweep over small regions — small subtitles, small dialogue boxes, small tooltips — is what
    /// would move it. Renaming it does not make it measured.
    /// </remarks>
    public const int DownscaleMinSide = 800;

    /// <summary>
    /// Fraction to read a wide, short block at — a subtitle strip.
    /// </summary>
    /// <remarks>
    /// <para>Was 0.5, on the reasoning that a strip's glyphs run around 60px and halving lands them
    /// near the 30px the model was trained on. The number survived the move to PP-OCRv6 because the
    /// sweep that checked it only ever looked at frames the primary size had FAILED to read — and a
    /// fraction cannot be judged on those, because it is never asked about the frames it reads
    /// wrongly rather than not at all. This type's own remarks said as much: "a sweep on the control
    /// group (frames the primary size already read) is what would move them."</para>
    ///
    /// <para>That sweep, on the 109 primaryok frames of the corpus, comparing each size against what
    /// the other sizes agreed the frame said:</para>
    ///
    /// <code>
    ///   fraction   reads correctly   detector size   detection
    ///     0.40        75/109  69%         736             63ms
    ///     0.50        85/109  78%         928             79ms
    ///     0.60        93/109  85%        1120            116ms
    ///     0.68        94/109  86%        1248            122ms
    ///     0.75        96/109  88%        1376            146ms
    ///     0.85       102/109  94%        1568            163ms
    ///     1.00        94/109  86%        1825            189ms
    /// </code>
    ///
    /// <para>So a fifth of what the old size read, it read wrong — silently, because the fallback
    /// chain only runs when a read finds nothing, and a wrong answer is still an answer. What it
    /// lost was whole words: "Yay!" as "ay!", "I heard a bunch of experts" as "heard a bunch of
    /// experts", "エピソードトーク強すぎ" as "エピンドク強すぎ", and the line that opened issue #81,
    /// "…for a new song are…" as "…for a new so!g are…", cut mid-word and the cut recognised as
    /// punctuation. Those two dropped leading words are also the symptom issue #69 was closed over
    /// as a limitation of the detector; it was the size all along.</para>
    ///
    /// <para>Native is not the answer either, and that is the useful part: 1.00 reads worse than
    /// 0.85. There is a scale the detector wants and both directions away from it cost accuracy,
    /// which is why this is a fraction at all rather than "no downscale, like the screenshot flow".
    /// PaddleOCR's own guidance is the same shape — a limit on the longest side, 960 by default and
    /// 1216 when a larger detection scale is wanted, in multiples of 32. Nothing there says half.</para>
    ///
    /// <para>The cost is real: detection is about 85% of a pass, so this roughly doubles it, 79ms to
    /// 163ms measured on the same frames. It is paid for accuracy on the one thing a subtitle
    /// overlay does.</para>
    ///
    /// <para>What this number does, measured in #89: a fraction sets both of the things that decide a
    /// read, because it sets the glyph's height after resizing (region glyph height x fraction, the
    /// block's size cancelling out) and IS the downscale. On this corpus, around 55px glyphs, 0.85
    /// leaves 47px after a gentle resize and 0.50 leaves 27px after a harsh one — which is #81's 94%
    /// against 78%, arrived at from the other side.</para>
    ///
    /// <para>An earlier version of this note said the glyph height alone was the rule, with a floor
    /// near 15px, and drew the obvious conclusion: derive the scale from the measured glyph height
    /// and any content would clear the floor. Both halves were wrong, and #89's second round is why.
    /// Shrinking the source images makes glyphs genuinely small without a harsh resize, and there the
    /// floor simply is not there — 0–12px reads 76% on sources shrunk to 35% against 37% at full
    /// size. The rule would therefore have spent detector time enlarging small text, which is the
    /// case that was already fine, and it had no way to help the case that is not.</para>
    ///
    /// <para>So the honest bound on this number is that it is tuned for THIS content, and what would
    /// move it is a corpus where subtitles are read badly today — not a formula. #39 is where an
    /// earlier attempt at a derived scale was reverted, for reasons about cropping to the previous
    /// frame rather than about the scale rule.</para>
    /// </remarks>
    public const double StripFraction = 0.85;

    /// <summary>
    /// Fraction to read a chunkier block at — an interface panel, a tooltip, a dialogue box.
    /// </summary>
    /// <remarks>
    /// The opposite problem. Text is a small part of a panel's height, so its glyphs are already
    /// near interface size and halving takes them below what the detector can find. Measured on a
    /// 1380x750 grab of a game screen carrying a nine-box item tooltip:
    ///
    /// <code>
    ///   0.5  → 704   221ms   3 of 9 boxes
    ///   0.68 → 960   405ms   7 of 9 boxes
    ///   1.0  → 1380  724ms   9 of 9 boxes
    /// </code>
    ///
    /// Reading four more boxes is not a refinement — a box that is never detected is never drawn
    /// over either, so a partial read shows on screen as a card with the original language still
    /// visible between the translated lines.
    ///
    /// 0.68 is also what the fallback sweep already favoured: it read 8 of the 9 frames the shipped
    /// pair had given up on, more than any other fraction. It was only ever reached as a fallback,
    /// and a fallback runs only when the primary reads <em>nothing</em> — so a primary that read a
    /// little never escalated and the rest was silently lost.
    /// </remarks>
    public const double PanelFraction = 0.68;

    /// <summary>
    /// Fractions of the block to fall back to, in the order to try them, when the first size reads
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// The detector does not respond to scale smoothly, so there is no one size to get right.
    /// Sweeping one frame that reliably failed: 0.45 read it perfectly, 0.52 read nothing, 0.59
    /// read a fragment, 0.68 read it perfectly, 1.0 collapsed. Neighbouring sizes land on opposite
    /// sides of working.
    ///
    /// Which fractions, from 280 frames the loop had given up on, 9 of which held readable text:
    ///
    /// <code>
    ///   0.68 read 8 of 9      0.45 read 5      1.00 read 3
    ///   0.35 read 7 of 9      0.52 read 3
    /// </code>
    ///
    /// Those frames were kept precisely because the shipped pair failed on them, so the sample
    /// cannot say 0.68 is a better first choice than 0.52 — it never saw the frames 0.52 reads
    /// happily. What it does say is that neighbouring fractions land on opposite sides of working,
    /// which is why the mode above picks a starting point rather than one size trying to serve
    /// everything.
    ///
    /// The fallbacks are still earning their place under PP-OCRv6_det_tiny, on a smaller margin
    /// and for a different reason. Of 84 control frames the old detector read at the primary size,
    /// the new one reads 82 there and the other two only from 0.70 up — so those two are read by
    /// the first fallback rather than lost, at the price of one extra inference. What changed is
    /// how often that happens: on the frames a live session had to fall back for, the primary size
    /// now reads 9 of 15 rather than 4, which is the trigger rate coming down. Rarely used is what
    /// a fallback is supposed to be.
    ///
    /// The first fallback is whichever mode's fraction was not chosen: the two fail on opposite
    /// content, so the one the mode turned down is the best answer for the case where the mode was
    /// not the whole story — a subtitle block that also caught a line of interface text, say. Native
    /// goes last as the only size that can read text too small to survive any downscale — it
    /// recovered nine lines in a single measured session.
    ///
    /// That coupling has a cost worth knowing about: raising <see cref="StripFraction"/> for #81
    /// silently moved a PANEL's first fallback from 0.50 to 0.85, and #81 measured nothing but
    /// subtitles. The worry was specific — 0.68 and 0.85 sit on the same side of the primary, so the
    /// escape route DOWNWARDS disappeared, and <see cref="PanelFraction"/> records that 0.5 read
    /// only 3 of 9 boxes on the shape it was measured on. If some panel could be read at 0.5 and
    /// nowhere else, nothing would reach it any more.
    ///
    /// Swept for #84 over the panel corpus — 18 Japanese/English screens plus 9 Korean ones, the
    /// latter cut to 6 after dropping screens whose text never exceeds the harness's 10-character
    /// noise floor at any size — no such panel exists in it, and the question turns out to be moot
    /// from one step earlier:
    ///
    /// <code>
    ///   primary (0.68) read nothing, so a fallback ran   0 of 24
    ///   0.50 read materially more than 0.85              0 of 24
    /// </code>
    ///
    /// One Korean screen looked like the case being hunted (0.50 → 72 characters, 0.85 → 58) until
    /// its whole sweep was read across: 56–82 characters with no trend, 82 at 0.40 and 56 at 0.55.
    /// That is jitter with scale, not a fraction the content prefers.
    ///
    /// So the chain is unharmed, but note what the sweep actually says — on this corpus a panel's
    /// fallbacks never run at all, which means their value here is unmeasured rather than confirmed.
    /// What would move this is a panel the primary size fails on, and the corpus has none.
    /// </remarks>
    public const double NativeFraction = 1.0;

    /// <summary>
    /// A region of columns is read at the size the screenshot flow reads one, and none of the
    /// fractions above apply to it.
    /// </summary>
    /// <remarks>
    /// <para>Every table in this type was measured on writing that runs ACROSS. A row of glyphs
    /// resized down stays a row of glyphs; a page of columns does not. Columns of Japanese sit about
    /// a glyph apart, so at 0.68 a 25px column and the 12px reading beside it land 17px and 8px
    /// apart on the detector's own probability map, and what comes back is one box over both of
    /// them — or over the next column as well. Recognition is then handed a crop with two columns
    /// side by side in a strip 48 pixels tall, and loses most of what is in it.</para>
    ///
    /// <para>MEASURED against what the screenshot flow reads on the same pages, counting the
    /// screenshot's groups that the live read does not cover:</para>
    ///
    /// <code>
    ///   detector size     ja2, 15 spreads   the same at 70%   vertical-image-ja, 9
    ///   0.68 (PanelFraction)   17 missing        25 missing         11 missing
    ///   0.80                   12                20                 10
    ///   0.85                   12                18                  7
    ///   0.90                   13                14                  7
    ///   native                  4                 5                  5
    /// </code>
    ///
    /// The direction is the opposite of <see cref="StripFraction"/>'s and there is no peak in the
    /// middle to find: the fractions between buy almost nothing and the reading only comes right at
    /// the top. Capping at the screenshot size costs one group of the 88 on the second corpus,
    /// where one source is 8598px on its longest side, and bounds what an oversized region can
    /// spend — 2.8s against 27.6s on that one page.
    ///
    /// A FLOOR WAS PUT UNDER THE CAP AND THEN MEASURED AWAY. The argument for it was that a flat
    /// cap at 2048 stops being a cap once the region passes about 3000px on its longest side — it
    /// becomes a HARDER downscale than <see cref="PanelFraction"/> asks for, so a region off a 4K
    /// screen would be read at 0.53 where a smaller one is read at 0.68. That is a statement about
    /// ratios, and the table above is about accuracy; nothing had been read at both sizes to say
    /// which way it actually went.
    ///
    /// Read at both sizes, it goes the other way. On the three pages in the corpora wider or taller
    /// than 2048 the cap alone is the better of the two, and the floor is neutral at best: a 4K
    /// crop of a comic page reads 143 characters at 2048 against 139 at 2611, with ウワ！汚ったねぇ
    /// intact at the cap and 汚ったねえ without it; a 2590x4096 four-panel strip reads
    /// 朝は4本足昼は2本足は３本足で歩むモノ as one sentence at 2048 and in two broken pieces at
    /// 2816; a 2297x3123 page is a wash. The 8598px source the cap was introduced for is worse
    /// again without it — 19 fragmented groups against 14 whole sentences. So the vertical branch
    /// is now exactly the screenshot flow's rule, which is also the simplest thing it could be.
    ///
    /// COST, on the pages this is for: a whole two-page spread goes from about 1.0s to 1.8s per
    /// pass, which is the same work the screenshot flow already does on the same picture. Vertical
    /// reading is a comic-reading feature and a comic page is looked at for seconds at a time, so
    /// the trade is a poor one only if the reader is scrolling faster than they can read.
    ///
    /// NO FALLBACKS. The chain below exists because text can be out of the detector's range in two
    /// directions and they need opposite sizes. Here only one direction exists — everything smaller
    /// was measured and is strictly worse — so a second inference on an empty region would be
    /// paying the most expensive size twice to ask a question already answered.
    /// </remarks>
    public static int ColumnsAreReadAtTheScreenshotSize => Ocr.OnnxOcrEngine.ScreenshotDetectSize;

    /// <param name="mode">
    /// What the user says the block holds. This used to be guessed from the block's width-to-height
    /// ratio, which is a fact about how the user dragged rather than about what they are reading —
    /// see <see cref="RealtimeBlockMode"/> for what that cost. Issue #35 replaced the guess with
    /// the question.
    /// </param>
    /// <param name="primary">Detector input size to read with first.</param>
    /// <param name="fallbacks">
    /// Sizes to try, in order, when a read finds nothing at all — stopping at the first that reads
    /// something. Empty is the signal because every way of being out of the detector's range
    /// produces it: text too large collapses into one unreadable box, text too small is never
    /// detected, and a scale the model simply dislikes returns nothing.
    /// </param>
    /// <param name="orientation">
    /// Which way the region's text is written. Every fraction in this type was measured on writing
    /// that runs ACROSS — subtitle strips and interface panels — and columns do not behave like it;
    /// see <see cref="ColumnsAreReadAtTheScreenshotSize"/>.
    /// </param>
    public static (int Primary, IReadOnlyList<int> Fallbacks) For(
        int width, int height, RealtimeBlockMode mode,
        RealtimeTextOrientation orientation = RealtimeTextOrientation.Horizontal)
    {
        var native = Math.Max(width, height);

        // ImgResize only ever downscales, so passing the longest side asks for the image as it is.
        if (native < DownscaleMinSide)
            return (native, []);

        if (orientation == RealtimeTextOrientation.Vertical)
            return (Math.Min(native, Ocr.OnnxOcrEngine.ScreenshotDetectSize), []);

        // The mode decides where to start; the other mode's fraction is then the first thing to try
        // if that start read nothing.
        var isStrip = mode == RealtimeBlockMode.Subtitle;
        var chosen = isStrip ? StripFraction : PanelFraction;
        var other = isStrip ? PanelFraction : StripFraction;

        var primary = RoundToStride((int)(native * chosen));
        var fallbacks = new[] { other, NativeFraction }
            .Select(fraction => fraction >= 1.0 ? native : RoundToStride((int)(native * fraction)))
            .Where(size => size != primary)
            .Distinct()
            .ToList();

        return (primary, fallbacks);
    }

    /// <summary>
    /// The fallbacks worth paying for while the region is known to be showing nothing at all.
    /// </summary>
    /// <remarks>
    /// Native size is the most expensive inference of the three and the least productive one here.
    /// Sorted by the state the region was in, the 163 rescues across four days of logs fall out as:
    ///
    /// <code>
    ///   the pass before had text     121   74%
    ///   the overlay still showed one  19   12%
    ///   the region had been cleared   23   14%
    /// </code>
    ///
    /// That last 14% is a new line arriving after a quiet stretch — the one nobody can afford to
    /// miss — so the fallbacks are not dropped there. But within it, the other mode's fraction
    /// rescued 17 of the 23 and native only 6, while native costs 190–220ms against the
    /// primary's ~90ms. Dropping it while the region is blank saves about a sixth of a subtitle
    /// session's whole recognition time for about 3.7% of its rescues.
    ///
    /// Only while blank. A region that has text on it, or had text a moment ago, keeps every size:
    /// that is where 86% of the rescues happen and where a missed line is a line the reader was in
    /// the middle of following.
    /// </remarks>
    public static IReadOnlyList<int> WhileNothingIsShown(
        IReadOnlyList<int> fallbacks, int width, int height)
    {
        var native = Math.Max(width, height);
        return [.. fallbacks.Where(size => size != native)];
    }

    // The detector works on a grid; sizes off it are rounded up internally anyway, and rounding
    // here keeps the number in the log the same as the one the model actually used.
    internal static int RoundToStride(int size) => Math.Max(320, (size + 31) / 32 * 32);
}
