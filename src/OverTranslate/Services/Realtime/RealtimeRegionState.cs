using System.Drawing;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// Decides, poll by poll, whether one watched region is worth recognising again. Holding this apart
/// from the loop keeps the policy — the part with all the ways to be subtly wrong — testable without
/// a screen, a model or a network.
/// </summary>
/// <remarks>
/// The policy hangs on one observation: <b>a region is mostly not text.</b> Over a video or a game,
/// comparing the whole rectangle reports a change on every single poll, because the picture behind
/// the subtitle never stops moving. There is then nothing to wait for — no two frames are ever alike
/// — so a rule that waits for the picture to settle waits forever, and one that gives up waiting
/// after a fixed delay pays that delay on every line of dialogue.
///
/// So once a pass has found text, this stops watching the region and watches only the strips the
/// text was found in, padded enough to catch a new line appearing beside it. Background motion
/// outside those strips is then invisible, a subtitle changing shows up on the very next poll, and
/// the region falls quiet in between instead of being recognised on a timer.
///
/// The comparison itself is <see cref="FrameFingerprint"/> rather than an exact hash, because the
/// pixels inside those strips are not stable either — that difference alone took one measured region
/// from recognising continuously to recognising when its words changed. Text appearing somewhere
/// else entirely is what <see cref="FullRescanPolls"/> is for: nothing short of recognition can tell
/// it from background motion, so it is checked occasionally rather than never.
/// </remarks>
internal sealed class RealtimeRegionState
{
    /// <summary>
    /// How often a watched region is looked at. Every threshold below is expressed against it.
    /// </summary>
    /// <remarks>
    /// <para>This is a sampling rate and nothing more — how quickly a change can be NOTICED. What is
    /// then done about it is set by the intervals below, in milliseconds, so that moving this number
    /// changes latency and not how much recognition a session pays for. It used to be the other way
    /// round: the thresholds were poll counts, so halving the interval silently doubled the rate the
    /// region was scanned at and the rate it was re-examined at.</para>
    ///
    /// <para>150ms, down from 250ms. The floor is the capture backend's own readback throttle
    /// (<c>MaxFrameAge</c>, 120ms): polling faster than frames are read back means two polls in a
    /// row see the same pixels, which reads as "the picture has settled" and quietly disables the
    /// wait below. What the change buys is the one thing that was pure latency — a line that changes
    /// inside the watched strips is noticed up to 100ms sooner and confirmed 100ms sooner after
    /// that, so the worst case for a subtitle changing goes from 500ms to 300ms.</para>
    ///
    /// <para>What it costs is a grab and a fingerprint 1.67x as often. The grab is a crop out of the
    /// frame the backend already read back, not a capture; the fingerprint is measured at 0.2ms over
    /// a subtitle strip's text bands and 0.46ms over the whole strip. Recognition, which is the
    /// expensive thing, is unaffected — that is what expressing the thresholds in time buys.</para>
    /// </remarks>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// How long a region with no known text may keep changing before it is scanned anyway. This is
    /// the path a session starts on, and the one it returns to whenever the text goes away.
    /// </summary>
    /// <remarks>
    /// Short, and deliberately so. There is no way to tell "a line just appeared" from "the picture
    /// moved" without recognising, so over live content this is simply the rate at which the region
    /// is searched for text — and a line that shows for a second and a half has to be caught inside
    /// its own lifetime or it is missed entirely.
    ///
    /// This rate is held whether or not the region has been fruitless for a while. Easing off after
    /// a quiet spell would save real work, but it buys that saving with exactly the thing the
    /// feature exists to provide: the moment it eases off is the moment a line can slip through
    /// between scans, and the user cannot tell that from the feature simply not working. The regions
    /// people draw for this are subtitle-sized, so the work being saved was small to begin with.
    ///
    /// It is also the most expensive path there is — every scan is a recognition over the whole
    /// region, found text or not — which is why it is held at a time rather than at a poll count.
    /// Sampling faster must not search faster.
    /// </remarks>
    public static readonly TimeSpan SearchInterval = TimeSpan.FromMilliseconds(500);

    /// <inheritdoc cref="SearchInterval"/>
    public static readonly int MaxUnsettledPolls = PollsIn(SearchInterval) - 1;

    /// <summary>
    /// The same wait once the text strips are being watched. Far shorter, because a change here is
    /// the text itself changing rather than the picture behind it — one poll is enough to let a line
    /// that fades in arrive, and any longer is latency the reader pays for every subtitle.
    /// </summary>
    /// <remarks>
    /// The one threshold deliberately left in polls rather than moved to a time. It is not rationing
    /// anything — the work it gates happens once per line of dialogue either way, set by how often
    /// the words change and not by how often they are looked at — so all it does is wait. Sampling
    /// faster should therefore confirm faster, and this is where the poll interval is allowed to
    /// show up as latency saved: at 150ms it is a 150ms wait where it used to be 250ms.
    /// </remarks>
    public const int MaxTextUnsettledPolls = 1;

    /// <summary>
    /// How often the whole region is re-examined while the watched strips sit still, to catch text
    /// that appeared somewhere the last pass found none.
    /// </summary>
    /// <remarks>
    /// Every poll spent below this is a poll in which a line appearing outside the strips is
    /// invisible, and a line that comes and goes inside one such window is not late — it is missed.
    /// At three seconds that blind spot was longer than plenty of subtitles are on screen. One
    /// second costs more recognition over still content and buys back the case the strips cannot see
    /// by design: the second speaker's line appearing well away from the first.
    ///
    /// In time rather than in polls for the same reason as <see cref="SearchInterval"/>: a rescan is
    /// a recognition, and how often the region is sampled must not decide how often it is paid for.
    /// </remarks>
    public static readonly TimeSpan FullRescanInterval = TimeSpan.FromMilliseconds(1000);

    /// <inheritdoc cref="FullRescanInterval"/>
    public static readonly int FullRescanPolls = PollsIn(FullRescanInterval);

    /// <summary>
    /// How many passes in a row must find nothing before the overlay is cleared. Recognition drops a
    /// line it had a moment ago often enough — a frame caught mid-repaint, a compression artefact —
    /// and acting on the first one makes the translation blink out and come straight back, which
    /// reads far worse than a stale line lingering for one more poll.
    /// </summary>
    public const int EmptyPassesBeforeClearing = 2;

    // Rounded to the nearest whole poll and never below one, because these are counted in polls
    // wherever they are used and a threshold of zero would mean "every poll".
    private static int PollsIn(TimeSpan interval) =>
        Math.Max(1, (int)Math.Round(interval / PollInterval));

    private static readonly IReadOnlyList<Rectangle> NoBands = [];
    private static readonly IReadOnlyList<RenderedLine> NoLines = [];

    private IReadOnlyList<Rectangle> _watchBands = NoBands;
    private FrameFingerprint? _rendered;
    private FrameFingerprint? _renderedFull;
    private FrameFingerprint? _pending;
    private int _unsettledPolls;
    private int _pollsSinceFullScan;
    private int _emptyPasses;
    internal DialogueReadingTracker Dialogue { get; } = new();

    /// <summary>
    /// What the region shows, one entry per line, each with the score it was read at — so a later
    /// reading of one sentence can be judged against that same sentence rather than against the
    /// average of everything that happened to be in frame with it. See
    /// <see cref="RealtimeReadingMerge"/> for why that distinction is the whole of issue #30.
    /// </summary>
    public IReadOnlyList<RenderedLine> RenderedLines { get; private set; } = NoLines;

    /// <summary>The source text currently on screen for this region.</summary>
    public string RenderedText { get; private set; } = "";

    /// <summary>
    /// How well <see cref="RenderedText"/> was read as a whole — the per-line scores weighted by how
    /// much text each line contributes, so a long line read well is not outvoted by a stray
    /// two-character block beside it. Zero when nothing is shown.
    /// </summary>
    /// <remarks>
    /// Nothing decides anything by this any more; it is what the log reports so a session can still
    /// be read as "this pass scored better than what was up". The decisions are per line, against
    /// <see cref="RenderedLines"/>.
    /// </remarks>
    public double RenderedConfidence { get; private set; }

    /// <summary>True once a pass has found text and the state is watching its strips.</summary>
    public bool IsWatchingText => _watchBands.Count > 0;

    /// <summary>
    /// Whether enough passes have found nothing that the overlay really should be emptied — see
    /// <see cref="EmptyPassesBeforeClearing"/>.
    /// </summary>
    public bool ShouldClearOverlay => _emptyPasses >= EmptyPassesBeforeClearing;

    /// <param name="capture">
    /// Summarises the current frame over the given sub-rectangles, or the whole region when passed
    /// null. Taken as a delegate so this class never touches a bitmap, and so a test can drive the
    /// policy with fingerprints it builds by hand.
    /// </param>
    /// <returns>Whether the frame should be recognised now.</returns>
    public bool Observe(Func<IReadOnlyList<Rectangle>?, FrameFingerprint> capture, bool dialogue = false) =>
        Examine(capture, dialogue) != RealtimeReadReason.Nothing;

    /// <summary>The same decision, and what made it — see <see cref="RealtimeReadReason"/>.</summary>
    /// <remarks>
    /// The caller needs the reason because the three are not worth the same. A change inside the
    /// watched strips is known text changing, and there is nothing cheaper than recognition that
    /// could confirm it. The other two are asking "is there anything here at all", on a timer, and
    /// the answer is usually no — which is a question a much smaller detection can be asked first.
    /// </remarks>
    /// <inheritdoc cref="Observe" path="/param"/>
    public RealtimeReadReason Examine(
        Func<IReadOnlyList<Rectangle>?, FrameFingerprint> capture, bool dialogue = false)
    {
        if (dialogue && Dialogue.TryTakeConfirmation()) return RealtimeReadReason.TextChanged;
        var current = capture(IsWatchingText ? _watchBands : null);

        if (current.Differs(_rendered))
        {
            if (dialogue) Dialogue.ObservePixelChange();
            // Changed, and not yet the same twice running. Give it a poll to settle so a line that
            // is still fading in is read once it has arrived — but only up to the cap, or content
            // that never holds still would never be read at all.
            var cap = IsWatchingText ? (dialogue ? 0 : MaxTextUnsettledPolls) : MaxUnsettledPolls;
            if (current.Differs(_pending) && _unsettledPolls < cap)
            {
                _pending = current;
                _unsettledPolls++;
                return RealtimeReadReason.Nothing;
            }

            _pending = current;
            _unsettledPolls = 0;
            // With strips to compare against, a change in them is the text itself changing. Without
            // any, this is the search: the region is changing because the picture is, and whether
            // that includes a line of text is exactly what is not known.
            return IsWatchingText ? RealtimeReadReason.TextChanged : RealtimeReadReason.Search;
        }

        _pending = current;
        _unsettledPolls = 0;

        // Nothing known is being watched, and nothing changed — the idle path, and the one that has
        // to stay free: an untouched region costs a grab and a fingerprint, and nothing else.
        if (!IsWatchingText) return RealtimeReadReason.Nothing;

        // The text we know about is unchanged, but something may have appeared outside it, which no
        // view of the old lines can see.
        if (++_pollsSinceFullScan < FullRescanPolls) return RealtimeReadReason.Nothing;

        _pollsSinceFullScan = 0;
        bool changed = capture(null).Differs(_renderedFull);
        if (dialogue && changed) Dialogue.ObservePixelChange();
        return changed ? RealtimeReadReason.Rescan : RealtimeReadReason.Nothing;
    }

    /// <summary>
    /// Whether a box the gate found sits inside text this region is already watching.
    /// </summary>
    /// <remarks>
    /// Used by the rescan, whose whole question is whether something appeared where the strips
    /// cannot see. A box over the line already on screen answers "no" — that text is known, it has
    /// not changed (or the strip comparison would have said so), and recognising the region again
    /// for it would be the timer doing exactly what the strips exist to avoid.
    ///
    /// By majority overlap rather than containment: the gate detects at a third of the size, so its
    /// boxes land a few pixels off the ones a full pass would produce, and a box that is mostly over
    /// a known line is that line.
    /// </remarks>
    public bool IsInsideWatchedText(System.Windows.Rect box)
    {
        var area = box.Width * box.Height;
        if (area <= 0) return false;

        foreach (var band in _watchBands)
        {
            var overlapWidth = Math.Min(box.Right, band.Right) - Math.Max(box.Left, band.Left);
            var overlapHeight = Math.Min(box.Bottom, band.Bottom) - Math.Max(box.Top, band.Top);
            if (overlapWidth <= 0 || overlapHeight <= 0) continue;
            if (overlapWidth * overlapHeight * 2 >= area) return true;
        }

        return false;
    }

    /// <summary>
    /// Records what the region now shows. <paramref name="textBounds"/> are the recognised lines in
    /// region coordinates and become the strips watched from here on.
    /// </summary>
    /// <remarks>
    /// An empty <paramref name="textBounds"/> deliberately keeps the previous strips rather than
    /// falling straight back to watching the whole region: one pass finding nothing is far more
    /// often a bad frame than text that has really gone, and the strips are exactly where to look to
    /// find out. They are only given up once <see cref="EmptyPassesBeforeClearing"/> passes agree.
    /// </remarks>
    public void MarkRendered(
        IReadOnlyList<Rectangle> textBounds,
        Func<IReadOnlyList<Rectangle>?, FrameFingerprint> capture,
        string sourceText,
        double confidence = 0) =>
        MarkRendered(textBounds, capture, ToLines(sourceText, confidence));

    /// <summary>
    /// The same, told what each line says and how well each was read — which is what the pass itself
    /// knows and what <see cref="RealtimeReadingMerge"/> needs back on the next pass.
    /// </summary>
    public void MarkRendered(
        IReadOnlyList<Rectangle> textBounds,
        Func<IReadOnlyList<Rectangle>?, FrameFingerprint> capture,
        IReadOnlyList<RenderedLine> lines)
    {
        if (textBounds.Count > 0)
        {
            _emptyPasses = 0;
            _watchBands = BuildBands(textBounds);
        }
        else if (++_emptyPasses >= EmptyPassesBeforeClearing)
        {
            _watchBands = NoBands;
        }

        _renderedFull = capture(null);
        _rendered = IsWatchingText ? capture(_watchBands) : _renderedFull;
        _pending = _rendered;
        _unsettledPolls = 0;
        _pollsSinceFullScan = 0;
        RenderedLines = lines;
        RenderedText = string.Join('\n', lines.Select(line => line.Text));
        RenderedConfidence = WeightedConfidence(lines);
    }

    /// <summary>
    /// One score for a whole reading, weighted by how much text each line contributes.
    /// </summary>
    private static double WeightedConfidence(IReadOnlyList<RenderedLine> lines)
    {
        double weighted = 0;
        double weight = 0;

        foreach (var line in lines)
        {
            var characters = Math.Max(1, line.Text.Trim().Length);
            weighted += line.Confidence * characters;
            weight += characters;
        }

        return weight > 0 ? weighted / weight : 0;
    }

    /// <summary>
    /// Splits a whole reading back into lines scored alike, for callers that only have the joined
    /// text — the empty pass, which has no lines at all, and the tests.
    /// </summary>
    private static IReadOnlyList<RenderedLine> ToLines(string sourceText, double confidence) =>
        sourceText.Length == 0
            ? NoLines
            : [.. sourceText.Split('\n').Select(line => new RenderedLine(line, confidence))];

    /// <summary>
    /// Forgets what the region is known to show, so the next poll reads it again.
    /// </summary>
    /// <remarks>
    /// For a pass that was recorded as rendered but never reached the screen — a translation that
    /// failed or was dropped. Nothing else would retry it: the pixels have not changed, so every
    /// later poll would compare the region against a record of the very frame it is looking at and
    /// conclude, correctly and uselessly, that nothing has happened. The watched strips are kept:
    /// where the text is has not stopped being true just because translating it did not work.
    /// </remarks>
    public void Invalidate()
    {
        Dialogue.Reset();
        _rendered = null;
        _renderedFull = null;
        _pending = null;
        _unsettledPolls = 0;
        RenderedLines = NoLines;
        RenderedText = "";
        RenderedConfidence = 0;
    }

    /// <summary>
    /// Grows each recognised line into the strip to watch. The vertical padding is the generous one:
    /// the change most likely to be missed is a second line arriving directly under the first — a
    /// dialogue box filling in, a subtitle going from one line to two — and covering the gap either
    /// side means that shows up immediately instead of waiting for the next full rescan.
    /// </summary>
    private static IReadOnlyList<Rectangle> BuildBands(IReadOnlyList<Rectangle> textBounds)
    {
        var bands = new List<Rectangle>(textBounds.Count);
        foreach (var bounds in textBounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;

            int padX = 6;
            int padY = Math.Max(4, (int)Math.Round(bounds.Height * 0.75));
            bands.Add(Rectangle.FromLTRB(
                bounds.Left - padX, bounds.Top - padY, bounds.Right + padX, bounds.Bottom + padY));
        }

        // Every recognised line was degenerate — treat that as having found nothing to watch.
        return bands.Count > 0 ? bands : NoBands;
    }
}
