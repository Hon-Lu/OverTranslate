using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using NLog;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime.Capture;

namespace OverTranslate.Services.Realtime;

/// <summary>
/// The continuous half of realtime translation: watches each region on its own loop and pays for
/// recognition and translation only when what it is watching has actually changed.
/// </summary>
/// <remarks>
/// This is a monitoring loop that may run for hours next to a game, so every stage exists to avoid
/// work rather than to do it:
/// <list type="bullet">
/// <item>a poll grabs a small rectangle and summarises it — an idle region costs only that;</item>
/// <item><see cref="RealtimeRegionState"/> decides when a changed frame is worth reading, so text
/// that is fading in or scrolling is recognised once rather than at every intermediate frame,
/// without ever waiting forever for a still frame that moving content will never produce;</item>
/// <item>recognised text that says the same thing as what is already on screen ends the pass without
/// a network call — see <see cref="TextSimilarity"/>, which is what keeps recognition's own jitter
/// from being mistaken for the words having changed;</item>
/// <item>a per-session cache means a line of dialogue that comes back (a repeated subtitle, a menu
/// the user reopens) is never translated twice.</item>
/// </list>
///
/// Each region runs on its own loop, no loop ever queues for the recogniser — a busy engine means
/// this poll is skipped, not that this region waits — and no loop waits for a translation it asked
/// for (see <see cref="RegionTranslationPump"/>). All three follow from what "realtime" costs when
/// it is not true: a shared loop makes every region wait out the slowest one, a queued pass answers
/// a frame that has already been replaced while delaying the frame that replaced it, and a loop
/// waiting on the network is a loop not watching the screen. The screenshot flow makes the opposite
/// trade, because its user is waiting for that one result and it will not come round again.
///
/// The OCR and translation services are handed in rather than created: they are the same instances
/// the screenshot flow uses, so the two features share one loaded ONNX runtime and one bounded pool
/// of inference slots instead of competing for the CPU with a second copy of each.
/// </remarks>
public sealed class RealtimeTranslationSession
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Fast enough that a subtitle appears to update as it changes, slow enough that the grab+hash
    // of a few small regions stays invisible in Task Manager. Owned by RealtimeRegionState, which
    // expresses every threshold that decides RECOGNITION against it — so this number sets how
    // quickly a change is noticed and nothing else. See the note on PollInterval there.
    private static readonly TimeSpan PollInterval = RealtimeRegionState.PollInterval;

    // Bounded so a long session on scrolling content cannot grow the cache without limit. Cleared
    // wholesale rather than evicted one by one: at this size the loss is one extra translation for
    // lines that are still on screen, and an LRU here would cost more to maintain than it saves.
    private const int TranslationCacheLimit = 400;

    // How many translations may be in flight across the whole session. More than one because a slow
    // answer must not delay the line after it, and only a few because a provider that has stopped
    // answering would otherwise accumulate work for as long as the session runs.
    //
    // Session-wide rather than per region: the free endpoints answer one account, and three regions
    // each allowed three of their own would put nine requests in flight at once — which is where a
    // provider starts rate-limiting, and a rate-limited pass fails, retries, and adds more load.
    private const int MaxConcurrentTranslations = 4;

    private readonly OcrService _ocr;
    private readonly TranslationService _translation;

    // Shared by every region, and generation-tagged so a provider answer from before a pause cannot
    // repopulate the cache after that pause cleared it.
    private readonly RealtimeTranslationCache _translationCache = new();

    // Shared by every region's pump, for the reasons on MaxConcurrentTranslations.
    private readonly SemaphoreSlim _translationSlots =
        new(MaxConcurrentTranslations, MaxConcurrentTranslations);

    // The Failed event promises at most one notification per distinct message, and a session-wide
    // failure — a missing API key, a provider that is down — reaches every region at once. Held
    // here rather than per region so three regions report it once between them, not three times.
    private readonly object _failureGate = new();
    private string? _lastReportedFailure;
    private CancellationTokenSource? _cts;

    // How many region loops are mid-pass. Only drives the busy indicator, so it is deliberately not
    // synchronised beyond being interlocked.
    private int _busyRegions;

    // Same shape of problem on the translation side: a provider that has stopped answering holds
    // every slot and refuses a read per poll per region. Session-wide rather than per-region,
    // because the slots are — one report says all there is to say about the provider.
    private int _slotExhaustionReported;

    // And once more for the recognition side. This one has never fired in a measured session — the
    // gate allows more concurrent inferences than a realtime session has regions — but "never on
    // this machine" is not "never", and the machines this ships to are smaller: the gate is derived
    // from the core count, so a four-core one allows a single inference at a time. Without a line in
    // the shipped log, a user there would see one block updating far less often than the others and
    // have no way to say why, and neither would anyone reading their report.
    private int _noSlotReported;

    // The engine this session runs with, chosen on 即時翻譯 and not shared with the rest of the
    // application. Set by Start before any region loop begins.
    private Models.TranslationProvider _provider;

    // What Resume needs to put the loops back the way Start left them. A paused session is still
    // the session the user framed: the blocks, the languages and the engine cannot change while it
    // is paused, because everything that could change them lives on a page a session has hidden.
    private IReadOnlyList<RealtimeRegion> _regions = [];
    private string _sourceLanguage = "";
    private string _targetLanguage = "";

    // Where the pixels come from. Handed in by the caller and owned by it — a pause must not tear
    // down a capture source that 繼續 is about to want back, and the caller is the only one that
    // knows which windows it had to keep out of frame to build it.
    private IRealtimeCaptureBackend? _capture;

    public RealtimeTranslationSession(OcrService ocr, TranslationService translation)
    {
        _ocr = ocr;
        _translation = translation;
    }

    /// <summary>Fresh lines for one region. Raised on a background thread.</summary>
    public event EventHandler<RealtimeRegionUpdate>? RegionUpdated;

    /// <summary>
    /// Raised when a pass fails in a way the user has to know about (a missing API key, an engine
    /// that is down). Raised on a background thread, and at most once per distinct message so a
    /// failure that repeats every poll does not turn into a stream of notifications.
    /// </summary>
    public event EventHandler<string>? Failed;

    /// <summary>True while <see cref="RunAsync"/> is doing something more than hashing pixels.</summary>
    public event EventHandler<bool>? BusyChanged;

    /// <summary>
    /// True while the region loops are stopped by <see cref="Pause"/> — as opposed to never started,
    /// or ended by <see cref="Stop"/>.
    /// </summary>
    public bool IsPaused { get; private set; }

    /// <param name="capture">
    /// Where this session reads the screen. Must be a backend whose frames cannot contain this
    /// application's own overlays — see <see cref="IRealtimeCaptureBackend"/>, where that is a
    /// property of how a backend is built rather than something it is asked — because a session that
    /// can recognise its own overlays feeds on its own output (#94). Stays owned by the caller.
    /// </param>
    /// <param name="readAtOnce">
    /// Whether each region reads its first frame immediately instead of waiting for a poll and for
    /// the picture to settle. True for <see cref="Resume"/>; false when a session is starting, where
    /// both waits are earning their keep — see <see cref="RunRegionAsync"/>.
    /// </param>
    public void Start(
        IReadOnlyList<RealtimeRegion> regions,
        string sourceLanguage,
        string targetLanguage,
        Models.TranslationProvider provider,
        IRealtimeCaptureBackend capture,
        bool readAtOnce = false)
    {
        // Not the public Stop: this one is the tail of the previous run only when there was one,
        // and 繼續 comes through here with the same backend it is about to keep using. Reporting on
        // it now would put a partial total in the log every time the user unpauses, and the line is
        // supposed to be the closing summary of one run of translating.
        Stop(releaseRecogniser: false, reportCapture: false);

        _provider = provider;
        _regions = regions;
        _sourceLanguage = sourceLanguage;
        _targetLanguage = targetLanguage;
        _capture = capture;

        var cts = new CancellationTokenSource();
        _cts = cts;
        Interlocked.Exchange(ref _slotExhaustionReported, 0);
        Interlocked.Exchange(ref _noSlotReported, 0);
        Interlocked.Exchange(ref _busyRegions, 0);
        // Otherwise a session that ended on a failure would swallow the same one on the next run,
        // which is the run where the user is checking whether they fixed it.
        ClearFailure();

        Log.Info(
            "Realtime session started: {Count} region(s), {Src}->{Tgt}, capture={Capture}",
            regions.Count, sourceLanguage, targetLanguage, capture.Name);

        // A watched region is idle between lines, and the recogniser cannot tell that from a tray
        // icon nobody has touched all afternoon. Told explicitly, it stops releasing the model that
        // the next line is about to need.
        _ocr.SetKeepWarm(true);

        // One loop per region rather than one loop over the regions. Sharing a loop made every
        // region wait out the slowest one: with three regions and a half-second recognition apiece,
        // the third could not update more than about twice a second no matter how little had
        // changed in it. All of this is background work anyway — the grab is a BitBlt, recognition
        // is CPU-bound, translation is I/O — so none of it belongs on the dispatcher, which has an
        // interface to keep responsive for as long as this runs.
        foreach (var region in regions)
        {
            var watched = region;
            _ = Task.Run(
                () => RunRegionAsync(watched, sourceLanguage, targetLanguage, readAtOnce, cts.Token),
                cts.Token);
        }
    }

    /// <summary>
    /// Stops watching the screen without ending the session.
    /// </summary>
    /// <remarks>
    /// For the scene the user does not want translated: a cutscene they have already read, a menu
    /// they know by heart, anything where the overlays are in the way rather than helping. Ending
    /// the session would work too, and would cost them their blocks and a trip back through the
    /// shell window to draw them again — this keeps the whole arrangement and only stops the work.
    ///
    /// The poll loops are cancelled; the recogniser is not. It used to be handed back here, on the
    /// reasoning that a paused session holding hundreds of MB is indistinguishable from one that was
    /// never paused — but the memory is not what the user is waiting for. Loading the model again was
    /// measured at 575–1027ms against a steady state of 234ms
    /// (see <see cref="Ocr.OnnxOcrEngine.SetKeepWarm"/>), and every millisecond of it lands inside
    /// the first poll after 繼續, which is the one moment in a session where somebody is watching the
    /// screen waiting for words to appear. A pause is "not now", and the arrangement is still framed
    /// and still theirs; the press that means "done" is the one that ends the session, and that is
    /// where the memory goes back — see <see cref="Stop"/>.
    ///
    /// What has already been translated does not go with it. An answer still in flight must not land
    /// on screen after the pause — the scene has moved on — but that is a statement about a pass, not
    /// about the wording it was carrying: "this source text translates to that" is still true when the
    /// user comes back. So the cache is fenced rather than emptied, and 繼續 over content that has not
    /// changed reads the region once and draws it again without a second trip to the provider. The
    /// paragraph above is why that trip was the one worth removing: it is the only stage here whose
    /// duration this application does not control.
    /// </remarks>
    /// <returns>The generation assigned to this pause, for rejecting older queued UI updates.</returns>
    public int Pause()
    {
        StopLoops();
        IsPaused = true;

        // Put back after StopLoops turned it off: a paused session is still a session, and the model
        // it will want in a moment is the one already loaded. A release queued by that turn-off is
        // benign — ReleaseIdleRuntime checks this flag before disposing anything.
        _ocr.SetKeepWarm(true);

        var generation = _translationCache.Fence();

        // Human-paced, and the first thing to check when a session is reported as having stopped
        // updating — unlike the per-poll traffic, which has to stay at Debug.
        Log.Info("Realtime session paused: region loops stopped, recogniser and translations kept");
        return generation;
    }

    /// <summary>
    /// Starts watching again after <see cref="Pause"/>, with the blocks, languages and engine the
    /// session was started with.
    /// </summary>
    /// <remarks>
    /// Every region begins from an empty view of the screen, so whatever is on screen now is read
    /// rather than compared against a frame from before the pause. Read, but not necessarily
    /// translated again: the reading is looked up in the cache the pause fenced rather than emptied,
    /// so a scene that has not changed comes back as fast as one recognition pass. A scene that has
    /// changed pays what it always did.
    /// </remarks>
    public void Resume()
    {
        if (!IsPaused || _capture is not { } capture) return;

        Log.Info("Realtime session resuming: every region read at once, from scratch");
        Start(_regions, _sourceLanguage, _targetLanguage, _provider, capture, readAtOnce: true);
    }

    /// <param name="releaseRecogniser">
    /// Whether to hand the model's memory back now rather than leaving it to the inactivity timer.
    /// True when the user has ended the session, false when this is the stop on the way back to block
    /// framing — that one is nearly always followed by 開始翻譯 within seconds, and it would pay the
    /// reload the pause above exists to avoid.
    /// </param>
    public void Stop(bool releaseRecogniser = false) =>
        Stop(releaseRecogniser, reportCapture: true);

    /// <param name="reportCapture">
    /// Whether this stop ends a run of translating, as opposed to being the restart inside
    /// <see cref="Start"/>. Only the former writes the closing summary.
    /// </param>
    private void Stop(bool releaseRecogniser, bool reportCapture)
    {
        StopLoops();
        IsPaused = false;

        if (releaseRecogniser) _ocr.ReleaseModel();

        // One line per run of translating, on the way out. Which backend was used and how it fared
        // is the first thing any report about this feature needs and the last thing the log would
        // otherwise carry — the per-poll traffic sits at Debug, and this must survive being off.
        // The counters it reads are cumulative over the backend's life, so a line written anywhere
        // but at the end is a partial total that reads exactly like a final one.
        if (reportCapture && _capture is { } capture)
            Log.Info("Realtime capture {Backend}: {Activity}", capture.Name, capture.DescribeActivity());
        _capture = null;
    }

    private void StopLoops()
    {
        _cts?.Cancel();
        _cts = null;
        // Back to releasing the model after a period of inactivity, which is the right rule again
        // the moment nothing is watching the screen.
        _ocr.SetKeepWarm(false);
        // The cache is kept across a stop/edit/start cycle on purpose: the user usually comes back
        // to the same content, and re-translating lines we already have would be a visible pause
        // for nothing.
    }

    /// <param name="readAtOnce">
    /// Whether the first look happens before the first tick and without waiting for the picture to
    /// settle. It is the difference between a poll and a press: an ordinary pass is this loop noticing
    /// something on its own, where waiting a tick lets a line that is still fading in arrive — but the
    /// first look after 繼續 is the user asking, and they are watching the screen for words. Two ticks
    /// and a recognition is most of a second of nothing happening.
    ///
    /// Not done when a session starts, deliberately. That press is followed by the framing layer
    /// coming down and the shell window hiding, so the picture really has not settled yet, and reading
    /// it early would read those instead of the application underneath.
    /// </param>
    private async Task RunRegionAsync(
        RealtimeRegion region,
        string sourceLanguage,
        string targetLanguage,
        bool readAtOnce,
        CancellationToken token)
    {
        var state = new RealtimeRegionState(region.Orientation);
        var pump = new RegionTranslationPump(this, region, sourceLanguage, targetLanguage, token);

        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            var lastScan = Stopwatch.GetTimestamp();
            var skippedPolls = 0;
            var asked = readAtOnce;
            // Which of the gate's two detector sizes the next timed read uses. Alternating is what
            // makes it lossless — see RealtimeGate — and it lives here because it is a property of
            // this region's loop, not of the policy.
            var gateAlternate = false;
            // How many gated polls in a row the gate has turned away. Both of its sizes have to
            // have had their turn before the frame counts as answered: see the turn-away below.
            var gateTurnedAway = 0;

            while (asked || await timer.WaitForNextTickAsync(token))
            {
                // Only the first pass can have been asked for; everything after it is a poll again.
                var demanded = asked;
                asked = false;

                token.ThrowIfCancellationRequested();

                // A translation that never reached the screen leaves this region recorded as showing
                // words it does not show, and the pixels will not change again on their own — so the
                // retry has to be asked for. Applied here, where the state is only ever touched by
                // this one loop.
                if (pump.TakeRetryRequest()) state.Invalidate();

                using var frame = _capture?.GrabRegion(region.Bounds);
                if (frame is null) continue;

                // Closes over this poll's frame, so the policy can summarise the text strips or the
                // whole region without ever seeing the bitmap.
                FrameFingerprint Capture(IReadOnlyList<Rectangle>? areas) =>
                    FrameFingerprint.Capture(frame, areas);

                // Asked for, so not asked about: a demanded pass reads whatever is there rather than
                // consulting a policy whose whole job is deciding which polls are worth paying for.
                // A reading caught mid-change is not lost either — the next pass keeps the better of
                // the two, see RealtimeReadingMerge.
                var reason = demanded
                    ? RealtimeReadReason.TextChanged
                    : state.Examine(Capture, region.Mode == RealtimeBlockMode.Subtitle);
                if (reason == RealtimeReadReason.Nothing)
                {
                    skippedPolls++;
                    continue;
                }

                // The two timed reads are asking whether there is anything here, not following a
                // change, and the answer is usually no — so they are asked the cheap way first. See
                // RealtimeGate: detection alone at a third of the size, which turns away three
                // quarters of the empty frames for a fifth of what the pass behind it costs, and is
                // what lets those timers run several times as often as they used to.
                if (reason != RealtimeReadReason.TextChanged
                    && RealtimeGate.WorthGating(frame.Width, frame.Height))
                {
                    var gateSize = RealtimeGate.SizeFor(frame.Width, frame.Height, gateAlternate);
                    gateAlternate = !gateAlternate;
                    var found = await _ocr.TryDetectTextAsync(
                        frame, sourceLanguage, gateSize, RealtimeGate.MinimumScore, token);

                    // Null is "no slot", which is not an answer — treated as this poll not having
                    // happened rather than as an empty region, or a busy moment would read as the
                    // text having gone away.
                    if (found is null)
                    {
                        skippedPolls++;
                        continue;
                    }

                    // On a rescan the strips are already accounted for: only a box outside them can
                    // be text this region does not know about.
                    var interesting = reason == RealtimeReadReason.Rescan
                        ? found.Count(box => !state.IsInsideWatchedText(box))
                        : found.Count;

                    if (interesting == 0)
                    {
                        Log.Debug(
                            "Realtime gate region={Region} reason={Reason} size={Size} boxes={Boxes} " +
                            "-> nothing worth reading",
                            region.Id, reason, gateSize, found.Count);

                        // Answered — but by one of the gate's two sizes, and the other one has not
                        // been asked. Recording the frame here is what stops it ever being asked,
                        // because a still picture then reports no change: measured over a panel
                        // corpus scaled the way a user's block scales it, the smaller size misses
                        // 11 frames of 23 that hold text and the larger one misses 1, so the second
                        // opinion is most of the gate's accuracy rather than a refinement of it.
                        // Two in a row is both sizes having had their turn; then the frame is
                        // recorded, and the region stops asking until something changes or the idle
                        // scan comes back for a proper read — see RealtimeRegionState.IdleScan for
                        // the 1 frame in 23 that neither size can see.
                        if (++gateTurnedAway >= 2) state.MarkScanned(Capture);
                        skippedPolls++;
                        continue;
                    }
                }

                // Past the gate, so the next turn-away starts a fresh pair.
                gateTurnedAway = 0;

                try
                {
                    SetBusy(true);
                    var reading = await ReadRegionAsync(
                        region, frame, state, Capture, sourceLanguage, pump, token);

                    // One line per look at the region, because every question about this feature
                    // being late or missing a line comes down to two things the outside cannot see:
                    // how long it had been since the region was last examined, and which of the ways
                    // out of a pass was taken. Counts and lengths only — the words themselves stay at
                    // Debug, where LogBlocks keeps them.
                    //
                    // Debug rather than Info because this fires once per poll that saw the pixels
                    // move: at a 150ms poll that is under 7/s per region, three regions, ~10MB an
                    // hour against a 12MB archive budget. A session over a video used to evict every
                    // other line in the log — including the startup snapshot and whatever the user
                    // actually opened the log
                    // for. It sat at Info because Debug needed an environment variable nobody was
                    // going to be talked through; 設定 → 進階設定 → 記錄詳細資訊 is now a checkbox,
                    // so the detail is still one click away when this is the thing being diagnosed.
                    Log.Debug(
                        "Realtime read region={Region} skipped={Skipped} since={Since}ms ocr={Ocr}ms " +
                        "lines={Lines} chars={Chars} shown={Shown} -> {Outcome}",
                        region.Id,
                        skippedPolls,
                        (int)Stopwatch.GetElapsedTime(lastScan).TotalMilliseconds,
                        reading.OcrMs,
                        reading.Lines,
                        reading.SourceLength,
                        reading.RenderedLength,
                        reading.Outcome);

                    lastScan = Stopwatch.GetTimestamp();
                    skippedPolls = 0;

                    // Skipped for want of an inference slot. Nothing has been recorded as rendered,
                    // so the same change is still pending and the next poll tries again — which is
                    // the whole point of not queueing.
                    if (reading.Outcome != PassOutcome.NoSlot) pump.ClearFailure();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One failed pass must not end the region — the engine may be briefly
                    // unavailable, and the next poll is one interval away.
                    Log.Warn(ex, "Realtime pass failed for region {Region}", region.Id);
                    pump.Report(DescribeFailure(ex));
                }
                finally
                {
                    SetBusy(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info("Realtime region {Region} stopped", region.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Realtime region {Region} ended unexpectedly", region.Id);
            Failed?.Invoke(this, LocalizationService.Format("S.Realtime.SessionAborted", ex.Message));
        }
    }

    // The indicator is on while any region is mid-pass, so it tracks a count rather than a flag.
    private void SetBusy(bool busy)
    {
        var count = busy
            ? Interlocked.Increment(ref _busyRegions)
            : Interlocked.Decrement(ref _busyRegions);

        BusyChanged?.Invoke(this, count > 0);
    }

    /// <summary>Which way out of a pass was taken. Recorded because they are indistinguishable from
    /// the outside, and "the line never appeared" has a different cause in every one of them.</summary>
    private enum PassOutcome
    {
        /// <summary>The recogniser was busy; nothing was read and the next poll will try again.</summary>
        NoSlot,
        /// <summary>Read, and no text survived recognition. The overlay keeps what it has for now.</summary>
        Empty,
        /// <summary>Read as empty often enough to believe it, so the overlay was emptied.</summary>
        Cleared,
        /// <summary>Read, and the words are the ones already on screen — see TextSimilarity.</summary>
        Unchanged,
        /// <summary>Read differently, but no line of it better than the reading already on screen.</summary>
        WorseReading,
        /// <summary>Read, the words are new, and they have been handed to the pump.</summary>
        Translating,
    }

    /// <param name="SourceLength">Characters read this pass.</param>
    /// <param name="RenderedLength">Characters the overlay was already showing, so a pass judged
    /// Unchanged can be told apart from a genuinely new line that the tolerance swallowed.</param>
    private readonly record struct PassReading(
        PassOutcome Outcome, int OcrMs, int Lines, int SourceLength, int RenderedLength);

    /// <summary>
    /// Reads one frame and decides what the region now shows. Everything here is bounded work the
    /// loop can afford to wait for; the translation, which is not, is handed to the pump.
    /// </summary>
    private async Task<PassReading> ReadRegionAsync(
        RealtimeRegion region,
        Bitmap frame,
        RealtimeRegionState state,
        Func<IReadOnlyList<Rectangle>?, FrameFingerprint> capture,
        string sourceLanguage,
        RegionTranslationPump pump,
        CancellationToken token)
    {
        // Split timings, because "the overlay feels slow" has three quite different causes —
        // recognition, the translation endpoint, and how long this loop waited before starting —
        // and they are indistinguishable from the outside. The pump logs the other half.
        var started = Stopwatch.GetTimestamp();

        // Try, not wait: a queued pass would be reading a frame that has already been replaced, and
        // would hold this region's loop shut while it did. Skipping costs one poll.
        var (primarySize, fallbackSizes) =
            RealtimeDetectorSize.For(frame.Width, frame.Height, region.Mode);
        var recognized = await _ocr.TryRecognizeAsync(
            frame, sourceLanguage, primarySize, token, region.Mode, region.Orientation);
        if (recognized is null)
        {
            state.Dialogue.RecognitionUnavailable();
            // Once per session at Warn, the rest at Debug — the same rule the grab and translation
            // sides use. Skipping is the whole recovery and the next poll is one interval away, so this is
            // not an error; it is the one thing that would explain a region updating far less often
            // than its neighbours, and it is invisible without saying so.
            if (Interlocked.Exchange(ref _noSlotReported, 1) == 0)
                Log.Warn(
                    "Realtime region {Region} skipped a poll: all {Slots} recognition slots busy; " +
                    "further skips logged at Debug",
                    region.Id, OcrService.ConcurrentRecognitions);
            else
                Log.Debug("Realtime region {Region} skipped a poll: all recognition slots busy", region.Id);

            return new PassReading(PassOutcome.NoSlot, 0, 0, 0, state.RenderedText.Length);
        }

        // Thrown-away boxes are cleared out before the pass is judged empty, because a collapse is
        // the strongest reason there is to try the other size: a detector that answered with one
        // box across the whole block has not looked at the block, and reading nothing at all is the
        // same evidence by a quieter route. Judging emptiness first left the one case that most
        // needed a second look as the one case that never got it — measured on a line that
        // collapsed on nearly every appearance and was simply lost each time.
        // Only for a block written across. A vertical one has had the same test made already, in
        // the turned frame the recogniser actually saw — see OcrService.TryRecognizeVerticalAsync.
        // Repeating it here would measure a column's length down the block against the block's own
        // height, and a full-height column is what vertical writing looks like when it is working.
        var rejectsCollapsed = region.Orientation == RealtimeTextOrientation.Horizontal;

        if (rejectsCollapsed)
            recognized = RejectCollapsedBlocks(recognized, frame.Height, region.Id);
        recognized = RejectShortReadings(recognized, region.Id);

        // Sampled here, before the fallbacks can run, because "the primary size was enough" is
        // exactly the condition this is the control group for.
        if (recognized.Count > 0)
            RealtimeFrameDump.SamplePrimaryRead(frame, region.Id, primarySize);

        // A region sitting on blank picture pays for the retries below over and over: between two
        // lines of dialogue the frame keeps moving, so the fingerprint never settles and every
        // other poll is a full pass that finds nothing. Measured over two subtitle sessions, 20–31%
        // of passes were blank and they took 44–58% of all the recognition time. The scan rate
        // itself is not the thing to cut — see MaxUnsettledPolls — but the most expensive of the
        // retries is; see RealtimeDetectorSize.WhileNothingIsShown for what that costs in rescues.
        if (!state.IsWatchingText)
            fallbackSizes = RealtimeDetectorSize.WhileNothingIsShown(
                fallbackSizes, frame.Width, frame.Height);

        // Nothing found can mean the text is out of the detector's range rather than absent, and
        // the two ways of being out of it need opposite sizes — so the one not tried yet gets a go
        // before the region is written off as empty. Only on empty: a pass that read something has
        // no reason to pay for a second inference.
        foreach (var retrySize in fallbackSizes)
        {
            if (recognized.Count > 0) break;

            var retried = await _ocr.TryRecognizeAsync(
                frame, sourceLanguage, retrySize, token, region.Mode, region.Orientation);
            if (retried is null) break;   // no free slot; the next poll can try again

            if (rejectsCollapsed)
                retried = RejectCollapsedBlocks(retried, frame.Height, region.Id);
            retried = RejectShortReadings(retried, region.Id);
            if (retried.Count == 0) continue;

            // Per pass, and content that needs the fallback size tends to need it every pass.
            Log.Debug(
                "Realtime region {Region} found {Lines} line(s) at detect={Retry} after none at {Primary}",
                region.Id, retried.Count, retrySize, primarySize);

            // The frame that proves a size was the problem: this one held text the whole time and
            // the first size still read nothing in it. Frames that no size could read say only
            // that something was wrong, and a session of them turned out to be mostly blank
            // picture between subtitles — so this is the sample the scale question needs.
            RealtimeFrameDump.SaveFallbackRescue(frame, region.Id, primarySize, retrySize);

            recognized = retried;
        }

        var ocrMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        token.ThrowIfCancellationRequested();

        var sourceText = string.Join('\n', recognized.Select(block => block.Text));
        var textBounds = ToTextBounds(recognized);

        // Claimed before anything can be drawn, so that a translation coming back out of order can
        // be told it has been overtaken. Every route to the screen goes through the pump holding it.
        var pass = pump.NextPass();

        // The pixels moved but the words did not — a cursor blinked, a background scrolled, a video
        // played on behind a caption. Record the frame so it is not examined again, and go no
        // further: this is the case that keeps a session over moving content off the network.
        if (recognized.Count == 0)
        {
            state.Dialogue.Reset();
            // Checked before the "same text" shortcut below, because both are the empty string once
            // the region has genuinely gone quiet and only this branch counts that towards clearing.
            RealtimeFrameDump.SaveUnread(frame, region.Id);

            var shownWhenEmpty = state.RenderedText.Length;
            state.MarkRendered(textBounds, capture, sourceText);

            var cleared = state.ShouldClearOverlay;
            if (cleared) pump.Publish(pass, []);

            return new PassReading(
                cleared ? PassOutcome.Cleared : PassOutcome.Empty, ocrMs, 0, 0, shownWhenEmpty);
        }

        // Line by line against what is on screen: each sentence keeps the better of its own two
        // readings, and a sentence nothing on screen answers to is simply new. Doing this per pass
        // instead — one weighted average against another — is what let a correctly read sentence be
        // thrown away because the line beside it had wobbled; see RealtimeReadingMerge.
        var merged = region.Mode == RealtimeBlockMode.Subtitle
            ? state.Dialogue.Merge(state.RenderedLines, recognized)
            : RealtimeReadingMerge.Merge(state.RenderedLines, recognized);
        var shownBefore = state.RenderedText.Length;

        // Whether this pass read the region differently at all, as opposed to reading it the same
        // way again. Taken before the state is written, because that is what it is a statement about.
        var reread = !TextSimilarity.IsSameContent(sourceText, state.RenderedText);

        // Nothing the reader can see is different: every line either says what it already said or
        // was read no better than the version already up. The strips are still updated — they may
        // have shifted a pixel — but the words and the scores they are defended with deliberately
        // are not, which stops a line drifting away one tolerated character at a time.
        if (!merged.Changed)
        {
            // Only worth a line when a reading was actually turned down; a pass that merely read the
            // same words again is the ordinary case and says nothing.
            if (reread)
                Log.Debug(
                    "Realtime region {Region} kept the better reading of {Kept} line(s): " +
                    "shown={Shown:0.00} \"{Old}\" against \"{Text}\"",
                    region.Id, merged.Kept, state.RenderedConfidence, state.RenderedText, sourceText);

            state.MarkRendered(textBounds, capture, merged.Lines);

            return new PassReading(
                reread ? PassOutcome.WorseReading : PassOutcome.Unchanged,
                ocrMs, recognized.Count, sourceText.Length, shownBefore);
        }

        // Recorded as shown before it has been translated, and deliberately: the frame has been
        // read and the words are known, so holding this region's state open until the network
        // answers would only stop the region being watched. A translation that never arrives asks
        // for this record to be undone — see RegionTranslationPump.
        //
        // The merged lines rather than the raw reading: a sentence whose re-reading lost keeps the
        // wording already on screen, which the session has translated once and cached, so carrying
        // it along with a corrected neighbour costs nothing on the network.
        state.MarkRendered(textBounds, capture, merged.Lines);
        pump.Post(pass, merged.Blocks, ocrMs);

        Log.Debug(
            "Realtime region {Region} redrew {Improved} improved, {Added} new, {Dropped} gone, " +
            "{Kept} held",
            region.Id, merged.Improved, merged.Added, merged.Dropped, merged.Kept);

        return new PassReading(
            PassOutcome.Translating, ocrMs, recognized.Count, sourceText.Length, shownBefore);
    }

    /// <summary>Drops boxes the detector threw across the whole block — see CollapsedDetection.</summary>
    internal static List<OcrTextBlock> RejectCollapsedBlocks(
        List<OcrTextBlock> blocks, double blockHeight, int regionId)
    {
        List<OcrTextBlock>? kept = null;

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (!CollapsedDetection.IsCollapsed(block.Bounds.Height, blockHeight, block.Text))
            {
                kept?.Add(block);
                continue;
            }

            // Copied lazily: nearly every pass keeps everything, and there is no reason to rebuild
            // the list for those.
            kept ??= [.. blocks.Take(index)];

            // Not Info at any traffic level: {Text} is recognised text, i.e. whatever was on the
            // user's screen, and the shipped log is documented as not carrying that.
            Log.Debug(
                "Realtime region {Region} dropped a collapsed {Height:0}px box in a {Block:0}px block: \"{Text}\"",
                regionId, block.Bounds.Height, blockHeight, block.Text);
        }

        return kept ?? blocks;
    }

    /// <summary>
    /// Drops the boxes holding a single character, and the short ones the recogniser was not sure
    /// about — both are scenery read as text rather than text.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="RejectCollapsedBlocks"/> rather than folded into it because the
    /// two ask different questions — that one measures the box, this one reads what came out of it
    /// — and a box can fail either test without failing the other. Both run on the primary read and
    /// on a fallback, because a false reading does the same damage whichever size produced it.
    ///
    /// The confidence half only catches what the shape test cannot: the box that reported "DM" over
    /// a subtitle was 158px tall in a 220px block — well clear of a real line at 86px, but short of
    /// the 90% that makes a collapse, so no measure of the box was ever going to reject it.
    /// </remarks>
    internal static List<OcrTextBlock> RejectShortReadings(List<OcrTextBlock> blocks, int regionId)
    {
        List<OcrTextBlock>? kept = null;

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var tooShort = ShortReadingDetection.IsTooShort(block.Text);
            var unconvincing =
                ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence);

            if (!tooShort && !unconvincing)
            {
                kept?.Add(block);
                continue;
            }

            // Copied lazily, as above: almost every pass keeps everything.
            kept ??= [.. blocks.Take(index)];

            // Debug for the same reason as the collapse log: {Text} is whatever was on the user's
            // screen, and the shipped log does not carry that.
            Log.Debug(
                "Realtime region {Region} dropped a {Length}-character box ({Why}, score={Score}): \"{Text}\"",
                regionId,
                block.Text?.Trim().Length ?? 0,
                tooShort ? "too short" : "short and unconvincing",
                block.Confidence is { } c ? c.ToString("0.00") : "none",
                block.Text);
        }

        return kept ?? blocks;
    }

    private void RaiseRegionUpdated(RealtimeRegionUpdate update) => RegionUpdated?.Invoke(this, update);

    private void RaiseFailed(string message) => Failed?.Invoke(this, message);

    /// <summary>Reports a failure once, however many regions run into it.</summary>
    private void ReportFailure(string message)
    {
        lock (_failureGate)
        {
            if (message == _lastReportedFailure) return;
            _lastReportedFailure = message;
        }

        RaiseFailed(message);
    }

    /// <summary>
    /// Lets the next failure be reported again, after a pass has got through. Any region getting
    /// through is evidence the trouble has passed, so this is shared too.
    /// </summary>
    private void ClearFailure()
    {
        lock (_failureGate) _lastReportedFailure = null;
    }

    /// <summary>
    /// The recognised lines as pixel rectangles in region coordinates, which is what the change
    /// detector watches from here on. Rounded outwards so a box does not lose the row of pixels its
    /// glyphs actually end on.
    /// </summary>
    private static List<Rectangle> ToTextBounds(IReadOnlyList<OcrTextBlock> blocks) =>
    [
        .. blocks.Select(block => Rectangle.FromLTRB(
            (int)Math.Floor(block.Bounds.Left),
            (int)Math.Floor(block.Bounds.Top),
            (int)Math.Ceiling(block.Bounds.Right),
            (int)Math.Ceiling(block.Bounds.Bottom)))
    ];

    /// <summary>
    /// Translates only the lines the session has not seen before, then reassembles the full result
    /// in the original order so the overlay always receives every line it has to draw.
    /// </summary>
    private async Task<List<TranslatedBlock>?> TranslateAsync(
        List<OcrTextBlock> blocks,
        string sourceLanguage,
        string targetLanguage,
        int generation,
        CancellationToken token)
    {
        if (!_translationCache.IsCurrent(generation)) return null;

        // The service is part of the key: it cannot change mid-session today, but a cache that
        // silently outlived a change of engine would serve the old engine's wording forever.
        var cacheKeyPrefix = $"{_provider}|{sourceLanguage}|{targetLanguage}|";
        var missing = blocks
            .Where(block => !_translationCache.TryGet(
                cacheKeyPrefix + block.Text, generation, out _))
            .ToList();

        if (missing.Count > 0)
        {
            var apiKey = SettingsService.Instance.Current.ApiKey;
            if (_translation.ProviderRequiresApiKey(_provider) && string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException(LocalizationService.Get("S.Realtime.MissingApiKey"));

            var (results, _) = await _translation.TranslateAsync(
                missing, sourceLanguage, targetLanguage, apiKey, cancellationToken: token, engine: _provider);

            _translationCache.ClearIfOverLimit(TranslationCacheLimit, generation);

            // Providers answer in request order; pair defensively anyway so a short reply degrades
            // to an untranslated line rather than throwing away the whole pass.
            for (int i = 0; i < missing.Count && i < results.Count; i++)
                _translationCache.Set(
                    cacheKeyPrefix + missing[i].Text,
                    results[i].TranslatedText,
                    generation);
        }

        // A pause may have landed while the provider was answering. Its result belongs to a screen
        // view the session has abandoned: do not draw it, even untranslated.
        if (!_translationCache.IsCurrent(generation)) return null;

        return blocks
            .Select(block => new TranslatedBlock(
                block.Text,
                _translationCache.TryGet(
                    cacheKeyPrefix + block.Text, generation, out var translated)
                        ? translated
                        : block.Text,
                block.Bounds,
                block.SourceLineBounds,
                block.RenderGlyphHeight) { RunsAcross = block.RunsAcross })
            .ToList();
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        InvalidOperationException => ex.Message,
        NotSupportedException => ex.Message,
        _ => LocalizationService.Format("S.Realtime.RetryingAfterFailure", ex.Message)
    };

    /// <summary>
    /// One region's translations, run off its poll loop.
    /// </summary>
    /// <remarks>
    /// Translation is the one stage whose duration this application does not control. Measured over
    /// 639 passes it answered in 82ms at the median, 1816ms at the 99th percentile and 3125ms at
    /// worst. Awaiting that inside the poll loop stopped the region being looked at for the whole
    /// time — <see cref="PeriodicTimer"/> drops the ticks that pass while it is not being awaited —
    /// so one slow answer blinded the region for a dozen polls, and a line that appeared and went in
    /// that window was never captured at all. That is what a missing sentence was: not a line
    /// misjudged, a line never seen.
    ///
    /// So a pass is posted here and the loop carries straight on to the next frame. Several may be
    /// in flight at once, because translation is I/O and a second one costs waiting rather than CPU,
    /// and they may finish out of order — hence the pass number every result carries, and the rule
    /// that a result is dropped once a later pass has reached the screen.
    /// </remarks>
    private sealed class RegionTranslationPump(
        RealtimeTranslationSession session,
        RealtimeRegion region,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken token)
    {
        private readonly RealtimePublishOrder _order = new();

        private int _retryRequested;

        /// <summary>Claims the number identifying this pass in the order the region was read.</summary>
        public long NextPass() => _order.NextPass();

        /// <summary>Translates a pass's lines and draws them, both without holding up the caller.</summary>
        public void Post(long pass, List<OcrTextBlock> blocks, int ocrMs)
        {
            if (!session._translationSlots.Wait(0))
            {
                // Every slot is held by a translation that has not answered, so the provider is
                // stalled rather than merely slow. Nothing in flight can be recalled to make room,
                // so this read is lost — and asking for a retry is what stops the region sitting
                // there recorded as showing a translation that was never drawn.
                // Once per session at Warn, the rest at Debug — the same rule a failing grab follows
                // for the same reason: a stalled provider repeats this every poll of every region, and the
                // first line already says everything the log needs to say about it.
                if (Interlocked.Exchange(ref session._slotExhaustionReported, 1) == 0)
                    Log.Warn(
                        "Realtime region {Region} dropped a read: {InFlight} translations already in " +
                        "flight; further drops logged at Debug",
                        region.Id, MaxConcurrentTranslations);
                else
                    Log.Debug(
                        "Realtime region {Region} dropped a read: {InFlight} translations already in flight",
                        region.Id, MaxConcurrentTranslations);
                RequestRetry();
                return;
            }

            // Captured after OCR. If a pause arrives from here onward, this pass is stale and both
            // TranslateAsync and the pre-publish check below refuse to let it repopulate the cache or
            // reach the screen.
            var generation = session._translationCache.Generation;

            // Not Task.Run(_, token): a token already cancelled skips the body entirely, and the
            // slot taken out above would never be given back.
            _ = Task.Run(async () =>
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    session.SetBusy(true);
                    var translated = await session.TranslateAsync(
                        blocks, sourceLanguage, targetLanguage, generation, token);
                    var translateMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

                    if (translated is null || !session._translationCache.IsCurrent(generation))
                    {
                        RequestRetry();
                        return;
                    }

                    // Both per pass, so both track the content's rate of change — see the read line
                    // in RunRegionAsync for why that cannot sit at Info.
                    if (Publish(pass, translated, generation))
                        Log.Debug(
                            "Realtime pass region={Region} ocr={Ocr}ms translate={Translate}ms lines={Lines}",
                            region.Id, ocrMs, translateMs, translated.Count);
                    else
                        // Worth its own line: it means the region changed again before this answer
                        // arrived, which is the shape of a provider too slow for the content.
                        Log.Debug(
                            "Realtime pass region={Region} overtaken after translate={Translate}ms, not drawn",
                            region.Id, translateMs);
                }
                catch (OperationCanceledException)
                {
                    // The session is stopping; nothing to report and nothing to retry.
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "Realtime translation failed for region {Region}", region.Id);
                    Report(DescribeFailure(ex));
                    RequestRetry();
                }
                finally
                {
                    session.SetBusy(false);
                    session._translationSlots.Release();
                }
            });
        }

        /// <summary>Draws a pass's lines unless a later pass has already been drawn.</summary>
        /// <returns>False when this pass has been overtaken.</returns>
        public bool Publish(
            long pass,
            IReadOnlyList<TranslatedBlock> lines,
            int? generation = null)
        {
            if (!_order.TryClaim(pass)) return false;

            session.RaiseRegionUpdated(new RealtimeRegionUpdate(
                region.Id,
                lines,
                generation ?? session._translationCache.Generation));
            return true;
        }

        /// <summary>Reports a failure, but only if it is not the one already reported.</summary>
        public void Report(string message) => session.ReportFailure(message);

        public void ClearFailure() => session.ClearFailure();

        private void RequestRetry() => Interlocked.Exchange(ref _retryRequested, 1);

        /// <summary>Whether the region should forget what it thinks is on screen and read it again.</summary>
        public bool TakeRetryRequest() => Interlocked.Exchange(ref _retryRequested, 0) == 1;
    }
}
