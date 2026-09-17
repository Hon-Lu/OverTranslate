using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NLog;
using RapidOcrNet;
using SkiaSharp;

namespace OverTranslate.Services.Ocr;

/// <summary>
/// The detector used to return boxes that start part way along a line, losing the characters before
/// them with nothing in the log to say they existed. It was the detector input's geometry, not the
/// model — see <see cref="CreateDetectorFrame"/> and the note above <c>DetectorAlignment</c>.
/// </summary>
/// <remarks>
/// #69 closed this as unfixable and said not to look for the setting that causes it, which was
/// right in its own terms: it is not a setting, it is how the image was being resized. Everything
/// that round ruled out stays ruled out, and each was ruled out with a measurement — the
/// recognition confidence floor (the leading box is never returned, so nothing filters it), all
/// three detector box thresholds (<see cref="DetectorThresholdOverride"/> — dropping BoxScoreThresh
/// to 0.10, which discards nothing, changes not one character), the normalisation statistics
/// (<see cref="ShippedDetector"/>), the detector size (<c>--scale-sweep</c>), and how tightly the
/// user framed the capture (<c>--margin-series</c> over 27 whole screens: 1.1% apart).
///
/// The border (<see cref="DetectorPaddingOverride"/>, 0 to 96) was ruled out there too and should
/// not have been: it was swept while the distortion was still in place, where it is an input to the
/// aligned dimensions and so changes the squash along with itself. See
/// <c>DetectorPadding</c> for the re-sweep.
///
/// #69's own best evidence is what names the cause, read the other way round. It found the response
/// knife-edge ALONG THE SHORT AXIS ONLY: cropping one pixel off the top took the reading from 5
/// characters to 6 and four pixels took it to 10, while cropping eight off the side changed nothing;
/// appending blank rows below the picture, which touches no content at all, walked it between 5 and
/// 10 with no pattern. That is a 32-pixel quantisation step on the short axis being crossed and
/// re-crossed — a few pixels of height nobody chose deciding how far the image gets squashed. The
/// conclusion drawn at the time ("a frame either reads or does not, replacing the detector is the
/// only lever left", and #33's rejection of PP-OCRv6_det_medium at 90ms and 62MB) followed from
/// reading that as model behaviour.
/// </remarks>
internal sealed class OnnxOcrEngine : IOcrEngine
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static readonly string ModelRoot =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocrmodels", "onnx");
    // Threads inside one inference and how many inferences may run at once, decided together — see
    // OcrThreadBudget for the table and why the product rather than either number is the thing held
    // fixed.
    private static readonly int ThreadCount = OcrThreadBudget.For(Environment.ProcessorCount).Threads;

    // Blocks whose average per-character recognition confidence falls below this are
    // discarded as noise / icon misdetections. Mirrors the old Tesseract MinWordConfidence
    // (60/100) that the previous English engine relied on to drop non-text regions.
    private const double MinRecognitionConfidence = 0.6;

    // Automatic mode classifies each block independently, but the overlay should not render two
    // font scales in one frame or jump between them when a borderline OCR reading changes. A
    // shared midpoint keeps the effective glyph height independent from the chosen layout path.
    private const double AutomaticGlyphHeightFromPitch = 1.24;

    // A runtime holds det + cls + rec ONNX sessions plus their CPU memory arenas (hundreds of
    // MB with the larger ImgResize). This is a tray-resident, occasional-use tool, so we keep
    // only the active model loaded AND release it after a period of inactivity, returning that
    // memory to baseline while idle. The next capture transparently reloads the needed model.
    private static readonly TimeSpan IdleReleaseDelay = TimeSpan.FromMinutes(1);

    // Long enough that no real inference could still be running, short enough that a caller stuck
    // behind a wedged one gets an error instead of never returning.
    private static readonly TimeSpan ModelSwapDrainTimeout = TimeSpan.FromSeconds(10);

    // Measured on a 16-core machine against a 1200x200 screen grab, back when each pass took two
    // threads: one pass 320ms; four concurrent passes 502ms in total, so 2.5x the throughput for
    // 1.6x the latency, and five was slower than four. That is where the cap of 4 comes from.
    //
    // Realtime has never reached this limit: across a full day of sessions the gate turned nobody
    // away once, because a session runs one or two blocks and each block is one loop. The batch
    // image translation feature is the caller this number was really chosen for, and the one to
    // re-measure for if it lands.
    private static readonly int InferenceSlots = OcrThreadBudget.For(Environment.ProcessorCount).Slots;

    /// <inheritdoc cref="InferenceSlots"/>
    internal static int ConcurrentRecognitions => InferenceSlots;

    private readonly object _sync = new();
    // Admits a bounded number of concurrent inferences; see RecognizeAsync for why it is bounded.
    private readonly SemaphoreSlim _inferenceGate = new(InferenceSlots, InferenceSlots);
    private readonly System.Threading.Timer _idleReleaseTimer;
    private RapidOcrRuntime? _current;
    private string? _currentModelKey;
    // Number of Detect calls currently running against _current. Inference runs OUTSIDE _sync
    // (it is slow and must not block other work or the timer), so the idle timer must never
    // dispose a runtime while this is > 0 — that would free the native ONNX sessions mid-
    // inference and crash. A Timer.Change() does NOT cancel an already-queued callback, so this
    // in-use check is what makes a stale idle callback that fires during a new capture benign.
    // Guarded by _sync.
    private int _inUse;
    private bool _disposed;
    // Suspends the idle release. Guarded by _sync.
    private bool _keepWarm;

    public OnnxOcrEngine() =>
        _idleReleaseTimer = new System.Threading.Timer(_ => ReleaseIdleRuntime());

    /// <summary>
    /// Holds the loaded model in memory regardless of how long it sits unused.
    /// </summary>
    /// <remarks>
    /// For a realtime session, where the inactivity release is measuring the wrong thing. A watched
    /// region is idle between lines of dialogue, and a gap over <see cref="IdleReleaseDelay"/> is
    /// ordinary — a quiet scene, a paused video, a menu nobody is touching. Releasing the model
    /// there means the next line pays to load it again: measured at 575–1027ms against a steady
    /// state of 234ms, all of it inside the poll loop, where it is time the region is not being
    /// watched. Idle in a session is not idle; it is waiting.
    /// </remarks>
    public void SetKeepWarm(bool keepWarm)
    {
        lock (_sync)
        {
            _keepWarm = keepWarm;

            // Nothing re-arms the countdown on its own once the last pass has finished, so leaving
            // the mode has to start it — otherwise the model would sit loaded until the next use.
            if (!keepWarm && !_disposed && _inUse == 0)
                _idleReleaseTimer.Change(IdleReleaseDelay, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Hands the loaded model's memory back now, instead of waiting out
    /// <see cref="IdleReleaseDelay"/>.
    /// </summary>
    /// <remarks>
    /// For a realtime session being paused: the user has said they are done watching for a while,
    /// and a runtime that holds hundreds of MB has no business sitting in memory for another minute
    /// on the strength of a timer. Reloading it costs the same as the first load did, which is what
    /// makes 繼續 affordable.
    ///
    /// Leaves the model alone while a Detect is running against it — freeing the native sessions
    /// mid-inference would crash the process. Nothing is lost by returning: the pass that is holding
    /// it re-arms the inactivity countdown as it finishes, so the memory still comes back on its own.
    /// </remarks>
    public void ReleaseNow()
    {
        lock (_sync)
        {
            // Otherwise the release below would be undone by the very next recognition — and a
            // caller asking for the model to go is a caller that has stopped watching the screen.
            _keepWarm = false;

            if (_disposed || _inUse > 0) return;

            DisposeCurrentRuntime();
        }
    }

    public Task<List<OcrTextBlock>> RecognizeAsync(
        Bitmap bitmap, string sourceLanguage, CancellationToken cancellationToken = default)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        return Task.Run(async () =>
        {
            // Bounded, not unlimited. Each inference already uses ThreadCount threads, so past the
            // slot count concurrent passes only split the same cores between themselves: overlapping
            // captures (frame it wrong, Esc, frame again) used to pile up that way and starve the
            // one the user was actually waiting for.
            await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Checked after the gate, not before: by the time an abandoned request reaches the
                // front of the queue its session is usually long gone, and bailing out here costs
                // nothing at all. Inference itself cannot be interrupted, so this is the last point
                // where giving up is still free.
                cancellationToken.ThrowIfCancellationRequested();

                return RecognizeCore(bitmap, sourceLanguage);
            }
            finally
            {
                _inferenceGate.Release();
            }
        }, cancellationToken);
    }

    public Task<List<OcrTextBlock>?> TryRecognizeAsync(
        Bitmap bitmap,
        string sourceLanguage,
        int? maxDetectSize = null,
        CancellationToken cancellationToken = default)
    {
        if (!OcrLanguageRouter.IsSupported(sourceLanguage))
            throw new NotSupportedException(OcrLanguageRouter.GetUnsupportedLanguageMessage(sourceLanguage));

        return Task.Run<List<OcrTextBlock>?>(() =>
        {
            // Inside the lambda, not before it: Task.Run with an already-cancelled token never runs
            // the body, so a slot taken out here would never be given back.
            if (!_inferenceGate.Wait(0, cancellationToken))
                return null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return RecognizeCore(bitmap, sourceLanguage, maxDetectSize);
            }
            finally
            {
                _inferenceGate.Release();
            }
        }, cancellationToken);
    }

    private List<OcrTextBlock> RecognizeCore(
        Bitmap bitmap, string sourceLanguage, int? maxDetectSize = null)
    {
        var normalizedLanguage = OcrLanguageRouter.Normalize(sourceLanguage);
        var useCjkRenderMetrics = OcrLanguageRouter.UsesCjkOnnx(normalizedLanguage);
        var usesAutomaticLayout = OcrLanguageRouter.UsesAutomaticLayout(normalizedLanguage);

        // Select the runtime and register an in-use reference atomically under _sync, so the
        // idle timer cannot dispose it between selection and Detect. The matching release
        // (which also re-arms the idle countdown) runs in the finally, under _sync.
        var runtime = AcquireRuntime(normalizedLanguage);
        try
        {
            Log.Info(
                "Running ONNX OCR on {W}x{H} bitmap, detect={Detect}, lang={Lang}, model={Model}, threads={Threads}",
                bitmap.Width,
                bitmap.Height,
                maxDetectSize?.ToString() ?? "default",
                normalizedLanguage,
                runtime.ModelName,
                ThreadCount);

            TextBlock[] recognised;
            List<OcrTextBlock> blocks;
            if (maxDetectSize is null)
            {
                // The screenshot path runs through the detect/recognise seam so ChromaticBoxRepair
                // can rejoin a broken row of coloured text before anything crops from it. The seam
                // is not a second implementation of recognition: across all 413 corpus captures it
                // returns the same text, in boxes at the same coordinates, as the library's one-shot
                // Detect — so what this path adds is that one repair and nothing else.
                using var session = new DetectionSession(
                    this, runtime, bitmap, normalizedLanguage, null, releasesRuntime: false);
                blocks = session
                    .Recognize(Enumerable.Range(0, session.Boxes.Count).ToArray(), out recognised)
                    .ToList();
            }
            else
            {
                using var skBitmap = ConvertToSkBitmap(bitmap);
                using var frame = CreateDetectorFrame(skBitmap, maxDetectSize);
                var result = runtime.Engine.Detect(frame.Bitmap, frame.Options);
                frame.MapToSource(result.TextBlocks);
                recognised = result.TextBlocks;
                blocks = ApplyBlockFilters(
                    recognised, normalizedLanguage, useCjkRenderMetrics, usesAutomaticLayout);
            }

            // Counts and lengths only — enough to tell "found nothing" from "found the wrong thing"
            // without the recognised text itself, which LogBlocks keeps at Debug.
            Log.Info(
                "ONNX OCR lang={Lang} rawBlocks={RawBlocks} blocks={Blocks} strLen={StrLen}",
                normalizedLanguage,
                recognised.Length,
                blocks.Count,
                // Summed here rather than taken from the library's own concatenation, which the
                // seam does not produce. The same quantity: how much text came back at all.
                recognised.Sum(block => block.Text?.Length ?? 0));

            // Before the filters rather than after, and only when they took everything: a region
            // that reads as empty has nothing left to log, which is exactly the case anyone is
            // trying to diagnose. Where the rejected boxes sit and what they scored is what tells a
            // line framed outside the block (a box against an edge, a couple of clipped glyphs)
            // from one the confidence floor threw away (a box over the text, plausible words, a
            // score just under the bar).
            if (blocks.Count == 0 && recognised.Length > 0)
                LogRejectedBlocks(normalizedLanguage, recognised);

            LogBlocks(normalizedLanguage, blocks);
            return blocks;
        }
        finally
        {
            ReleaseRuntime();
        }
    }

    internal static string GetModelKeyForLanguage(string language) =>
        OcrLanguageRouter.Normalize(language) switch
        {
            "KO" => "korean",
            // Everything else uses the general ("cjk") model — PP-OCRv6_small_rec, one model
            // covering 50 languages: Simplified and Traditional Chinese, English, Japanese, and 46
            // Latin-script ones. English UI captures very often contain embedded Chinese (chrome,
            // labels, ratings), which a Latin-only model dropped or garbled; this reads Latin AND
            // those CJK glyphs in one pass. The text is still Latin, so source-language routing
            // (UsesCjkOnnx) keeps EN on the Latin layout path. Lone-ideograph icon misreads that
            // come with reading both scripts at once are stripped only where nothing CJK is left
            // behind — see <see cref="StripLoneIdeographs"/>.
            //
            // Korean stays on its own model above because v6 carries no Hangul at all — measured
            // on its dictionary, 0 of 18,708 characters — so the one model cannot cover KO.
            _ => "cjk",
        };

    /// <summary>
    /// The detector's own output — every box it found and the score it gave — with none of the
    /// recognition, normalisation or filtering that <see cref="RecognizeAsync"/> runs afterwards.
    /// </summary>
    /// <remarks>
    /// For OcrHarness, and specifically for telling a box the detector never found from a box it
    /// found and the recogniser then read differently. Those two are indistinguishable in the
    /// finished blocks and have completely different causes, and no other entry point separates
    /// them.
    ///
    /// Everything ahead of the detector is shared with the real path on purpose — the same options,
    /// the same alignment, the same runtime — because a measurement that prepared its own input
    /// would be measuring the preparation.
    /// </remarks>
    /// <param name="maxDetectSize">As <see cref="TryRecognizeAsync"/>; null is the screenshot flow.</param>
    internal IReadOnlyList<(System.Windows.Rect Bounds, float Score)> DetectBoxesOnly(
        Bitmap bitmap, string sourceLanguage, int? maxDetectSize = null)
    {
        var runtime = AcquireRuntime(OcrLanguageRouter.Normalize(sourceLanguage));
        try
        {
            using var skBitmap = ConvertToSkBitmap(bitmap);
            using var frame = CreateDetectorFrame(skBitmap, maxDetectSize);

            return runtime.Engine.DetectBoxes(frame.Bitmap, frame.Options)
                .Select(box => (frame.ToSourceBounds(box.BoxPoints), box.Score))
                .ToList();
        }
        finally
        {
            ReleaseRuntime();
        }
    }

    /// <summary>
    /// Detection held open, so recognition can be asked for a chosen subset of the boxes instead of
    /// all of them. Everything after recognition is the shipped path.
    /// </summary>
    /// <remarks>
    /// For OcrHarness, and specifically for the candidate that detects once over the whole source
    /// image and then reads only the boxes that fall inside what the user framed: the boxes then
    /// come from a bitmap larger than the answer is about, which no other entry point allows.
    ///
    /// Assembled out of the library's own pieces rather than reimplemented, several of them reached
    /// by reflection. That is the point of the type. Recognition does NOT crop from the bitmap that
    /// was handed in: the library prepares a detector input first — outer padding, an optional
    /// letterbox, and the resize <see cref="RapidOcrOptions.ImgResize"/> caps — detects on that, and
    /// crops the part images from THAT bitmap, mapping the boxes back to the original only on the
    /// way out. Cropping from the original instead reads a different picture, and on a page the
    /// resize actually shrinks it would read a picture at the wrong size, which is exactly the cost
    /// this measurement exists to find.
    ///
    /// Verified rather than argued — see the harness's <c>--roi-fullframe</c>, which begins by
    /// recognising every box through here and checking the text against <see cref="RecognizeAsync"/>.
    /// </remarks>
    internal DetectionSession BeginDetection(
        Bitmap bitmap, string sourceLanguage, int? maxDetectSize = null)
    {
        var normalizedLanguage = OcrLanguageRouter.Normalize(sourceLanguage);
        var runtime = AcquireRuntime(normalizedLanguage);
        try
        {
            return new DetectionSession(this, runtime, bitmap, normalizedLanguage, maxDetectSize);
        }
        catch
        {
            ReleaseRuntime();
            throw;
        }
    }

    /// <summary>
    /// One detection, kept alive so its boxes can be recognised a subset at a time.
    /// </summary>
    internal sealed class DetectionSession : IDisposable
    {
        private readonly OnnxOcrEngine _owner;
        private readonly RapidOcr _engine;
        private readonly string _language;
        private readonly SKBitmap _skBitmap;
        private readonly DetectorFrame _frame;
        // Boxed, because the type is internal to the library and cannot be named here.
        private readonly object _detectorInput;
        private readonly SKBitmap _detectorBitmap;
        private readonly RapidOcrOptions _options;
        private IReadOnlyList<RapidOcrNet.TextBox> _detectorSpaceBoxes;
        // False when the caller acquired the runtime itself and releases it in its own finally.
        private readonly bool _releasesRuntime;

        internal DetectionSession(
            OnnxOcrEngine owner,
            RapidOcrRuntime runtime,
            Bitmap bitmap,
            string normalizedLanguage,
            int? maxDetectSize,
            bool releasesRuntime = true)
        {
            _owner = owner;
            _releasesRuntime = releasesRuntime;
            _engine = runtime.Engine;
            _language = normalizedLanguage;
            _skBitmap = ConvertToSkBitmap(bitmap);
            _frame = CreateDetectorFrame(_skBitmap, maxDetectSize);
            _options = _frame.Options;

            _detectorInput = PrepareDetectorInputMethod.Invoke(null, new object[] { _frame.Bitmap, _options })!;
            _detectorBitmap = (SKBitmap)DetectorInputBitmapField.GetValue(_detectorInput)!;
            var scale = (ScaleParam)DetectorInputScaleField.GetValue(_detectorInput)!;

            var detector = (TextDetector)TextDetectorField.GetValue(_engine)!;
            _detectorSpaceBoxes = detector.GetTextBoxes(
                _detectorBitmap, scale, _options.BoxScoreThresh, _options.BoxThresh, _options.UnClipRatio);

            // The caller works in the coordinates of the bitmap it handed in, so every box is
            // reported there. The detector-space originals are what recognition crops with and are
            // kept beside them, because mapping is not reversible once the resize is not 1.0.
            var mapped = ToSourceSpace(_detectorSpaceBoxes);

            // Screenshot captures only, which is what a null detector size means here — the
            // realtime path always names a size. Run before anything crops, because the whole point
            // is that the pieces are never cropped: the glyphs in the gaps between them have no box
            // of their own to read. Apply hands back the same list when nothing qualifies, which is
            // all but two captures in the corpus.
            if (maxDetectSize is null)
            {
                var repaired = ChromaticBoxRepair.Apply(
                    _skBitmap,
                    mapped.Select(box => new SKRect(
                        (float)box.Bounds.Left,
                        (float)box.Bounds.Top,
                        (float)box.Bounds.Right,
                        (float)box.Bounds.Bottom)).ToList(),
                    _detectorSpaceBoxes,
                    _frame.RatioX,
                    _frame.RatioY,
                    _options.Padding);

                if (!ReferenceEquals(repaired, _detectorSpaceBoxes))
                {
                    _detectorSpaceBoxes = repaired;
                    mapped = ToSourceSpace(repaired);
                }
            }

            Boxes = mapped;

            List<(System.Windows.Rect Bounds, float Score)> ToSourceSpace(
                IReadOnlyList<RapidOcrNet.TextBox> boxes) =>
                boxes
                    .Select(box =>
                    {
                        var points = (SKPointI[])box.BoxPoints.Clone();
                        MapToOriginalMethod.Invoke(_detectorInput, new object[] { points });
                        return (Bounds: _frame.ToSourceBounds(points), box.Score);
                    })
                    .ToList();
        }

        /// <summary>Every box the detector found, in the handed-in bitmap's own coordinates.</summary>
        internal IReadOnlyList<(System.Windows.Rect Bounds, float Score)> Boxes { get; }

        /// <summary>
        /// Recognises the boxes at the given indices into <see cref="Boxes"/> and nothing else.
        /// </summary>
        internal IReadOnlyList<OcrTextBlock> Recognize(IReadOnlyList<int> boxIndices) =>
            Recognize(boxIndices, out _);

        /// <param name="recognised">
        /// What recognition produced before <see cref="ApplyBlockFilters"/> ran, which is the only
        /// thing that can tell a box the filters threw away from one the detector never found.
        /// </param>
        /// <inheritdoc cref="Recognize(IReadOnlyList{int})"/>
        internal IReadOnlyList<OcrTextBlock> Recognize(
            IReadOnlyList<int> boxIndices, out TextBlock[] recognised)
        {
            recognised = Array.Empty<TextBlock>();
            if (boxIndices.Count == 0) return Array.Empty<OcrTextBlock>();

            var chosen = boxIndices.Select(index => _detectorSpaceBoxes[index]).ToList();
            var partImages = (SKBitmap[])GetPartImagesMethod.Invoke(
                null, new object[] { _detectorBitmap, chosen })!;
            try
            {
                // No classifier pass: DoAngle is false on every options set the app builds, and the
                // library's own 180° rotation is gated on it.
                var recognizer = (TextRecognizer)TextRecognizerField.GetValue(_engine)!;
                var lines = recognizer.GetTextLines(partImages);

                var textBlocks = new List<TextBlock>(chosen.Count);
                for (var i = 0; i < chosen.Count; i++)
                {
                    // The library's own floor, applied here for the same reason it applies it: what
                    // reaches ConvertBlocks on the shipped path has already been through it.
                    var scores = lines[i].CharScores;
                    if (scores is not { Length: > 0 } || scores.Average() < _options.TextScore)
                        continue;

                    var points = (SKPointI[])chosen[i].BoxPoints.Clone();
                    MapToOriginalMethod.Invoke(_detectorInput, new object[] { points });
                    _frame.MapToSource(points);

                    textBlocks.Add(new TextBlock
                    {
                        BoxPoints = points,
                        BoxScore = chosen[i].Score,
                        // Required by the type and ignored downstream: ConvertBlocks builds the text
                        // from Chars, the same as it does for the shipped path.
                        Text = string.Concat(lines[i].Chars ?? Array.Empty<string>()),
                        Chars = lines[i].Chars,
                        CharScores = scores,
                    });
                }

                recognised = textBlocks.ToArray();
                return ApplyBlockFilters(
                    recognised,
                    _language,
                    OcrLanguageRouter.UsesCjkOnnx(_language),
                    OcrLanguageRouter.UsesAutomaticLayout(_language));
            }
            finally
            {
                foreach (var part in partImages) part.Dispose();
            }
        }

        public void Dispose()
        {
            ((IDisposable)_detectorInput).Dispose();
            _frame.Dispose();
            _skBitmap.Dispose();
            if (_releasesRuntime)
                _owner.ReleaseRuntime();
        }
    }

    private static readonly MethodInfo PrepareDetectorInputMethod =
        typeof(RapidOcr).GetMethod("PrepareDetectorInput", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo GetPartImagesMethod =
        typeof(RapidOcr).Assembly.GetType("RapidOcrNet.OcrUtils")!
            .GetMethod("GetPartImages", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo TextRecognizerField =
        typeof(RapidOcr).GetField("_textRecognizer", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo TextDetectorField =
        typeof(RapidOcr).GetField("_textDetector", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly Type DetectorInputType =
        typeof(RapidOcr).Assembly.GetType("RapidOcrNet.RapidOcr+DetectorInput")!;

    private static readonly FieldInfo DetectorInputBitmapField =
        DetectorInputType.GetField("Bitmap", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo DetectorInputScaleField =
        DetectorInputType.GetField("Scale", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo MapToOriginalMethod =
        DetectorInputType.GetMethod("MapToOriginal", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;

    // Selects (loading if necessary) the runtime for the language and registers an in-use
    // reference under _sync. Every successful call MUST be paired with a ReleaseRuntime().
    private RapidOcrRuntime AcquireRuntime(string language)
    {
        var modelKey = GetModelKeyForLanguage(language);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_current is not null && _currentModelKey == modelKey)
            {
                _inUse++;
                return _current;
            }

            // A different model is requested, and swapping is only safe once no Detect is running
            // against the current runtime. With concurrent inference this is genuinely reachable —
            // a realtime session watching English while a screenshot is translated from Korean — so
            // wait the others out rather than failing the pass. The timeout is a backstop: it means
            // an inference has been running far longer than any real one does, and hanging here
            // would take the caller's whole session with it.
            while (_inUse > 0)
            {
                if (!Monitor.Wait(_sync, ModelSwapDrainTimeout))
                    throw new InvalidOperationException(LocalizationService.Get("S.Error.OcrSwapTimeout"));

                ObjectDisposedException.ThrowIf(_disposed, this);

                // Whoever we were waiting for may have loaded the model we wanted in the meantime.
                if (_current is not null && _currentModelKey == modelKey)
                {
                    _inUse++;
                    return _current;
                }
            }

            // Release the previous model's sessions/arenas before loading the next one.
            // Clear the fields first so a failed load doesn't leave a disposed runtime cached.
            _current?.Dispose();
            _current = null;
            _currentModelKey = null;

            var runtime = CreateRuntime(modelKey);
            _current = runtime;
            _currentModelKey = modelKey;
            _inUse++;
            return runtime;
        }
    }

    // Releases the in-use reference taken by AcquireRuntime. Once no Detect is in flight, the
    // inactivity countdown is (re)armed so the delay is measured from the end of the last use.
    private void ReleaseRuntime()
    {
        lock (_sync)
        {
            if (_inUse > 0)
                _inUse--;

            // Wakes any pass waiting to swap models — see AcquireRuntime. Pulsed even when disposed,
            // so a waiter is never left holding the door for a runtime that is going away.
            if (_inUse == 0)
                Monitor.PulseAll(_sync);

            if (_disposed || _keepWarm)
                return;

            if (_inUse == 0)
                _idleReleaseTimer.Change(IdleReleaseDelay, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private void ReleaseIdleRuntime()
    {
        lock (_sync)
        {
            // Skip disposal if we are gone, a session is holding the model open, or a Detect is
            // still running against the runtime. A timer callback that
            // was queued before a later Change() still fires; these checks are what make a stale
            // callback benign — including one queued before SetKeepWarm was turned on.
            if (_disposed || _keepWarm || _inUse > 0)
                return;

            DisposeCurrentRuntime();
        }
    }

    // Callers hold _sync and have already established that nothing is running against the runtime.
    private void DisposeCurrentRuntime()
    {
        if (_current is null) return;

        try
        {
            _current.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "釋放閒置 OCR 模型時發生例外。");
        }
        finally
        {
            _current = null;
            _currentModelKey = null;
        }
    }

    /// <summary>
    /// A text detection model and the pixel normalisation it was exported with.
    /// </summary>
    /// <param name="Path">Full path to the detector's .onnx file.</param>
    /// <param name="Mean">Per-channel mean subtracted from each pixel, in BGR order, 0–255 scale.</param>
    /// <param name="Std">Per-channel standard deviation each pixel is divided by, same order and scale.</param>
    /// <remarks>
    /// The numbers are not a tuning knob: feeding a model the statistics it was not trained with
    /// shifts every pixel it sees, so a detector measured under the wrong pair is not that detector.
    /// PP-OCRv5 was exported with the ImageNet statistics; RapidOcrNet's own PP-OCRv6 presets use
    /// 127.5/127.5 instead, which is why swapping the file alone is not enough to measure a v6
    /// detector — see <see cref="ImageNetNormalization"/> and <see cref="HalfNormalization"/>.
    /// </remarks>
    internal sealed record DetectorModel(string Path, float[] Mean, float[] Std);

    /// <summary>What PP-OCRv5 detectors were exported with.</summary>
    internal static float[] ImageNetNormalization => [123.675f, 116.28f, 103.53f];

    /// <inheritdoc cref="ImageNetNormalization"/>
    internal static float[] ImageNetNormalizationStd => [58.395f, 57.12f, 57.375f];

    /// <summary>
    /// What PP-OCRv6 detectors — including the shipped one — are read with, for both mean and
    /// deviation.
    /// </summary>
    /// <remarks>
    /// This contradicts PaddlePaddle's own <c>inference.yml</c> for the v6 detectors, which lists
    /// the ImageNet statistics, and the contradiction is not academic: swept over the same 15
    /// frames at the same sizes, PP-OCRv6_small read 8 of them at the application's primary size
    /// under 127.5 and 1 of them under ImageNet. RapidOcrNet's own v6 presets use 127.5, and the
    /// measurement agrees with the library rather than the export config.
    /// </remarks>
    internal static float[] HalfNormalization => [127.5f, 127.5f, 127.5f];

    /// <summary>
    /// The shipped detector and the statistics to read it with.
    /// </summary>
    /// <remarks>
    /// Re-measured on PP-OCRv6_det_tiny after the detector was swapped under the choice recorded on
    /// <see cref="HalfNormalization"/>, which had been made on PP-OCRv6_small. ImageNet looked like
    /// the better pair on the six fixtures in this repository and read one reported failure at twice
    /// the characters — and then lost on 137 real dumped frames: it read four of them as completely
    /// empty that 127.5 read fine ("It's bright.", "Let's pay CiRCLE a visit on the way home.",
    /// "ここは…？" twice), against one frame the other way whose reading was a misread anyway. The
    /// leading-character loss that started the investigation happens under both, at about the same
    /// rate. So 127.5 stays, and the fixtures in this repository are now known to be too small a
    /// sample to move this on. See issue #69.
    /// </remarks>
    private static DetectorModel ShippedDetector(string detPath) =>
        new(detPath, HalfNormalization, HalfNormalization);

    /// <summary>
    /// Detector to load in place of the shipped one. Null — the shipped detector — everywhere but
    /// <c>OcrHarness</c>.
    /// </summary>
    /// <remarks>
    /// A measurement seam, not a setting. Issue #22 needs the same frames read by different
    /// detectors to say whether the dead band in detector sizes is a property of the model, and
    /// the answer only means anything if everything around the detector — recogniser, dictionary,
    /// options, grouping — is the code the application really runs. Nothing in the application
    /// assigns this, so the shipped path is the untouched two-argument load below.
    ///
    /// Set it before the first recognition of an <see cref="OnnxOcrEngine"/>: a runtime already
    /// loaded is reused until it goes idle, so changing this mid-life leaves the previous detector
    /// in place. One engine per detector under measurement is the way to be sure.
    /// </remarks>
    internal static DetectorModel? DetectorOverride { get; set; }

    /// <summary>
    /// Loads the detector, classifier, recogniser and dictionary for one recognition model.
    /// </summary>
    /// <remarks>
    /// The detector is PP-OCRv6_det_tiny. The one before it, PP-OCRv5_mobile_det, did not respond
    /// to scale smoothly: swept over 15 frames a watched region had failed to read, it read 8 of
    /// them at 0.40 of native, 4 at 0.50, 6 at 0.55 and 8 again at 0.60 — a dead band with the
    /// subtitle primary size sitting in it, which is why a subtitle session spent 13% of its passes
    /// paying for fallback sizes. The same sweep with v6_det_tiny reads 9 of 15 across the whole
    /// band, and 84 of the 84 control frames the old detector already read (2 of them only from
    /// 0.70 up, where the existing fallback catches them). It is also cheaper: 89ms against 104ms
    /// at the primary size, and 1.8MB against 4.8MB on disk. See issue #22.
    ///
    /// Only the detector changed. The recogniser stayed on PP-OCRv6_small from #23, deliberately:
    /// the two models answer different questions — whether text was found at all, and whether it
    /// was read correctly — and moving both at once makes neither answer attributable.
    /// </remarks>
    private static RapidOcrRuntime CreateRuntime(string modelName)
    {
        var sharedPath = Path.Combine(ModelRoot, "shared");
        var modelPath = Path.Combine(ModelRoot, modelName);

        var detPath = Path.Combine(sharedPath, "det.onnx");
        var clsPath = Path.Combine(sharedPath, "cls.onnx");
        var recPath = Path.Combine(modelPath, "rec.onnx");
        var dictPath = Path.Combine(modelPath, "dict.txt");

        EnsureModelFile(detPath);
        EnsureModelFile(clsPath);
        EnsureModelFile(recPath);
        EnsureModelFile(dictPath);

        var detector = DetectorOverride ?? ShippedDetector(detPath);
        if (DetectorOverride is not null)
        {
            EnsureModelFile(detector.Path);
            Log.Info(
                "ONNX OCR detector overridden: {Path} mean=[{Mean}]",
                detector.Path,
                string.Join(",", detector.Mean));
        }

        var engine = new RapidOcr();

        // The model-set overload rather than the four-path one, because it is the only one that
        // carries the detector's normalisation — and the shipped detector is no longer from the
        // same family as the library's default. Verified equivalent before the swap: loaded this
        // way with the old detector and the ImageNet statistics, a sweep of two frames across all
        // fifteen sizes reproduced the four-path result line for line.
        engine.InitModels(
            new RapidOcrModelSet
            {
                DetModelPath = detector.Path,
                ClsModelPath = clsPath,
                RecModelPath = recPath,
                KeysPath = dictPath,
                DetMean = detector.Mean,
                DetStd = detector.Std,
            },
            ThreadCount);

        return new RapidOcrRuntime(modelName, engine);
    }

    // Default ImgResize (1024) downscales wide UI screenshots and destroys small-text detail,
    // causing recognition errors. Raising it keeps typical captures near native resolution;
    // smaller images are unaffected, because ImgResize only ever downscales.
    private const int ScreenshotDetectSize = 2048;

    /// <param name="maxDetectSize">
    /// Longest side to give the detector, or null for the screenshot default. A caller that knows
    /// its text is far larger than interface text passes a smaller number — see
    /// <see cref="Realtime.RealtimeDetectorSize"/> for the measurements behind that.
    /// </param>
    /// <remarks>
    /// <c>DoAngle</c> is off, against the library's default. It runs the classifier model over every
    /// detected box to decide whether the text is upside down, and nothing this application reads
    /// ever is: screen text, game interfaces and subtitles are all drawn the right way up by the
    /// application underneath. Left on it can only cost — a misfired classification flips a box and
    /// turns a readable line into nonsense — so this is not a speed-for-accuracy trade in either
    /// direction.
    ///
    /// Measured on a 1380x750 grab of a game screen with 7 text boxes: 440ms with it, 427ms without.
    /// 3%, which is worth saying out loud — the classifier is cheap next to recognition, and anyone
    /// arriving here looking for the big win should keep reading past this line. The box count is
    /// where the time goes; see issue #21.
    /// </remarks>
    /// <summary>
    /// Border to surround the image with instead of the library default of 50, or null for it.
    /// </summary>
    /// <remarks>
    /// A measurement seam for <c>OcrHarness</c>, like <see cref="DetectorOverride"/>. Nothing in the
    /// application sets this.
    ///
    /// The library's 50 was inherited rather than chosen, so it was swept under the current models
    /// (RapidOcrNet 3.0.0, PP-OCRv6_det_tiny) across 0, 8, 16, 24, 32, 50, 64 and 96, on the two
    /// small capture fixtures, 25 subtitle strips and 6 game panels. Scored as the share of each
    /// frame's own best reading that a border returned:
    ///
    /// <code>
    ///   border      0      8     16     24     32     50     64     96
    ///   strips   93.5%  91.2%  92.7%  96.8%  96.1%  97.5%  96.3%  94.6%
    ///   panels   72.1%  71.5%  71.3%  81.4%  71.7%  99.1%  85.4%
    /// </code>
    ///
    /// 50 is the best value in every category, with both neighbours worse — a peak rather than a
    /// floor, so raising it is not "safer". The small values are not merely weaker: on a 264x56
    /// capture, borders of 8 and 16 return no boxes at all where 0 returns a fragment and 50 reads
    /// the whole thing. That is <see cref="AlignForDetector"/> showing through — the border decides
    /// what the aligned dimensions become, and a few of them land on geometry the detector dislikes.
    ///
    /// The border is also not the free choice it looks like on the clock. A strip reads in 43ms
    /// without it and 77ms with it, and almost all of that difference is recognition of text that
    /// no border failed to find: the detector's own input is the same size either way, because
    /// <c>ImgResize</c> caps the long side after the border is added.
    ///
    /// EVERYTHING ABOVE WAS MEASURED WHILE THE DETECTOR INPUT WAS STILL BEING SQUASHED, and the
    /// paragraph that says "that is AlignForDetector showing through" is the reason it cannot be
    /// read as a ranking of borders. The border feeds <c>AlignedLength</c>, so moving it moved the
    /// aligned dimensions and therefore how far each axis was quantised down; what the table ranks
    /// is which border happened to land on the least distorted geometry, and 50 winning "in every
    /// category with both neighbours worse" is the shape of that rather than of a border optimum.
    /// The clock reading is superseded for the same reason — with the geometry exact the border no
    /// longer counts towards anything, and dropping it is 10% FASTER rather than slower.
    ///
    /// Re-swept with the geometry fixed, the border wants to be small rather than absent: see
    /// <c>DetectorPadding</c>, which is now 8.
    ///
    /// ITS COLOUR ONLY MATTERS AT ZERO. With no border the library never composites, so the stride
    /// strip <see cref="AlignForDetector"/> leaves on the right and bottom reaches normalisation as
    /// stored premultiplied transparent, which is black; painting it white instead cost 2.6 points
    /// of F1 on region-subtitle-mixed-boxshape (99.9% to 97.3%) and painting it black scored
    /// identically to leaving it transparent, as it must. At any non-zero border the library's own
    /// MakePadding runs, clears white and composites, so the strip is white and this is moot.
    /// </remarks>
    internal static int? DetectorPaddingOverride { get; set; }

    /// <summary>
    /// The three thresholds that turn the detector's probability map into boxes.
    /// </summary>
    /// <param name="BoxThresh">
    /// Where the probability map is cut into "text" and "not text". Higher leaves weakly answered
    /// strokes out of the component, which is what puts a box edge inside a glyph.
    /// </param>
    /// <param name="BoxScoreThresh">
    /// The mean probability a finished box must reach to be returned at all. A box under it is
    /// dropped inside the library, before anything this class can see or count.
    /// </param>
    /// <param name="UnClipRatio">
    /// How far the shrunken polygon is expanded back out. Too small and every box loses a little at
    /// each end.
    /// </param>
    internal readonly record struct DetectorThresholds(
        float BoxThresh,
        float BoxScoreThresh,
        float UnClipRatio);

    /// <summary>
    /// What PP-OCRv6_det_tiny was exported to be read with, from the <c>inference.yml</c> published
    /// beside the model.
    /// </summary>
    /// <remarks>
    /// Until #71 these were whatever <c>RapidOcrOptions.Default</c> happened to carry — 0.30, 0.50
    /// and 1.60, which are the library's generic numbers and not this model's. Every one of the
    /// three was wrong, in the direction that costs text: a stricter binarisation threshold leaves
    /// faint strokes out of a box, a stricter box score throws whole boxes away, and a larger unclip
    /// returns boxes taller than the glyphs in them — and box height is what the overlay sizes its
    /// font from.
    ///
    /// Measured on the model's own values against three workloads, all of which improve:
    ///
    /// <code>
    ///   workload                       shipped        exported
    ///   realtime subtitles, 45 frames  697 chars      701, boxes 5% tighter
    ///   realtime fallback size, 19     364 chars      371, boxes 6% tighter
    ///   screenshot flow, 16 frames     3481 chars     3522, 15 of 16 frames equal or better
    /// </code>
    ///
    /// Small, but free, and it is a correction rather than a tuning: the same class of mistake as
    /// reading a v6 detector with v5's normalisation statistics.
    ///
    /// WHAT WAS TRIED AND REJECTED. Raising the box score to reject the noise boxes that reach the
    /// screen as huge garbage — the obvious move — loses on every workload (682 against 697 on
    /// subtitles, 3450 against 3481 on screenshots) and reads nothing at all on more frames. The
    /// noise boxes it was aimed at survive it. See #71.
    /// </remarks>
    internal static readonly DetectorThresholds ExportedThresholds = new(0.2f, 0.4f, 1.4f);

    /// <summary>
    /// Detector post-processing thresholds to use instead of <see cref="ExportedThresholds"/>, or
    /// null for those.
    /// </summary>
    /// <remarks>
    /// A measurement seam for <c>OcrHarness</c>, like <see cref="DetectorPaddingOverride"/>. Nothing
    /// in the application sets this.
    ///
    /// Worth a seam because these three are the only part of the pipeline that can lose text without
    /// leaving a trace — <c>rawBlocks</c> is counted after the library has already applied
    /// <see cref="DetectorThresholds.BoxScoreThresh"/>, so a box it dropped is indistinguishable in
    /// the log from a box that was never found. #69 spent a round proving that by sweeping each of
    /// them one at a time, and missed the answer for exactly that reason: the other two stayed on the
    /// library's values while one moved, and what was wrong was all three together.
    /// </remarks>
    internal static DetectorThresholds? DetectorThresholdOverride { get; set; }

    /// <remarks>
    /// Internal rather than private only so OcrHarness can ask what the app would send. A measuring
    /// mode that rebuilt these itself would be measuring its own copy, and the copy is exactly the
    /// thing that drifts.
    /// </remarks>
    internal static RapidOcrOptions CreateOptions(int? maxDetectSize)
    {
        var options = RapidOcrOptions.Default with
        {
            ImgResize = maxDetectSize ?? ScreenshotDetectSize,
            DoAngle = false,
            Padding = DetectorPaddingOverride ?? DetectorPadding,
        };

        var thresholds = DetectorThresholdOverride ?? ExportedThresholds;
        return options with
        {
            BoxThresh = thresholds.BoxThresh,
            BoxScoreThresh = thresholds.BoxScoreThresh,
            UnClipRatio = thresholds.UnClipRatio,
        };
    }

    // Everything between the library handing back its raw blocks and the caller getting usable ones.
    // One method rather than a chain repeated at each entry point: the order matters (see the
    // comments inside), and a second copy of it is the kind of thing that drifts a filter at a time.
    /// <remarks>
    /// Internal rather than private so the source-language contract can be tested on the real chain
    /// instead of one rebuilt in the test. Two rules in here have already drifted into being
    /// language-dependent by accident — the lone-ideograph cleanup openly, and BoxShapeNoise by
    /// reading a Bounds that normalisation had rewritten — and neither was caught by the tests that
    /// covered those rules one at a time. A test that calls this method inherits whatever is added
    /// to the chain later; one that lists the stages does not.
    /// </remarks>
    internal static List<OcrTextBlock> ApplyBlockFilters(
        TextBlock[] textBlocks, string normalizedLanguage, bool useCjkRenderMetrics, bool usesAutomaticLayout)
    {
        var converted = ConvertBlocks(textBlocks);

        // Every language, because an accented letter is a misread whichever script surrounds
        // it, and it costs the whole line its translation rather than just one character.
        converted = FoldBlockDiacritics(converted);

        // Every language, and that is the change that let this run at all. It used to be a Latin-
        // pages-only rule, which made the recognised text depend on the source language picked;
        // narrowed to "only when nothing CJK is left" it is safe to apply to all four alike, so
        // 田Projects reads as Projects under 自動 and under 日文 and under 中文 identically. Before
        // normalisation, so the script and glyph height a block is measured with come from the
        // text that survives. See StripLoneIdeographs for what it will not touch.
        converted = StripIconIdeographs(converted);

        // After normalisation, because that is where a CJK box is pulled in onto its glyphs and
        // the shape being judged becomes the real one. Before grouping, which happens further
        // out, so a stray box is never joined to the line beside it.
        var normalized = usesAutomaticLayout
            ? NormalizeAutomaticBlocks(converted)
            : NormalizeBlocks(converted, useCjkRenderMetrics);

        return RemoveMisshapenBlocks(normalized, normalizedLanguage);
    }

    private static List<OcrTextBlock> ConvertBlocks(TextBlock[] textBlocks)
    {
        var blocks = new List<OcrTextBlock>(textBlocks.Length);
        foreach (var block in textBlocks)
        {
            var text = string.Concat(block.Chars ?? Array.Empty<string>()).Trim();
            if (string.IsNullOrWhiteSpace(text) || block.BoxPoints is null || block.BoxPoints.Length == 0)
                continue;

            var confidence = block.CharScores is { Length: > 0 } scores ? scores.Average() : 1f;
            if (confidence < MinRecognitionConfidence)
                continue;

            // Kept blocks carry their score into the log as well as the rejected ones. Reading the
            // same subtitle several times produces several slightly different answers, and the
            // score is the only thing that says which of them to believe.
            if (Log.IsDebugEnabled)
                Log.Debug("ONNX OCR kept score={Score:0.00} text=\"{Text}\"", confidence, text);

            var left = block.BoxPoints.Min(p => p.X);
            var top = block.BoxPoints.Min(p => p.Y);
            var right = block.BoxPoints.Max(p => p.X);
            var bottom = block.BoxPoints.Max(p => p.Y);
            blocks.Add(new OcrTextBlock(
                text,
                new System.Windows.Rect(left, top, right - left, bottom - top),
                Confidence: confidence));
        }

        return blocks
            .OrderBy(b => b.Bounds.Y)
            .ThenBy(b => b.Bounds.X)
            .ToList();
    }

    /// <summary>
    /// Drops boxes that cannot be holding the text read out of them — see <see cref="BoxShapeNoise"/>.
    /// </summary>
    /// <remarks>
    /// Applies to every language, as the lone-ideograph rule beside it now does too. A Japanese or
    /// Korean capture had no noise filter at all before this, and the lone □ that a detector
    /// returns for a strip of interface is not a script-specific problem.
    /// </remarks>
    internal static List<OcrTextBlock> RemoveMisshapenBlocks(List<OcrTextBlock> blocks, string language)
    {
        List<OcrTextBlock>? kept = null;

        for (var index = 0; index < blocks.Count; index++)
        {
            if (!BoxShapeNoise.IsTooWideForItsText(blocks[index]))
            {
                kept?.Add(blocks[index]);
                continue;
            }

            kept ??= [.. blocks.Take(index)];

            if (Log.IsDebugEnabled)
                Log.Debug(
                    "ONNX OCR dropped a misshapen box lang={Lang} {W:0}x{H:0} text=\"{Text}\"",
                    language, blocks[index].Bounds.Width, blocks[index].Bounds.Height, blocks[index].Text);
        }

        return kept ?? blocks;
    }

    /// <summary>
    /// Rewrites the text of blocks that begin or end with an icon's ideograph — see
    /// <see cref="StripLoneIdeographs"/>. Returns as many blocks as it was given.
    /// </summary>
    /// <remarks>
    /// The rule it used to have could empty a block, and the caller then removed it. That cannot
    /// happen here: a cut needs a Latin letter beside the ideograph to happen at all, so at least
    /// that letter survives and every block handed in is handed back.
    ///
    /// IT CAN STILL COST A BLOCK ITS LIFE FURTHER DOWN, which is the part worth knowing. This runs
    /// before normalisation and <see cref="RemoveMisshapenBlocks"/> runs after it, and that filter
    /// judges a box against how many characters are in it — so taking one character off can push a
    /// box over <see cref="BoxShapeNoise"/>'s ratio and have it dropped there. Measured on the
    /// screenshot corpus, exactly one capture of 256 loses a block this way: a Korean panel's "早E"
    /// becomes "E", too little text for a box that wide, and goes. That reading is icon noise and
    /// the other skill slots on the same screen read "E V+", so the outcome is right — but it is
    /// the shape filter deleting it, not this, and a redesign of the icon heuristic has to account
    /// for the coupling rather than assume text cleanup is free.
    ///
    /// None of it reintroduces a source-language dependency: neither this rule nor the shape filter
    /// asks what the user picked, so the same picture still loses the same block on all four.
    /// </remarks>
    internal static List<OcrTextBlock> StripIconIdeographs(List<OcrTextBlock> blocks)
    {
        List<OcrTextBlock>? cleaned = null;

        for (var index = 0; index < blocks.Count; index++)
        {
            var text = StripLoneIdeographs(blocks[index].Text);
            if (text == blocks[index].Text)
            {
                cleaned?.Add(blocks[index]);
                continue;
            }

            cleaned ??= [.. blocks.Take(index)];
            cleaned.Add(blocks[index] with { Text = text });

            if (Log.IsDebugEnabled)
                Log.Debug(
                    "ONNX OCR stripped an icon ideograph \"{Before}\" -> \"{After}\"",
                    blocks[index].Text, text);
        }

        return cleaned ?? blocks;
    }

    private static List<OcrTextBlock> FoldBlockDiacritics(List<OcrTextBlock> blocks)
    {
        List<OcrTextBlock>? folded = null;

        for (var index = 0; index < blocks.Count; index++)
        {
            var text = FoldLatinDiacritics(blocks[index].Text);
            if (text == blocks[index].Text)
            {
                folded?.Add(blocks[index]);
                continue;
            }

            // Copied lazily: an accented letter is rare, so nearly every pass keeps the list it
            // was given.
            folded ??= [.. blocks.Take(index)];
            folded.Add(blocks[index] with { Text = text });

            Log.Debug(
                "ONNX OCR folded accented letters lang=\"{Lang}\" \"{Before}\" -> \"{After}\"",
                "latin", blocks[index].Text, text);
        }

        return folded ?? blocks;
    }

    // Folds accented Latin letters onto their plain form: "șong" -> "song".
    //
    // PP-OCRv6 carries ~200 diacritical characters so one model can serve 46 Latin-script
    // languages. None of the languages this application reads (EN, ZH, ZH-HANT, JA, KO) use them,
    // so when one appears in a reading it is a misread of the plain letter — and it does more
    // damage than a wrong letter usually would, because the result is a word no translator knows.
    // Measured: "That kind of șong" (U+0219, Romanian s-with-comma) came back at 0.93 confidence,
    // far too sure to be caught by any score floor, and the line was translated as nonsense while
    // the very next frame read plain "song" and translated correctly.
    //
    // Deliberately limited to the Latin ranges. Normalising everything would decompose Japanese
    // voiced kana as well — が is か plus a combining mark — and stripping that mark would quietly
    // turn Japanese into a different word.
    internal static string FoldLatinDiacritics(string text)
    {
        if (!text.Any(IsLatinWithDiacritic))
            return text;

        var folded = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!IsLatinWithDiacritic(c))
            {
                folded.Append(c);
                continue;
            }

            // The decomposed form is the base letter followed by its combining marks, so the first
            // character that is not a mark is the letter wanted. Characters that do not decompose
            // at all (ø, đ) come back unchanged, which is the right answer for them too.
            var baseLetter = c.ToString()
                .Normalize(NormalizationForm.FormD)
                .FirstOrDefault(ch =>
                    CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark);

            folded.Append(baseLetter == '\0' ? c : baseLetter);
        }

        return folded.ToString();
    }

    private static bool IsLatinWithDiacritic(char c) =>
        c is >= 'À' and <= 'ɏ'    // Latin-1 Supplement, Latin Extended-A and -B
        or >= 'Ḁ' and <= 'ỿ';     // Latin Extended Additional

    // Strips a single isolated Han ideograph glued to the start or end of a Latin word, which is
    // what an icon misread looks like. The letter-adjacency guard preserves date glyphs like the
    // 年/月/日 in "2026年5月8日" (those sit next to digits), and multi-ideograph runs (真實中文 such
    // as 翻譯這個網頁 / 免費) are never single, so are kept.
    //
    // A block that is nothing BUT one ideograph is deliberately not touched. It used to be dropped
    // outright, on the reasoning that a lone ideograph on an English page is an icon — and that is
    // sometimes true, but 攻 防 技 火 水 光 闇 標準 are how a Chinese or Japanese interface labels
    // things, and a screenshot is not required to be in one language just because the user named
    // one. Keeping a piece of OCR rubbish costs the reader one wrong word; deleting a real label
    // costs them the one thing they pointed the tool at.
    //
    // The remainder gate is what makes this safe enough to run on every source language, which is
    // the shape it has to have: a cleanup that rewrites text on some source languages and not
    // others cannot be reconciled with reading the same picture the same way whatever the user
    // picked. Strip only when what is left carries no CJK at all — 本Wikiについて keeps its 本
    // because the rest of it is Japanese, and a line that reads as CJK or Mixed after the cut is
    // one this rule has no business judging.
    //
    // Two known gaps, neither closed by the narrowing:
    //
    //   文A日本語 — the remainder A日本語 is Mixed, so the 文 an icon was read as stays. That is the
    //   price of the gate and it is the right way round: a kept rubbish character is visible to the
    //   reader, a deleted real one is not.
    //
    //   閣Lv100 50 — the remainder is pure Latin so this DOES strip, and on the picture that came
    //   from it is a real 闇 misread as 閣, not an icon. Narrowing does not solve wrong deletion;
    //   only a heuristic that looks at the box geometry or the recognition confidence can tell an
    //   icon's ideograph from a text one, and that is still deferred. Do not read this rule as
    //   having fixed that.
    internal static string StripLoneIdeographs(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            return text;

        var stripped = text;

        if (stripped.Length >= 2 && LayoutScriptDetection.IsHanIdeograph(stripped[0]) && !LayoutScriptDetection.IsHanIdeograph(stripped[1]) && char.IsAsciiLetter(stripped[1]))
            stripped = stripped[1..].TrimStart();

        if (stripped.Length >= 2 && LayoutScriptDetection.IsHanIdeograph(stripped[^1]) && !LayoutScriptDetection.IsHanIdeograph(stripped[^2]) && char.IsAsciiLetter(stripped[^2]))
            stripped = stripped[..^1].TrimEnd();

        // Same reading of "CJK" the grouping side uses, so the two cannot drift apart on what
        // counts as real text. When in doubt, keep: this returns the original, not the cut.
        return LayoutScriptDetection.For(stripped) is OcrLayoutScript.Cjk or OcrLayoutScript.Mixed
            ? text
            : stripped;
    }

    /// <summary>
    /// Which of the two render normalisations one recognised block gets when the source language
    /// is automatic. Kana is unambiguously Japanese, and two Han characters identify real CJK text.
    /// </summary>
    /// <remarks>
    /// Not the block's script — that is <see cref="LayoutScriptDetection.For"/>, which counts a
    /// single Han character and which grouping reads. This is a choice between two ways of sizing
    /// the overlay's font, and the two answers deliberately differ: the layout side wants to know
    /// what is written, the render side wants a font scale that does not jump between frames when
    /// a borderline reading changes. Nothing here reaches grouping.
    /// </remarks>
    internal static bool UsesCjkRenderMetricsForText(string text) =>
        text.Any(LayoutScriptDetection.IsKana) || text.Count(LayoutScriptDetection.IsHanIdeograph) >= 2;

    internal static List<OcrTextBlock> NormalizeAutomaticBlocks(List<OcrTextBlock> blocks)
    {
        var normalized = new List<OcrTextBlock>(blocks.Count);

        foreach (var block in blocks)
        {
            var useCjkRenderMetrics = UsesCjkRenderMetricsForText(block.Text);
            normalized.Add(NormalizeBlock(block, useCjkRenderMetrics, AutomaticGlyphHeightFromPitch));
        }

        return normalized;
    }

    internal static List<OcrTextBlock> NormalizeBlocks(List<OcrTextBlock> blocks, bool useCjkRenderMetrics)
        => blocks.Select(block => NormalizeBlock(block, useCjkRenderMetrics)).ToList();

    /// <summary>
    /// Glyph body height estimated from a detection box: 0.82 of it, clamped against the average
    /// glyph pitch on wide lines where unclip leaves the box vertically loose.
    /// </summary>
    /// <remarks>
    /// ONNX/unclip can return vertically loose boxes on wide single lines. The average glyph pitch
    /// is a better proxy for the real line height than an over-tall detection rectangle.
    /// </remarks>
    private static double EstimateGlyphHeight(System.Windows.Rect box, int glyphCount, double glyphHeightFromPitch)
        => EstimateGlyphHeight(box, glyphCount, glyphHeightFromPitch, out _);

    /// <param name="trace">
    /// The path this call took, for the diagnostics to print. Produced here rather than worked out
    /// again by whoever prints it: a second copy of these conditions is a copy that can disagree
    /// with this one, and the disagreement shows up as a trace quietly describing code nobody runs.
    /// </param>
    /// <inheritdoc cref="EstimateGlyphHeight(System.Windows.Rect, int, double)"/>
    private static double EstimateGlyphHeight(
        System.Windows.Rect box, int glyphCount, double glyphHeightFromPitch, out GlyphHeightTrace trace)
    {
        const double verticalScale = 0.82;

        var glyphHeight = box.Height * verticalScale;
        var boxEstimate = glyphHeight;

        var hasEnoughGlyphs = glyphCount >= ShortTextGlyphHeight.PitchCorrectedFromGlyphs;
        var isWideEnough = box.Width > box.Height * 2;

        if (hasEnoughGlyphs && isWideEnough)
        {
            var estimatedGlyphPitch = box.Width / glyphCount;
            glyphHeight = Math.Min(glyphHeight, estimatedGlyphPitch * glyphHeightFromPitch);
        }

        var estimated = Math.Max(1, glyphHeight);

        trace = new GlyphHeightTrace(
            OcrLayoutScript.Unknown,
            box.Width,
            box.Height,
            glyphCount,
            glyphHeightFromPitch,
            boxEstimate,
            glyphCount > 0 ? box.Width / glyphCount * glyphHeightFromPitch : null,
            box.Width - box.Height * 2,
            hasEnoughGlyphs,
            isWideEnough,
            hasEnoughGlyphs && isWideEnough,
            // Read off the value that came back rather than by asking which of the two is smaller a
            // second time, so a tie is reported as the branch having changed nothing.
            glyphHeight < boxEstimate,
            estimated != glyphHeight,
            ShortTextApplied: false,
            ShortTextCandidate: null,
            ShortTextSelected: false,
            estimated != glyphHeight ? GlyphHeightSource.Floor
                : glyphHeight < boxEstimate ? GlyphHeightSource.Pitch
                : GlyphHeightSource.Box,
            estimated);

        return estimated;
    }

    /// <summary>
    /// The same estimate keyed on the block's own script rather than on the language the user
    /// picked, so it reads the same under 自動 as under 日文.
    /// </summary>
    /// <remarks>
    /// Mixed and Unknown get nothing: there is no single glyph body to estimate, and grouping
    /// falls back to the raw detection box for them. Latin carries the short-line correction the
    /// overlay's own height carries, because too few glyphs for the pitch clamp leaves 0.82 of the
    /// box standing, which is 1.7x the truth. CJK does not, matching how its box is normalised.
    /// </remarks>
    internal static double? LayoutGlyphHeightFor(OcrLayoutScript script, System.Windows.Rect box, string text)
        => LayoutGlyphHeightFor(script, box, text, out _);

    /// <param name="trace">
    /// Every intermediate value on the way to the answer, from the estimate itself. This is what
    /// the grouping diagnostics print, and the only honest way to say which of the three paths a
    /// line took: the three cannot be told apart by reading the number that comes back.
    /// </param>
    /// <inheritdoc cref="LayoutGlyphHeightFor(OcrLayoutScript, System.Windows.Rect, string)"/>
    internal static double? LayoutGlyphHeightFor(
        OcrLayoutScript script, System.Windows.Rect box, string text, out GlyphHeightTrace trace)
    {
        var glyphCount = text.Count(c => !char.IsWhiteSpace(c));

        if (script is not (OcrLayoutScript.Latin or OcrLayoutScript.Cjk))
        {
            trace = GlyphHeightTrace.NotEstimated(script, box, glyphCount);
            return null;
        }

        var glyphHeight = EstimateGlyphHeight(box, glyphCount, script == OcrLayoutScript.Cjk ? 1.18 : 1.3, out trace);
        trace = trace with { Script = script };

        if (script == OcrLayoutScript.Cjk)
            return glyphHeight;

        var corrected = ShortTextGlyphHeight.For(glyphHeight, box.Height, glyphCount, out var correction);
        trace = trace with
        {
            ShortTextApplied = correction.Applied,
            ShortTextCandidate = correction.Candidate,
            ShortTextSelected = correction.Selected,
            Source = correction.Selected ? GlyphHeightSource.ShortText : trace.Source,
            Result = corrected,
        };

        return corrected;
    }

    /// <param name="useCjkRenderMetrics">
    /// Which overlay-font normalisation to apply. It follows the source language the user picked
    /// (or, under 自動, one block's own text), and it is render-only: it decides the pitch
    /// multiplier and whether Bounds is pulled in onto the glyphs. Everything grouping reads is
    /// filled in below from the block's own text and the detector's own box, so this cannot reach
    /// it — which is the whole of why the metrics were split.
    /// </param>
    private static OcrTextBlock NormalizeBlock(
        OcrTextBlock block,
        bool useCjkRenderMetrics,
        double? glyphHeightFromPitchOverride = null)
    {
        // Convert the average source-glyph pitch (width / glyphCount) into the line height that
        // drives the overlay font size, clamping the unclipped (loose) detection box so text is
        // not rendered far too large. The multiplier is keyed on the *rendered* script, which is
        // always the translated CJK text — so a Latin source page must use ~the CJK ratio too,
        // not a Latin one. Measured EN-vs-KO box heights on the same screenshot showed the old
        // Latin value (2.0) rendered English ~1.7x larger than the Korean (CJK) path; 1.3 brings
        // it in line, leaving English just slightly larger than CJK.
        var glyphHeightFromPitch = glyphHeightFromPitchOverride ?? (useCjkRenderMetrics ? 1.18 : 1.3);

        // Per block, from its own text, and the box exactly as the detector drew it — neither of
        // them touched by useCjkRenderMetrics above. Both callers reach here, and this runs before
        // the CJK branch below rewrites Bounds, so it is the one place the layout side is told what
        // it is looking at.
        var layoutScript = LayoutScriptDetection.For(block.Text);
        block = block with
        {
            LayoutScript = layoutScript,
            LayoutBounds = block.Bounds,
            LayoutGlyphHeight = LayoutGlyphHeightFor(layoutScript, block.Bounds, block.Text),
        };

        var bounds = block.Bounds;
        var glyphCount = block.Text.Count(c => !char.IsWhiteSpace(c));
        var glyphHeight = EstimateGlyphHeight(bounds, glyphCount, glyphHeightFromPitch);

        if (useCjkRenderMetrics)
        {
            // CJK glyphs ≈ the detection box height, so shrinking + recentering the box
            // drives both the overlay font and its background coverage correctly.
            var adjustedY = bounds.Y + (bounds.Height - glyphHeight) / 2.0;
            return block with { Bounds = new System.Windows.Rect(bounds.X, adjustedY, bounds.Width, glyphHeight) };
        }

        // Latin: the detection box is much taller than the rendered CJK font. Keep the
        // full box as the bubble's coverage area (so it still hides the taller original
        // Latin glyphs) and carry the reduced glyph height separately for font sizing.
        //
        // Too few glyphs for the pitch clamp above to have run leaves that height at 0.82
        // of the box, which is 1.7x the truth — a one- or two-character line rendered
        // enormously. See ShortTextGlyphHeight for the measurements.
        return block with
        {
            RenderGlyphHeight = ShortTextGlyphHeight.For(glyphHeight, bounds.Height, glyphCount)
        };
    }

    // Debug on purpose, and the shipped configuration drops that level: this is the text the user
    // just had on screen, so it must never reach a log file that gets sent to anyone.
    /// <summary>
    /// Everything the detector found on a pass that ended up empty, with the score that decided it.
    /// </summary>
    private static void LogRejectedBlocks(string language, TextBlock[] blocks)
    {
        if (!Log.IsDebugEnabled)
            return;

        for (var index = 0; index < blocks.Length; index++)
        {
            var block = blocks[index];
            var text = string.Concat(block.Chars ?? []).Trim();
            var score = block.CharScores is { Length: > 0 } scores ? scores.Average() : 0;
            var points = block.BoxPoints;

            Log.Debug(
                "ONNX OCR rejected lang={Lang} index={Index} score={Score:0.00} floor={Floor:0.00} " +
                "bounds=({L},{T},{R},{B}) text=\"{Text}\"",
                language,
                index,
                score,
                MinRecognitionConfidence,
                points is { Length: > 0 } ? points.Min(p => p.X) : -1,
                points is { Length: > 0 } ? points.Min(p => p.Y) : -1,
                points is { Length: > 0 } ? points.Max(p => p.X) : -1,
                points is { Length: > 0 } ? points.Max(p => p.Y) : -1,
                text);
        }
    }

    private static void LogBlocks(string language, IReadOnlyList<OcrTextBlock> blocks)
    {
        if (!Log.IsDebugEnabled)
            return;

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            Log.Debug(
                "ONNX OCR block lang={Lang} index={Index} bounds=({X:0.#},{Y:0.#},{W:0.#},{H:0.#}) text=\"{Text}\"",
                language,
                index,
                block.Bounds.X,
                block.Bounds.Y,
                block.Bounds.Width,
                block.Bounds.Height,
                block.Text);
        }
    }

    // RapidOcrNet feeds the detector an image whose width and height have each been rounded down
    // to a multiple of DetectorAlignment — and rounded down a whole extra step, via
    // (n / 32 - 1) * 32 in ScaleParam.GetScaleParam. The two axes are quantised independently, so
    // the amount of squashing differs between them by an amount that depends on the exact capture
    // size. A 264x56 capture reaches the detector squashed to 0.88x horizontally but 0.62x
    // vertically; widen the same selection by six pixels and 270x60 comes out 0.86x by 1.00x. That
    // is why two captures of identical pixels could recognise differently ("domain" -> "donain"):
    // the user's selection box, not the glyphs, was choosing the aspect ratio.
    //
    // Detect() pads the bitmap by options.Padding on all four sides and, for anything below
    // ImgResize, targets exactly that padded long side — so the scale factor is already 1.0 on the
    // long axis and the quantisation is the only thing left distorting the image. Growing the
    // bitmap so the padded dimensions land on multiples of DetectorAlignment skips the
    // quantisation on both axes, and the detector sees native, undistorted pixels.
    //
    // Only the right and bottom edges grow, leaving block coordinates in the caller's frame, and
    // the added pixels are the same transparent black Detect() already surrounds every capture
    // with — this pushes that border out by under 32px rather than introducing a new edge.
    //
    // WHAT THIS DOES NOT FIX. Above ImgResize a real downscale takes over, and there this buys
    // nothing: Detect() scales the padded image so its long side lands on ImgResize exactly (a
    // multiple of 32, so that axis survives), then quantises the short axis anyway, costing it
    // another 0-63px. A 2330x1102 capture still reaches the detector squashed by 5.2%, a
    // 2554x1437 one by 2.9% — small next to the 30% above, but still an amount that moves with
    // the selection size. Captures that large therefore behave exactly as they did before this
    // change, residual squash included.
    //
    // THE PARAGRAPH ABOVE DESCRIBED A GUARANTEE THIS METHOD NEVER HAD, and it is why the leading
    // characters of a line kept going missing. Detect() targets
    // min(ImgResize, aligned long side) + 2 * Padding, and ImgResize is computed from the size
    // BEFORE alignment — Realtime.RealtimeDetectorSize is asked about the region the user framed.
    // Alignment then grows that bitmap by up to 31px, so the target lands just UNDER the padded
    // long side, the ratio is no longer 1.0, and the quantisation runs after all. On a 465x76
    // dialogue box the aligned 576x192 reached the detector as 512x128: 0.89x across, 0.67x down.
    // Under it, "「うん、" was never detected at all.
    //
    // So the downscale is done here instead, isotropically, and ImgResize is then set above the
    // input so the library's own resize is the identity — see CreateDetectorFrame. Both prepared
    // dimensions are multiples of DetectorAlignment by construction at that point, which is the
    // one case ScaleParam leaves alone. The detector finally sees the aspect ratio it was handed.
    //
    // The measurement that turned this down before ("111/120 against 115/120 on a ground-truth
    // benchmark, inside the noise") compared doing the downscale ourselves against leaving it to
    // the library WHILE KEEPING the 50px border, which is the half of the change that loses: with
    // the geometry exact, the border is the worst value in the sweep rather than the best. See
    // .ai/realtime-dialogue/ocr-detector-geometry.md for the corpus numbers and for what was
    // measured and turned down (the library's v6/PythonCompat presets, stride rounding alone,
    // re-tuning StripFraction, painting the alignment strip white).
    private const int DetectorAlignment = 32;

    /// <summary>
    /// Border to surround the detector input with. Zero: the geometry is exact, so there is none.
    /// </summary>
    /// <remarks>
    /// The library's own default is 50, and a sweep of 0–96 once made it look like a peak with both
    /// neighbours worse. That sweep could not have said otherwise: the border is an input to
    /// <see cref="AlignedLength"/>, so changing it changed the aligned dimensions and therefore the
    /// squash, and what it was really ranking was which border happened to land on the least
    /// distorted geometry. <see cref="DetectorPaddingOverride"/>'s note says as much in passing.
    ///
    /// Re-swept once the geometry is exact and the border is finally an independent variable, on
    /// the six ja-game frames that lose their leading word: 0 and 8 recover all six, 16 recovers
    /// two, 24 three, 32 two, 50 none, 64 one. Shipped recovered none. On the subtitle corpora 0
    /// and 8 are within noise of each other in F1 and both beat 50, and 0 is 10% faster — which is
    /// why this was 0 first.
    ///
    /// 8 rather than 0 because those six frames were the wrong tiebreak. A user framing a dialogue
    /// BOX rather than the text in it is the case that opened all of this, and swept properly —
    /// the same game screen, the selection nudged over a 45-point grid of +-8px in each axis, which
    /// is the amount a hand-drawn box moves — the two are not close:
    ///
    /// <code>
    ///   border                 0      8
    ///   leading word read   22/45  44/45
    /// </code>
    ///
    /// It holds at every detector size (0.85x, 1.0x, 1.15x, 1.3x of the fraction: 30/45, 22/45,
    /// 14/45, 15/45 at zero against 45, 44, 43, 43 at eight), so it is the border and not an
    /// interaction with the scale. 0 loses nothing measurable on the corpora and everything on the
    /// one workload the fix exists for; the six-frame tie could not see that because all six were
    /// framed tight around the text.
    /// </remarks>
    private const int DetectorPadding = 8;

    /// <summary>
    /// An ImgResize the library can never act on, so its resize stays the identity.
    /// </summary>
    /// <remarks>
    /// Not a magic large number for its own sake: Detect() takes min(ImgResize, long side), so
    /// anything above the largest input the app can hand it makes that min pick the input and the
    /// scale ratio come out exactly 1. A screen capture is at most a few thousand pixels.
    /// </remarks>
    private const int NoLibraryResize = 65536;

    /// <summary>
    /// The pixels to detect on, and how to get from a box on them back to the caller's bitmap.
    /// </summary>
    /// <remarks>
    /// The two axes carry their own ratio because the isotropic resize rounds each dimension to a
    /// whole pixel, so they differ by a fraction of a percent. Mapping with one of them would put
    /// a box a pixel or two out on tall captures, and block bounds are what the overlay sizes its
    /// font and background from.
    /// </remarks>
    internal readonly struct DetectorFrame : IDisposable
    {
        internal required SKBitmap Bitmap { get; init; }
        internal required RapidOcrOptions Options { get; init; }
        internal required double RatioX { get; init; }
        internal required double RatioY { get; init; }

        // Null-tolerant because a struct can always be default-constructed, and a measurement that
        // holds one in a variable it does not always fill should not blow up on the way out.
        public void Dispose() => Bitmap?.Dispose();

        /// <summary>Rewrites detector-space points into the caller's coordinates, in place.</summary>
        internal void MapToSource(SKPointI[] points)
        {
            if (RatioX >= 1.0 && RatioY >= 1.0)
                return;

            for (var i = 0; i < points.Length; i++)
            {
                points[i] = new SKPointI(
                    (int)Math.Round(points[i].X / RatioX),
                    (int)Math.Round(points[i].Y / RatioY));
            }
        }

        internal void MapToSource(TextBlock[] blocks)
        {
            foreach (var block in blocks)
            {
                if (block.BoxPoints is { Length: > 0 } points)
                    MapToSource(points);
            }
        }

        internal System.Windows.Rect ToSourceBounds(SKPointI[] detectorSpacePoints)
        {
            var points = (SKPointI[])detectorSpacePoints.Clone();
            MapToSource(points);

            var xs = points.Select(point => point.X).ToList();
            var ys = points.Select(point => point.Y).ToList();
            return new System.Windows.Rect(
                xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min());
        }
    }

    /// <summary>
    /// Builds the detector's input: the isotropic downscale the caller asked for, aligned to the
    /// detector's stride, with the library told not to resize it again.
    /// </summary>
    /// <param name="maxDetectSize">
    /// Longest side to give the detector, or null for the screenshot default — as
    /// <see cref="CreateOptions"/>. It is honoured exactly here, which is the point: it used to be
    /// a request the library rounded down twice, by a different amount on each axis.
    /// </param>
    internal static DetectorFrame CreateDetectorFrame(SKBitmap source, int? maxDetectSize)
    {
        var requested = CreateOptions(maxDetectSize);
        var longest = Math.Max(source.Width, source.Height);

        // ImgResize only ever downscales, so a request at or above the input is no request at all.
        var ratio = requested.ImgResize <= 0 || requested.ImgResize >= longest
            ? 1.0
            : (double)requested.ImgResize / longest;

        var scaled = ratio >= 1.0 ? null : Downscale(source, ratio);
        try
        {
            var body = scaled ?? source;
            return new DetectorFrame
            {
                Bitmap = AlignForDetector(body, requested.Padding),
                Options = requested with { ImgResize = NoLibraryResize },
                RatioX = (double)body.Width / source.Width,
                RatioY = (double)body.Height / source.Height,
            };
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    // Mitchell because that is what the library resizes with (OcrUtils' NetworkSampling), so
    // moving the resize out here changes where it happens and not how the glyphs come out.
    private static SKBitmap Downscale(SKBitmap source, double ratio)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * ratio));
        var height = Math.Max(1, (int)Math.Round(source.Height * ratio));
        var scaled = new SKBitmap(width, height, source.ColorType, source.AlphaType);

        using (var canvas = new SKCanvas(scaled))
        {
            canvas.Clear(SKColors.Transparent);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(
                image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        return scaled;
    }

    internal static SKBitmap AlignForDetector(SKBitmap src, int detectPadding)
    {
        var aligned = new SKBitmap(
            AlignedLength(src.Width, detectPadding),
            AlignedLength(src.Height, detectPadding),
            src.ColorType,
            src.AlphaType);

        using (var canvas = new SKCanvas(aligned))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(src, 0, 0);
        }

        return aligned;
    }

    // Smallest length >= the original for which length + Detect()'s own padding is a multiple of
    // DetectorAlignment.
    private static int AlignedLength(int length, int detectPadding)
    {
        var overshoot = (length + 2 * detectPadding) % DetectorAlignment;
        return overshoot == 0 ? length : length + (DetectorAlignment - overshoot);
    }

    /// <summary>
    /// Hands the captured pixels to Skia by copying them straight across.
    /// </summary>
    /// <remarks>
    /// This used to encode the bitmap to PNG and decode it again, which is a lot of work to move
    /// pixels between two in-memory buffers — compression and decompression, entirely discarded.
    /// A screenshot pays it once and nobody notices; realtime translation pays it on every pass of
    /// every watched region, several times a second, for as long as the session runs.
    ///
    /// The format is not a free choice: it is whatever the decoder used to hand back, because
    /// everything downstream was tuned against that. Measured against the old path on four capture
    /// sizes, Bgra8888/Premul reproduces its output pixel for pixel — Format32bppArgb is already
    /// BGRA in memory, and a screen grab's alpha is 255, which premultiplied leaves untouched.
    /// Declaring the surface opaque instead is the tempting simplification and a real bug: it also
    /// changes what <see cref="AlignForDetector"/>'s transparent fill means, turning the padding
    /// from clear to black, which moved recognised text at the edges of every size tested.
    /// </remarks>
    internal static SKBitmap ConvertToSkBitmap(Bitmap bitmap)
    {
        var source = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            // Format32bppArgb is BGRA in memory, which is Bgra8888 here — so the copy is a copy and
            // never a per-pixel conversion.
            var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            var skBitmap = new SKBitmap(info);

            var destination = skBitmap.GetPixels();
            if (destination == IntPtr.Zero)
            {
                skBitmap.Dispose();
                throw new InvalidOperationException(LocalizationService.Get("S.Error.OcrImageConvert"));
            }

            // Row by row rather than one block: GDI+ and Skia pad their rows independently, so the
            // two strides agree only by coincidence.
            var rowBytes = bitmap.Width * 4;
            var row = new byte[rowBytes];
            for (int y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(source.Scan0 + y * source.Stride, row, 0, rowBytes);
                Marshal.Copy(row, 0, destination + y * skBitmap.RowBytes, rowBytes);
            }

            return skBitmap;
        }
        finally
        {
            bitmap.UnlockBits(source);
        }
    }

    private static void EnsureModelFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                LocalizationService.Format("S.Error.OcrModelMissing", path), path);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _idleReleaseTimer.Dispose();
            _current?.Dispose();
            _current = null;
            _currentModelKey = null;
        }

        // Outside _sync: a waiter released by disposal must not need the lock we are holding.
        _inferenceGate.Dispose();
    }

    internal sealed record RapidOcrRuntime(string ModelName, RapidOcr Engine) : IDisposable
    {
        public void Dispose() => Engine.Dispose();
    }
}
