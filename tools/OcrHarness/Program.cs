using System.Drawing;
using System.IO;
using GTranslate.Translators;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Providers;
using OverTranslate.Services.Realtime;

// OCR + grouping (+ Microsoft translate) harness.
// Usage: OcrHarness <imagePath> [imagePath...]   (PNG/JPG screenshots)
// Prints the grouped blocks that would be sent to translation, then the EN->ZH-HANT result.

// Everything this prints is the text off a capture, which is Japanese, Korean or Chinese as often
// as not. Left alone, a redirected run encodes it in the console's code page and every CJK
// character lands in the file as "?" — so a run kept for comparison holds the geometry and none of
// the text it belongs to, which is the half that says whether a verdict was right.
Console.OutputEncoding = System.Text.Encoding.UTF8;

if (args.Length > 0 && args[0] == "--panel-layout") return PanelLayoutProbe.Run(args[1..]);

if (args.Length > 0 && args[0] == "--session-equivalence")
    return SessionEquivalenceProbe.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--box-repair-rows")
    return BoxRepairRowProbe.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--repair-cost")
    return RepairCostProbe.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--realtime-repair")
    return RealtimeRepairProbe.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--realtime-row-gaps")
    return await RealtimeRowGapProbe.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--dialogue-probe")
    return await DialogueProbe.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--leading-edge-probe")
    return await LeadingEdgeProbe.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--leading-edge-controls")
    return await LeadingEdgeProbe.Run(args.Skip(1).ToArray(), controls: true);

if (args.Length > 0 && args[0] == "--leading-edge-fusion")
    return LeadingEdgeFusionProbe.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--ocr-root-probe")
    return OcrRootProbe.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--detector-geometry")
    return DetectorGeometryProbe.Run(args.Skip(1).ToArray());

if (args.Length > 0 && args[0] == "--group-prototype")
    return await GroupingPrototype.Run(args.Skip(1).ToArray());

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: OcrHarness <image.png> [more.png ...]");
    Console.Error.WriteLine("       OcrHarness --xlate-line <text>   (translate one line, all engines)");
    Console.Error.WriteLine("       OcrHarness --compare-models <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same frame and size, cjk vs korean recognition model)");
    Console.Error.WriteLine("       OcrHarness --scale-sweep <image.png> [...]");
    Console.Error.WriteLine("                  (reads each frame at every detector size, no translation)");
    Console.Error.WriteLine("       OcrHarness --margin-series <wholescreen.png> [more.png ...]");
    Console.Error.WriteLine("                  (whole screens: the same subtitle framed at a range of margins)");
    Console.Error.WriteLine("       OcrHarness --thresh-sweep <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same frame, detector box thresholds moved one axis at a time)");
    Console.Error.WriteLine("       OcrHarness --pad-sweep <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same frame and size, a range of borders around it)");
    Console.Error.WriteLine("       OcrHarness --margin-sweep <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (same text, blocks cropped tight around it vs left loose)");
    Console.Error.WriteLine("       OcrHarness --margin-scale-grid <wholescreen.png> [more.png ...]");
    Console.Error.WriteLine("                  (CSV: the same subtitle at several margins, each read at every scale)");
    Console.Error.WriteLine("       OcrHarness --group-explain [--interface] <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (every same-line and next-line verdict with the geometry it judged on)");
    Console.Error.WriteLine("                  (screenshot flow by default; --realtime for the live one)");
    Console.Error.WriteLine("                  (一般 mode by default, as the app is; --interface for the other one)");
    Console.Error.WriteLine("                  (--trace adds the estimate and identity diagnostics; the existing lines do not move)");
    Console.Error.WriteLine("       OcrHarness --estimate-precision <boxes.txt>");
    Console.Error.WriteLine("                  (script W H glyphs per line -> the same estimate at full precision, no OCR)");
    Console.Error.WriteLine("       OcrHarness --glyph-samples <outDir>");
    Console.Error.WriteLine("                  (draws the known-text comparison set; layout box and ink box, no OCR)");
    Console.Error.WriteLine("       OcrHarness --glyph-samples-ocr <outDir>");
    Console.Error.WriteLine("                  (reads those sheets back; detection boxes, and what was missed/split/merged)");
    Console.Error.WriteLine("       OcrHarness --vertical-explain <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (the vertical pipeline's columns and their text, which --group-explain never runs)");
    Console.Error.WriteLine("       OcrHarness --reject-audit <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (what the confidence filter drops, and what a line would have reclaimed)");
    Console.Error.WriteLine("       OcrHarness --repair-cost [--panel] <lang> <image.png> [...]");
    Console.Error.WriteLine("                  (what the row repair costs per frame, split by whether the pre-filter let it in)");
    Console.Error.WriteLine("       OcrHarness --realtime-repair [--panel] <lang> <image.png> [...]");
    Console.Error.WriteLine("                  (realtime detector size, read with and without the screenshot flow's row repair)");
    Console.Error.WriteLine("       OcrHarness --realtime-row-gaps [--panel] <lang> <image.png> [...]");
    Console.Error.WriteLine("                  (realtime-sized rows: the pieces, the gaps, and what the whole row reads as)");
    Console.Error.WriteLine("       OcrHarness --box-repair-rows <lang> <image.png> [more.png ...]");
    Console.Error.WriteLine("                  (what ChromaticBoxRepair measures on each row, and what it repaired)");
    Console.Error.WriteLine("       OcrHarness --xlate-test   (network translation/resilience check, no OCR)");
    Console.Error.WriteLine("       (add --panel / --lang KO / --size N / --det model.onnx:half to any sweep)");
    return 1;
}

// Which mode to read as. The application asks the user this (see RealtimeBlockMode) because the
// answer is about what is on screen and not about the picture's shape; offline there is nobody to
// ask, so it is a flag. Subtitle by default: every dump kept so far is one, and reading a subtitle
// dump as a panel would report the wrong half of RealtimeDetectorSize.
var harnessMode = args.Contains("--panel") ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
args = [.. args.Where(argument => argument != "--panel")];

// WHICH FLOW --group-explain IS ASKED ABOUT, and it has to be asked because grouping serves two of
// them at two different detector sizes:
//
//   截圖翻譯  OcrService.RecognizeAsync    ImgResize = 2048, which below 2048 is no downscale at all
//   即時翻譯  OcrService.TryRecognizeAsync RealtimeDetectorSize.For(w, h, mode), which downscales
//
// That is not a detail. The detector's boxes are not stable across input scales — measured on the
// old branch's corpus, reading the same 22 captures at the realtime sizes instead of the screenshot
// one moves 37 of 78 multi-line groups and invents 34 others — so a verdict read at the wrong size
// is a verdict about a layout the flow under test never sees. Measured again here on one framed
// nav bar, the boundary is sharp to the pixel: a 2040px-wide frame comes back with three UI labels
// fused into two boxes and a 2048px one with every label separate, the pixels being identical.
//
// This mode was written for the screenshot flow (the corpus is framed selections, and 截圖翻譯 is
// what the grouping work serves), but it took RealtimeDetectorSize from --reject-audit next door,
// where it is right: that mode is about a filter which runs on the realtime path only. So the
// default moves to the screenshot flow and --realtime asks for the other, which --panel implies —
// mode is a realtime concept and naming one is how you say which half of RealtimeDetectorSize.
//
// BASELINES TAKEN BEFORE THIS FLAG WERE REALTIME-SIZED. Anything compared against them has to be
// regenerated rather than read across. --vertical-explain never had the problem: it goes through
// OcrService.RecognizeVerticalAsync, which is the screenshot entry point already.
var harnessRealtime = args.Contains("--realtime") || args.Contains("--panel");

// Which capture mode the screenshot flow is asked about. The app's toolbar is what sets this, so
// without a flag every run reproduces what a user gets before touching anything — which since the
// v2 swap means General, the mode that is now the default.
//
// --comic is kept as a spelling of --general: a dozen saved corpus runs and the reports written
// around them name it, and a flag that stops working is a flag that makes those unreproducible.
var harnessLayoutMode = args.Contains("--interface")
    ? CaptureLayoutMode.Interface
    : CaptureLayoutMode.General;
args = [.. args.Where(argument =>
    argument is not ("--interface" or "--general" or "--comic"))];
args = [.. args.Where(argument => argument != "--realtime")];

// The diagnostics --group-explain cannot be read without: which line is which, and which of the
// three paths through the glyph height estimate each of them took.
//
// Off by default, and additive when on: every line the mode printed before prints unchanged, and
// everything this adds starts with "TRACE" or "  trace" so a traced run can be reduced to an
// untraced one with a grep. Every corpus comparison on this branch is a diff of those lines, and a
// diagnostic that moves them is a diagnostic that invalidates the comparisons it was added to make.
var harnessTrace = args.Contains("--trace");
args = [.. args.Where(argument => argument != "--trace")];

// The thresholds the ROI sweeps group on, written out here rather than borrowed from the product.
//
// Those sweeps are about the detector — where the box lands, how stable it is across scales, what a
// full frame costs — and they group only so that a "groups sent to translation" figure can sit
// beside the detection numbers as a coarse sanity check. Grouping is not what they are measuring.
//
// They used to quote the interface mode's profile, which was fine while nobody moved it. The step
// that tightens that mode is what makes it not fine: a sweep run before it and a sweep run after it
// would differ for a reason that has nothing to do with the detector, and neither run says which.
// A diagnostic tool's baseline belongs in the diagnostic tool.
//
// Not GroupingProfile.Vertical either, though its figures happen to match today: the name would say
// these sweeps read vertical text, and ten minutes of somebody's confusion is a worse price than
// two literals. Positional, so that a profile growing a field stops compiling here and somebody has
// to decide what this baseline should say about it.
//
// The third figure arrived when the general mode was allowed to relax the set-solid leading for a
// line long enough to have wrapped. This baseline keeps the unrelaxed one, for the same reason the
// other two are conservative: these sweeps report a group count as a sanity check beside detection
// numbers, and a sweep run before that change and one run after it must not differ for a reason
// that has nothing to do with the detector.
var harnessGroupingBaseline = new GroupingProfile(
    TightlySetMinTextSizeRatio: 0.88,
    WaiveLengthTestWhenSetSolid: false,
    SolidLineAdvanceWhenWrapped: 1.20,
    RefuseSpeakerLineStarts: false);

// Which language to read as. It picks the recognition model, and that is not a detail on a Korean
// dump: the general model carries no Hangul at all, so a Korean frame read as EN comes back as
// whatever Latin and Han the recogniser can force onto the shapes. The detector is shared, so this
// changes nothing about detection — which is the point when the thing under test is a detector.
var harnessLanguage = "EN";
var languageFlag = Array.IndexOf(args, "--lang");
if (languageFlag >= 0)
{
    if (languageFlag + 1 >= args.Length)
    {
        Console.Error.WriteLine("usage: --lang <EN|JA|KO|AUTO|...>");
        return 1;
    }

    harnessLanguage = args[languageFlag + 1].ToUpperInvariant();
    args = [.. args.Take(languageFlag), .. args.Skip(languageFlag + 2)];
}

// The detector size to read at, or null to let the mode decide. #71 needed this: the fault it was
// chasing only appears at the large sizes the realtime fallbacks use, and every sweep here either
// walked all the sizes or pinned one of its own, so there was no way to hold the size still and
// vary something else.
int? harnessSize = null;
var sizeFlag = Array.IndexOf(args, "--size");
if (sizeFlag >= 0)
{
    if (sizeFlag + 1 >= args.Length || !int.TryParse(args[sizeFlag + 1], out var parsedSize))
    {
        Console.Error.WriteLine("usage: --size <detector input long side in px>");
        return 1;
    }

    harnessSize = parsedSize;
    args = [.. args.Take(sizeFlag), .. args.Skip(sizeFlag + 2)];
}

// Which detector to read with. Without it every mode uses the shipped one, so the flag's absence
// reproduces the shipped numbers and its presence is the only thing that differs.
var detectorFlag = Array.IndexOf(args, "--det");
if (detectorFlag >= 0)
{
    if (detectorFlag + 1 >= args.Length)
    {
        Console.Error.WriteLine("usage: --det <det.onnx>[:imagenet|:half]");
        return 1;
    }

    var spec = args[detectorFlag + 1];
    args = [.. args.Take(detectorFlag), .. args.Skip(detectorFlag + 2)];

    // The normalisation travels with the model, so it is named next to it rather than left to a
    // default: a v6 detector read with v5's statistics is a different, worse detector, and the
    // mistake is invisible in the output — it just reads less.
    var separator = spec.LastIndexOf(':');
    var normalization = separator > 1 ? spec[(separator + 1)..] : "imagenet";
    var detPath = separator > 1 ? spec[..separator] : spec;

    OnnxOcrEngine.DetectorOverride = normalization switch
    {
        "imagenet" => new OnnxOcrEngine.DetectorModel(
            detPath, OnnxOcrEngine.ImageNetNormalization, OnnxOcrEngine.ImageNetNormalizationStd),
        "half" => new OnnxOcrEngine.DetectorModel(
            detPath, OnnxOcrEngine.HalfNormalization, OnnxOcrEngine.HalfNormalization),
        _ => null,
    };

    if (OnnxOcrEngine.DetectorOverride is null)
    {
        Console.Error.WriteLine($"unknown normalization \"{normalization}\" (expected imagenet or half)");
        return 1;
    }

    Console.WriteLine($"detector: {detPath}  normalization={normalization}");
}

// Forced-fallback check: the primary engine gets a 1ms-timeout HttpClient so it always fails,
// proving the hedge falls back to a backup engine and that the badge data (FallbackUsed/Dominant)
// is computed correctly. No OCR / screenshot needed.
if (args[0] == "--fallback-test")
{
    var samples = new List<OcrTextBlock>
    {
        new("Settings have been saved successfully.", new System.Windows.Rect(0, 0, 100, 20)),
        new("Please restart the application.",        new System.Windows.Rect(0, 0, 100, 20)),
    };

    var brokenHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(1) };
    var resilient = new ResilientProvider(
        [
            new GTranslateProvider(new MicrosoftTranslator(brokenHttp)), // primary: always times out
            new GTranslateProvider(new GoogleTranslator2()),             // backup
            new GTranslateProvider(new BingTranslator()),                // backup
        ],
        hedgeDelay: TimeSpan.FromMilliseconds(200));

    var (translated, _) = await resilient.TranslateAsync(samples, "EN", "ZH-HANT", "");
    var u = resilient.LastUsage!;
    Console.WriteLine($"FallbackUsed = {u.FallbackUsed}  (expected True)");
    Console.WriteLine($"Primary      = {u.Primary}       (expected: Microsoft)");
    Console.WriteLine($"BackupEngine = {u.BackupEngine}  (expected: a backup, not Microsoft)");
    Console.WriteLine($"Summary      = {u.Summary}");
    Console.WriteLine($"Badge would show: ⚡備援 {u.BackupEngine}");
    foreach (var t in translated)
        Console.WriteLine($"  ZH: {t.TranslatedText}");
    return 0;
}

// Translate one line given on the command line. For telling an OCR problem from a translation
// one: when a word goes missing on screen, this says whether the recogniser dropped it or the
// translator did.
if (args[0] == "--xlate-line")
{
    var line = string.Join(' ', args.Skip(1));
    if (line.Length == 0)
    {
        Console.Error.WriteLine("usage: OcrHarness --xlate-line <text to translate>");
        return 1;
    }

    // All four, named apart. "Google" is two different endpoints and they need not behave alike —
    // the resilient chains use GoogleTranslator2 as a backup and GoogleTranslator as a primary, so
    // a limit measured on one says nothing about the other.
    //
    // --raw hands each engine the whole text, bypassing TranslationRequestChunks. That is what
    // reproduces the fault the chunking exists to prevent, so it stays available: without it the
    // only way to see an endpoint's real behaviour past its limit is to delete the fix.
    var raw = args.Contains("--raw");
    var line2 = raw ? string.Join(' ', args.Skip(1).Where(a => a != "--raw")) : line;
    var limit = raw ? int.MaxValue : (int?)null;

    var block = new List<OcrTextBlock> { new(line2, new System.Windows.Rect(0, 0, 100, 20)) };
    Console.WriteLine($"  input: {line2.Length} chars, chunking {(raw ? "OFF" : "ON")}");

    foreach (var (name, provider) in new (string, GTranslateProvider)[]
             {
                 ("Microsoft", new GTranslateProvider(new MicrosoftTranslator(), null, limit)),
                 ("Google Web", new GTranslateProvider(new GoogleTranslator(), null, limit)),
                 ("Google RPC", new GTranslateProvider(new GoogleTranslator2(), null, limit)),
                 ("Bing      ", new GTranslateProvider(new BingTranslator(), null, limit)),
             })
    {
        try
        {
            var (result, _) = await provider.TranslateAsync(block, "EN", "ZH-HANT", "");
            Console.WriteLine($"  [{name}] {result[0].TranslatedText}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [{name}] FAILED: {ex.Message}");
        }
    }

    // The same text through the chain a user who picked Google actually gets: the engines above
    // answer for themselves, this answers for the application. Which one served the block is
    // printed with it, because "Google looped" and "Google was slow so Bing answered" produce the
    // same good line and are not the same result — the summary is what tells them apart.
    var googleChain = new ResilientProvider([
        new GTranslateProvider(new GoogleTranslator(), null, limit),
        new GTranslateProvider(new GoogleTranslator2(), null, limit),
        new GTranslateProvider(new BingTranslator(), null, limit),
    ]);

    var (chained, _) = await googleChain.TranslateAsync(block, "EN", "ZH-HANT", "");
    Console.WriteLine($"  [Google chain] {chained[0].TranslatedText}");
    Console.WriteLine($"                 served by {googleChain.LastBatchSummary}");

    return 0;
}

// Translation-only resilience check: exercises ResilientProvider over the live free endpoints
// and reports per-run latency. No OCR / screenshot needed.
if (args[0] == "--xlate-test")
{
    var samples = new List<OcrTextBlock>
    {
        new("The quick brown fox jumps over the lazy dog.", new System.Windows.Rect(0, 0, 100, 20)),
        new("Settings have been saved successfully.",        new System.Windows.Rect(0, 0, 100, 20)),
        new("Please restart the application to apply changes.", new System.Windows.Rect(0, 0, 100, 20)),
        new("Translation speed should now be more consistent.", new System.Windows.Rect(0, 0, 100, 20)),
    };

    var resilient = new ResilientProvider([
        new GTranslateProvider(new GoogleTranslator2()),
        new GTranslateProvider(new BingTranslator()),
        new GTranslateProvider(new MicrosoftTranslator()),
    ]);

    for (var run = 1; run <= 3; run++)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (translated, detected) = await resilient.TranslateAsync(samples, "EN", "ZH-HANT", "");
        sw.Stop();
        Console.WriteLine($"--- run {run}: {sw.ElapsedMilliseconds} ms (detected={detected}) ---");
        Console.WriteLine($"  實際使用引擎: {resilient.LastBatchSummary}");
        foreach (var t in translated)
            Console.WriteLine($"  EN: {t.OriginalText}\n  ZH: {t.TranslatedText}");
        Console.WriteLine();
    }
    return 0;
}

// Model comparison: the same frame, the same detector size, only the recognition model changed.
// "EN" routes to the general cjk model and "KO" to the korean one (see GetModelKeyForLanguage),
// which makes korean a natural control: both are large-dictionary models, and the only structural
// difference is that korean holds no Han characters at all. If Latin reads better under it, the
// 15,702 Han characters competing for every glyph are the problem, and a Latin-only dictionary
// would help more still.
if (args[0] == "--compare-models")
{
    using var cmpOcr = new OcrService();

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var native = Math.Max(image.Width, image.Height);
        var (primary, _) = RealtimeDetectorSize.For(image.Width, image.Height, harnessMode);

        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"{Path.GetFileName(path)}  {image.Width}x{image.Height}");

        // Both sizes, because the model is not the only thing that decides whether a line is read:
        // comparing at one size risks reporting a detector result as a recognition difference.
        foreach (var size in new[] { primary, native }.Distinct())
        {
            Console.WriteLine($"  --- detect={size}{(size == primary ? " (realtime primary)" : " (native)")} ---");

            foreach (var (label, lang) in new[] { ("cjk   ", "EN"), ("korean", "KO") })
            {
                List<OcrTextBlock>? blocks = null;
                for (var attempt = 0; attempt < 20 && blocks is null; attempt++)
                    blocks = await cmpOcr.TryRecognizeAsync(image, lang, size);

                if (blocks is null || blocks.Count == 0)
                {
                    Console.WriteLine($"    [{label}] (nothing read)");
                    continue;
                }

                foreach (var b in blocks)
                {
                    var confidence = b.Confidence is { } c ? $"{c:0.00}" : "  - ";
                    Console.WriteLine($"    [{label}] score={confidence}  \"{b.Text.Replace("\n", " ")}\"");
                }
            }
        }
    }

    return 0;
}

// Export: whole screens in, one crop per subtitle out, for use as a fixture set.
//
// Comparing two detectors needs frames neither of them chose. A region dump is not that — it was
// kept because the shipped detector did or did not read it — and a whole screen is not either, since
// most of it is not the text under test. Locating the subtitle once, with the shipped detector, and
// writing that crop out gives every later run the same input: the location is decided by one model
// but the crop is then fixed, so no candidate is being scored on a frame chosen to suit it.
//
// The margin is the one --margin-series measured as best over 27 frames, not a round number.
if (args[0] == "--export-subtitle")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("usage: --export-subtitle <outputDir> <wholescreen.png> [more.png ...]");
        return 1;
    }

    const double SubtitleBandTop = 0.62;
    const int ExportMargin = 60;

    var outputDir = args[1];
    System.IO.Directory.CreateDirectory(outputDir);
    using var exportOcr = new OcrService();
    var written = 0;

    foreach (var path in args.Skip(2))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var name = Path.GetFileNameWithoutExtension(path);

        var whole = await exportOcr.TryRecognizeAsync(image, "AUTO") ?? [];
        var band = whole
            .Where(b => b.Bounds.Y + b.Bounds.Height / 2 > image.Height * SubtitleBandTop)
            .ToList();
        if (band.Count == 0) { Console.WriteLine($"{name}: no subtitle found, skipped"); continue; }

        var anchor = band.MaxBy(b => b.Bounds.Width)!.Bounds;
        var union = System.Windows.Rect.Empty;
        foreach (var block in band)
        {
            var bounds = block.Bounds;
            var overlap = Math.Min(bounds.Right, anchor.Right) - Math.Max(bounds.Left, anchor.Left);
            if (Math.Abs(bounds.Y - anchor.Y) < anchor.Height * 2.5 &&
                overlap > Math.Min(bounds.Width, anchor.Width) * 0.5)
                union.Union(bounds);
        }

        var x = (int)Math.Max(0, union.X - ExportMargin);
        var y = (int)Math.Max(0, union.Y - ExportMargin);
        var w = (int)Math.Min(image.Width - x, union.Width + ExportMargin * 2);
        var h = (int)Math.Min(image.Height - y, union.Height + ExportMargin * 2);
        if (w <= 1 || h <= 1) { Console.WriteLine($"{name}: degenerate crop, skipped"); continue; }

        using var crop = image.Clone(new Rectangle(x, y, w, h), image.PixelFormat);
        var target = Path.Combine(outputDir, $"{name}-sub.png");
        crop.Save(target, System.Drawing.Imaging.ImageFormat.Png);
        written++;
        Console.WriteLine($"{name}: {w}x{h} -> {Path.GetFileName(target)}");
    }

    Console.WriteLine($"{written} crops written to {outputDir}");
    return 0;
}

// Margin series: one whole screen, cropped around its subtitle at a range of margins, read at each.
//
// The question is what the user's own framing costs. Every other sweep here starts from a region
// somebody already drew and can only change what is done to it; this one starts from the picture the
// region was drawn on, so the framing itself is the variable. It needs whole screens rather than
// region dumps for that reason — a dump has already had its margin chosen.
//
// The subtitle is located rather than given: reading a full screen finds it along with whatever else
// is on the picture, and the boxes in the bottom third of a video frame are the subtitle. The union
// of those is then re-read once with room around it, because the first pass is subject to the very
// effect being measured and its box may already be short at one end.
if (args[0] == "--margin-series")
{
    using var marginOcr = new OcrService();

    // Where a subtitle lives, as a fraction of the frame's height. Deliberately generous: a two-line
    // subtitle on a 16:9 frame starts around 0.82, and a frame with the line higher up is better
    // skipped by the character floor below than caught by a boundary drawn tightly here.
    const double SubtitleBandTop = 0.62;

    // Frames whose best reading is shorter than this are not carrying a subtitle to measure, and a
    // series over one of them reports noise moving around rather than framing.
    const int MinChars = 8;

    int[] margins = [0, 10, 20, 30, 40, 60, 80, 120];
    var totals = new int[margins.Length];
    var perfect = new int[margins.Length];
    var measured = 0;

    async Task<List<OcrTextBlock>> ReadAsync(Bitmap source, System.Windows.Rect area)
    {
        var x = (int)Math.Max(0, area.X);
        var y = (int)Math.Max(0, area.Y);
        var w = (int)Math.Min(source.Width - x, area.Width);
        var h = (int)Math.Min(source.Height - y, area.Height);
        if (w <= 1 || h <= 1) return [];

        using var crop = source.Clone(new Rectangle(x, y, w, h), source.PixelFormat);
        return await marginOcr.TryRecognizeAsync(crop, "AUTO") ?? [];
    }

    static System.Windows.Rect Union(IEnumerable<OcrTextBlock> blocks, double offsetX, double offsetY)
    {
        var union = System.Windows.Rect.Empty;
        foreach (var block in blocks)
        {
            var bounds = block.Bounds;
            bounds.Offset(offsetX, offsetY);
            union.Union(bounds);
        }

        return union;
    }

    static System.Windows.Rect Grow(System.Windows.Rect area, int margin) => new(
        area.X - margin, area.Y - margin, area.Width + margin * 2, area.Height + margin * 2);

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var name = Path.GetFileName(path);

        // Pass one: the whole screen, keeping only what sits in the subtitle band.
        var whole = await ReadAsync(image, new System.Windows.Rect(0, 0, image.Width, image.Height));
        var band = whole.Where(b => b.Bounds.Y + b.Bounds.Height / 2 > image.Height * SubtitleBandTop).ToList();
        if (band.Count == 0) { Console.WriteLine($"{name}: no subtitle found, skipped"); continue; }

        // The widest line in the band and whatever else belongs to the same subtitle — the second
        // line of a two-line one — and nothing else. The union of the whole band would swallow the
        // interface along the bottom of a game screen, and then the series would be measuring how
        // well a row of buttons reads rather than the subtitle it was pointed at.
        var anchor = band.MaxBy(b => b.Bounds.Width)!.Bounds;
        band = band.Where(b =>
        {
            var bounds = b.Bounds;
            var overlap = Math.Min(bounds.Right, anchor.Right) - Math.Max(bounds.Left, anchor.Left);
            var near = Math.Abs(bounds.Y - anchor.Y) < anchor.Height * 2.5;
            return near && overlap > Math.Min(bounds.Width, anchor.Width) * 0.5;
        }).ToList();

        // Pass two: the same text with room around it, which is where this box is most likely to be
        // the whole line rather than part of one.
        var rough = Grow(Union(band, 0, 0), 60);
        var refinedRead = await ReadAsync(image, rough);
        var refined = refinedRead.Count > 0 ? Union(refinedRead, rough.X, rough.Y) : Union(band, 0, 0);

        var readings = new string[margins.Length];
        var chars = new int[margins.Length];
        for (var index = 0; index < margins.Length; index++)
        {
            var blocks = await ReadAsync(image, Grow(refined, margins[index]));
            readings[index] = string.Join(" | ", blocks.Select(b => b.Text.Replace("\n", " ")));
            chars[index] = blocks.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c)));
        }

        var best = chars.Max();
        if (best < MinChars) { Console.WriteLine($"{name}: nothing worth measuring, skipped"); continue; }

        measured++;
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"{name}  {image.Width}x{image.Height}  best={best} chars");
        for (var index = 0; index < margins.Length; index++)
        {
            totals[index] += chars[index];
            if (chars[index] == best) perfect[index]++;

            Console.WriteLine(
                $"  +{margins[index],3}px : {chars[index],3}/{best,-3} {readings[index]}");
        }
    }

    if (measured > 0)
    {
        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"SUMMARY over {measured} frames");
        var bestTotal = totals.Max();
        for (var index = 0; index < margins.Length; index++)
            Console.WriteLine(
                $"  +{margins[index],3}px : {totals[index],5} chars ({totals[index] * 100.0 / bestTotal,5:0.0}% of best)  " +
                $"read best on {perfect[index],3}/{measured} frames");
    }

    return 0;
}

// The grid issue #89 needs: the same text, at several margins, each read at every detector scale.
//
// --margin-series varies the margin at one size; --scale-sweep varies the size at one margin. Neither
// can separate the variables, and the corpus cannot either — every region dump is about 1820 wide, so
// "fraction" and "absolute detector size" move together in it. Cropping a whole screen at a range of
// margins breaks that tie: the same glyphs, the same fraction, a different absolute size.
//
// What it is for: with a fixed fraction the glyph's height in DETECTOR space is (region glyph height x
// fraction), which does not depend on how the user framed — measured on two real blocks in #39, a
// tight 1435x146 and a loose 1894x269 both put a 55px glyph at ~28px. So glyph scale cannot be what
// makes a tight crop read 88-95% at every fraction while the same frames untrimmed peak at 0.85 and
// fall to 69% at 0.40. Something else moves, and each row here carries the candidates side by side so
// the answer comes from the data rather than from a story about it.
//
// Emitted as CSV on purpose. The other sweeps pad their columns to line up, which is fine to read and
// treacherous to parse — a right-aligned "chars= 78" silently dropped the lowest-scoring rows out of
// an analysis during #84, and those are exactly the rows that matter.
if (args[0] == "--margin-scale-grid")
{
    using var gridOcr = new OcrService();

    const double SubtitleBandTop = 0.62;
    const int MinChars = 8;
    int[] gridMargins = [8, 24, 60, 120, 200];

    System.Windows.Rect Clamp(System.Windows.Rect area, Bitmap source)
    {
        var x = Math.Max(0, area.X);
        var y = Math.Max(0, area.Y);
        return new System.Windows.Rect(
            x, y,
            Math.Min(source.Width - x, area.Width),
            Math.Min(source.Height - y, area.Height));
    }

    async Task<List<OcrTextBlock>> ReadCropAsync(Bitmap source, System.Windows.Rect area, int? size)
    {
        var box = Clamp(area, source);
        if (box.Width <= 1 || box.Height <= 1) return [];

        using var crop = source.Clone(
            new Rectangle((int)box.X, (int)box.Y, (int)box.Width, (int)box.Height), source.PixelFormat);
        return await gridOcr.TryRecognizeAsync(crop, harnessLanguage, size) ?? [];
    }

    static System.Windows.Rect UnionOf(IEnumerable<OcrTextBlock> blocks, double offsetX, double offsetY)
    {
        var union = System.Windows.Rect.Empty;
        foreach (var block in blocks)
        {
            var bounds = block.Bounds;
            bounds.Offset(offsetX, offsetY);
            union.Union(bounds);
        }

        return union;
    }

    static System.Windows.Rect GrowBy(System.Windows.Rect area, int margin) => new(
        area.X - margin, area.Y - margin, area.Width + margin * 2, area.Height + margin * 2);

    static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    Console.WriteLine(
        "image,margin,roiW,roiH,fraction,detect,chars,glyphRegionPx,glyphDetectorPx,occupancyPct");

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.Error.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var name = Path.GetFileName(path);

        // Locating the subtitle is --margin-series' logic, unchanged: read the whole screen, keep the
        // band low enough to be a subtitle, anchor on the widest line so a row of interface buttons
        // along the bottom does not get swallowed, then re-read with room around it because the first
        // box may already be clipped by the very effect under measurement.
        var whole = await ReadCropAsync(image, new System.Windows.Rect(0, 0, image.Width, image.Height), null);
        var band = whole
            .Where(b => b.Bounds.Y + b.Bounds.Height / 2 > image.Height * SubtitleBandTop)
            .ToList();
        if (band.Count == 0) { Console.Error.WriteLine($"{name}: no subtitle found, skipped"); continue; }

        var anchor = band.MaxBy(b => b.Bounds.Width)!.Bounds;
        band = band.Where(b =>
        {
            var bounds = b.Bounds;
            var overlap = Math.Min(bounds.Right, anchor.Right) - Math.Max(bounds.Left, anchor.Left);
            return Math.Abs(bounds.Y - anchor.Y) < anchor.Height * 2.5 &&
                   overlap > Math.Min(bounds.Width, anchor.Width) * 0.5;
        }).ToList();

        var rough = GrowBy(UnionOf(band, 0, 0), 60);
        var refinedRead = await ReadCropAsync(image, rough, null);
        var refined = refinedRead.Count > 0
            ? UnionOf(refinedRead, Math.Max(0, rough.X), Math.Max(0, rough.Y))
            : UnionOf(band, 0, 0);
        if (refined.IsEmpty) { Console.Error.WriteLine($"{name}: no text box, skipped"); continue; }

        // A frame not carrying a subtitle produces a grid of noise moving around, which averages into
        // the summary as though it were signal. Same floor and same reason as --margin-series.
        var refinedChars = refinedRead.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c)));
        if (refinedChars < MinChars)
        {
            Console.Error.WriteLine($"{name}: only {refinedChars} chars, nothing worth measuring, skipped");
            continue;
        }

        foreach (var margin in gridMargins)
        {
            var roi = Clamp(GrowBy(refined, margin), image);
            if (roi.Width <= 1 || roi.Height <= 1) continue;

            using var crop = image.Clone(
                new Rectangle((int)roi.X, (int)roi.Y, (int)roi.Width, (int)roi.Height), image.PixelFormat);
            var native = Math.Max(crop.Width, crop.Height);
            var roiArea = (double)crop.Width * crop.Height;

            for (var percent = 30; percent <= 100; percent += 5)
            {
                var fraction = percent / 100.0;
                var size = fraction >= 1.0
                    ? native
                    : Math.Max(320, ((int)(native * fraction) + 31) / 32 * 32);

                var blocks = await gridOcr.TryRecognizeAsync(crop, harnessLanguage, size) ?? [];

                // The same three drops a watched region applies, so a size counts as reading only
                // what the application would actually have shown.
                var kept = blocks.Where(block =>
                    !CollapsedDetection.IsCollapsed(block.Bounds.Height, crop.Height, block.Text) &&
                    !ShortReadingDetection.IsTooShort(block.Text) &&
                    !ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence)).ToList();

                var chars = kept.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c)));

                // RenderGlyphHeight is the ink height and is null for CJK, where the box already is
                // the glyph — taking one or the other rather than both is the #35 mistake, and it is
                // worth a factor of two on Latin.
                var glyphRegion = Median(kept.Select(b => b.RenderGlyphHeight ?? b.Bounds.Height).ToList());
                var glyphDetector = native > 0 ? glyphRegion * size / native : 0;

                var textUnion = UnionOf(kept, 0, 0);
                var occupancy = textUnion.IsEmpty || roiArea <= 0
                    ? 0
                    : textUnion.Width * textUnion.Height * 100.0 / roiArea;

                Console.WriteLine(
                    $"{name},{margin},{crop.Width},{crop.Height},{fraction:0.00},{size},{chars}," +
                    $"{glyphRegion:0.0},{glyphDetector:0.0},{occupancy:0.0}");
            }
        }

        Console.Error.WriteLine($"{name}: done");
    }

    return 0;
}

// Threshold sweep: the same frame read at the shipped detector size, with the three detector
// post-processing thresholds moved one at a time around the library's defaults.
//
// This is the one sweep whose answer cannot be read out of the application's log. rawBlocks is
// counted after the library has already dropped every box below BoxScoreThresh, so a subtitle whose
// leading characters went missing looks identical to one where the detector never found them. The
// only way to tell those apart is to move the threshold and see whether the text comes back.
//
// Reads as a screenshot (detect size null -> the shipped ScreenshotDetectSize) and in AUTO, which is
// what the failing captures were: the point is to reproduce a specific report, not to re-measure the
// realtime sizes that --scale-sweep already covers.
if (args[0] == "--thresh-sweep")
{
    using var threshOcr = new OcrService();

    // What the application actually sends, which since #71 is the model's own exported values
    // rather than the library's generic ones. The library's are kept as a row below so a sweep can
    // still show what moving away from them bought.
    var shipped = OnnxOcrEngine.ExportedThresholds;
    var libraryDefaults = new OnnxOcrEngine.DetectorThresholds(
        RapidOcrNet.RapidOcrOptions.Default.BoxThresh,
        RapidOcrNet.RapidOcrOptions.Default.BoxScoreThresh,
        RapidOcrNet.RapidOcrOptions.Default.UnClipRatio);

    Console.WriteLine(
        $"shipped: boxThresh={shipped.BoxThresh:0.00} boxScore={shipped.BoxScoreThresh:0.00} " +
        $"unclip={shipped.UnClipRatio:0.00}   library: {libraryDefaults.BoxThresh:0.00}/" +
        $"{libraryDefaults.BoxScoreThresh:0.00}/{libraryDefaults.UnClipRatio:0.00}");

    // One axis at a time, each stepping through its own range with the other two left shipped. A
    // grid would be three times the passes for an answer nobody could read: what is being asked
    // first is which of the three is even involved.
    // The values PaddlePaddle exported each v6 detector with, from the inference.yml beside the
    // model on Hugging Face. They are here as whole combinations because that is what they are: this
    // sweep otherwise moves one axis at a time and holds the other two at the library's generic
    // defaults, which cannot find a setting whose three parts only work together. #69 swept all
    // three that way and concluded none of them mattered.
    var runs = new List<(string Axis, OnnxOcrEngine.DetectorThresholds Value)>
    {
        ("shipped        ", shipped),
        ("library-default", libraryDefaults),
        ("official-sm/med", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.45f, 1.4f)),

        // Neither the library's defaults nor the export config are aimed at what goes wrong here.
        // Both move the three together in the same direction — permissive or strict — while the
        // three faults on record want opposite things: a low binarisation threshold to keep the
        // faint leading strokes a line loses, a HIGH box score to throw away the noise boxes that
        // reach the screen as enormous garbage, and a low unclip so a box does not come out taller
        // than its glyphs. PaddleOCR's own tuning guidance names those three moves separately; no
        // published preset combines them, so these are the combinations to measure.
        ("weak+strict-1.4", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.6f, 1.4f)),
        ("weak+strict-1.5", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.6f, 1.5f)),
        ("weak+mid-1.4   ", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.5f, 1.4f)),
        ("weak+strict-1.6", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.6f, 1.6f)),

        // One move at a time from what ships now, so the combinations above can be read against
        // which single change was doing the work.
        ("only-weaker    ", new OnnxOcrEngine.DetectorThresholds(0.2f, 0.5f, 1.6f)),
        ("only-tighter   ", new OnnxOcrEngine.DetectorThresholds(0.3f, 0.5f, 1.4f)),
        ("only-stricter  ", new OnnxOcrEngine.DetectorThresholds(0.3f, 0.6f, 1.6f)),
    };
    foreach (var boxThresh in new[] { 0.10f, 0.15f, 0.20f, 0.25f, 0.30f, 0.40f, 0.50f })
        runs.Add(($"boxThresh={boxThresh:0.00}", shipped with { BoxThresh = boxThresh }));
    foreach (var boxScore in new[] { 0.10f, 0.20f, 0.30f, 0.40f, 0.50f, 0.60f })
        runs.Add(($"boxScore ={boxScore:0.00}", shipped with { BoxScoreThresh = boxScore }));

    // Finer than the other two and reaching lower, because #71 needs this one to answer a question
    // about box size rather than about whether text is found: the unclip step is what inflates a
    // detected polygon back out, and a detector whose boxes come out too tall is asking to be tried
    // below the library's 1.6.
    foreach (var unclip in new[] { 0.8f, 1.0f, 1.2f, 1.4f, 1.6f, 1.8f, 2.0f, 2.5f })
        runs.Add(($"unclip   ={unclip:0.00}", shipped with { UnClipRatio = unclip }));

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        Console.WriteLine(new string('=', 78));
        Console.WriteLine(
            $"{Path.GetFileName(path)}  {image.Width}x{image.Height}  " +
            $"detect={harnessSize?.ToString() ?? "screenshot"}  lang={harnessLanguage}");

        foreach (var (axis, thresholds) in runs)
        {
            OnnxOcrEngine.DetectorThresholdOverride = thresholds;

            List<OcrTextBlock>? blocks = null;
            for (var attempt = 0; attempt < 20 && blocks is null; attempt++)
                blocks = await threshOcr.TryRecognizeAsync(image, harnessLanguage, harnessSize);

            var chars = blocks?.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c))) ?? 0;
            var text = blocks is null || blocks.Count == 0
                ? ""
                : "  " + string.Join(" | ", blocks.Select(b => b.Text.Replace("\n", " ")));

            // The tallest box, because #71 is about box shape rather than about what was read: a
            // reading that is right and a box that is twice the height of its glyphs still reaches
            // the screen as text twice the size it should be, and the character count cannot see it.
            var tallest = blocks is null || blocks.Count == 0 ? 0 : blocks.Max(b => b.Bounds.Height);

            Console.WriteLine(
                $"  {axis} : {blocks?.Count ?? -1} box chars={chars,3} tallest={tallest,3:0}{text}");
        }

        OnnxOcrEngine.DetectorThresholdOverride = null;
    }

    return 0;
}

// Padding sweep: the same frame at the size the application would really use, read with a range of
// borders around it. The border exists because text touching the edge of a capture detects badly,
// but it is not free — it counts towards the long side ImgResize caps, so a wider border shrinks
// the text the detector sees. This says whether the shipped 50 is still the right number.
if (args[0] == "--pad-sweep")
{
    using var padOcr = new OcrService();

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var (primary, _) = RealtimeDetectorSize.For(image.Width, image.Height, harnessMode);

        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"{Path.GetFileName(path)}  {image.Width}x{image.Height}  detect={primary}");

        foreach (var padding in new[] { 0, 8, 16, 24, 32, 50, 64, 96 })
        {
            OnnxOcrEngine.DetectorPaddingOverride = padding;

            List<OcrTextBlock>? blocks = null;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 0; attempt < 20 && blocks is null; attempt++)
            {
                elapsed.Restart();
                blocks = await padOcr.TryRecognizeAsync(image, "EN", primary);
            }

            elapsed.Stop();

            // The same two filters the realtime session applies, for the same reason as in the
            // scale sweep: a reading it would have thrown away is not a reading.
            var kept = blocks?
                .Where(block =>
                    !CollapsedDetection.IsCollapsed(block.Bounds.Height, image.Height, block.Text) &&
                    !ShortReadingDetection.IsTooShort(block.Text) &&
                    !ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence))
                .ToList();

            var chars = kept?.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c))) ?? 0;
            var text = kept is null || kept.Count == 0
                ? ""
                : "  " + string.Join(" | ", kept.Select(b => b.Text.Replace("\n", " ")));
            var mark = padding == 8 ? " <- shipped" : "";

            Console.WriteLine(
                $"  pad={padding,3} : {kept?.Count ?? -1} box chars={chars,3} " +
                $"{elapsed.ElapsedMilliseconds,5}ms{mark}{text}");
        }

        OnnxOcrEngine.DetectorPaddingOverride = null;
    }

    return 0;
}

// Margin sweep: the same text read inside blocks drawn tight around it and drawn loosely around it.
//
// Every other sweep here holds the block still and changes what is done to it. This one changes the
// block, because that is the one input the user chooses and the only one nobody has measured: a
// dumped frame is cropped back to the text plus a margin of so many line heights, and each crop is
// read exactly as the app would read a block of that shape — RealtimeDetectorSize picks the size
// from the cropped dimensions, and the realtime filters throw away what the session would throw
// away.
//
// It can only ever take margin away, never add it, since the pixels outside the dumped block were
// never captured. So the loosest row is the block as the user actually drew it, and the rows below
// it are that same content framed tighter.
if (args[0] == "--margin-sweep")
{
    using var marginOcr = new OcrService();

    // Fractions of a line's height to leave around the text on all four sides.
    double[] margins = [0, 0.15, 0.3, 0.5, 0.75, 1.0, 1.5];
    var totals = new double[margins.Length];
    var counted = new int[margins.Length];

    async Task<List<OcrTextBlock>> ReadAsync(Bitmap image)
    {
        var (primary, fallbacks) = RealtimeDetectorSize.For(image.Width, image.Height, harnessMode);

        foreach (var size in new[] { primary }.Concat(fallbacks))
        {
            var blocks = await marginOcr.TryRecognizeAsync(image, "EN", size);

            // The two filters the realtime session applies. A reading it would have thrown away is
            // not a reading, and the collapse filter is measured against the block's own height —
            // which is exactly what this sweep is changing.
            var kept = blocks?
                .Where(block =>
                    !CollapsedDetection.IsCollapsed(block.Bounds.Height, image.Height, block.Text) &&
                    !ShortReadingDetection.IsTooShort(block.Text) &&
                    !ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence))
                .ToList() ?? [];

            if (kept.Count > 0) return kept;
        }

        return [];
    }

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);

        // Where the text is, read from the block as drawn. A frame nothing can be read out of has
        // no text to centre a crop on, and counting it would score cropping against a frame that
        // was never readable in the first place.
        var located = await ReadAsync(image);
        if (located.Count == 0) continue;

        var lineHeight = located.Select(block => block.Bounds.Height).OrderBy(h => h).ToList()
            [located.Count / 2];
        var left = located.Min(block => block.Bounds.Left);
        var top = located.Min(block => block.Bounds.Top);
        var right = located.Max(block => block.Bounds.Right);
        var bottom = located.Max(block => block.Bounds.Bottom);

        Console.WriteLine(new string('=', 78));
        Console.WriteLine(
            $"{Path.GetFileName(path)}  {image.Width}x{image.Height}  " +
            $"line={lineHeight:0}px  text={right - left:0}x{bottom - top:0}");

        var readings = new (double Margin, string Size, int Boxes, int Chars, long Ms, string Text)[margins.Length];

        for (var i = 0; i < margins.Length; i++)
        {
            var pad = margins[i] * lineHeight;
            var crop = Rectangle.FromLTRB(
                Math.Max(0, (int)Math.Floor(left - pad)),
                Math.Max(0, (int)Math.Floor(top - pad)),
                Math.Min(image.Width, (int)Math.Ceiling(right + pad)),
                Math.Min(image.Height, (int)Math.Ceiling(bottom + pad)));

            using var cropped = image.Clone(crop, image.PixelFormat);
            var (primary, _) = RealtimeDetectorSize.For(cropped.Width, cropped.Height, harnessMode);

            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var kept = await ReadAsync(cropped);
            elapsed.Stop();

            var chars = kept.Sum(block => block.Text.Count(c => !char.IsWhiteSpace(c)));
            readings[i] = (
                margins[i],
                $"{cropped.Width}x{cropped.Height} d={primary}",
                kept.Count,
                chars,
                elapsed.ElapsedMilliseconds,
                string.Join(" | ", kept.Select(block => block.Text.Replace("\n", " "))));
        }

        // Scored against the frame's own best reading rather than against ground truth, the same way
        // the pad sweep is: what is being compared is one framing of one frame against another.
        var best = readings.Max(reading => reading.Chars);
        if (best == 0) continue;

        for (var i = 0; i < readings.Length; i++)
        {
            var reading = readings[i];
            var share = (double)reading.Chars / best;
            totals[i] += share;
            counted[i]++;

            Console.WriteLine(
                $"  margin={reading.Margin:0.00}line {reading.Size,-18} {reading.Boxes} box " +
                $"chars={reading.Chars,3} {share,5:0%} {reading.Ms,4}ms  {reading.Text}");
        }
    }

    Console.WriteLine(new string('=', 78));
    Console.WriteLine("share of each frame's own best reading, by how much margin the block left:");
    for (var i = 0; i < margins.Length; i++)
        if (counted[i] > 0)
            Console.WriteLine(
                $"  margin={margins[i]:0.00} line heights : {totals[i] / counted[i],6:0.0%}  " +
                $"({counted[i]} frames)");

    return 0;
}

// Scale sweep: the same frame read at every detector size, to see which sizes find the text and
// how wide the working band is. This is the measurement #22 step 0 asks for, and it only means
// anything on frames whose contents are known — the dumped "rescued" frames, where the primary
// size read nothing and a fallback read the subtitle fine.
// What the confidence filter throws away, and whether any of it belonged to a real line.
//
// Issue #85: RejectUnconvincingBlocks runs BEFORE OcrTextBlockGrouper, so a low-confidence tail
// fragment is gone before MergeSameLineFragments could join it back onto the sentence it came from
// — the subtitle then reaches the screen a few characters short. Swapping the two is not available;
// OcrService records why (a scenery box merged into a subtitle takes the subtitle down with it when
// the merged box is judged a collapse).
//
// So the question is whether a narrower rule is worth writing, and that turns on WHAT such a rule
// would let back in. This mode answers exactly that: for every fragment the filter drops, it runs
// the real grouper over the UNFILTERED blocks and reports whether the fragment would have landed
// inside a line long enough to be real. Those are the readings a carve-out would keep; everything
// else is what it would still drop. Read the two lists before writing the rule, not after.
// The glyph height estimate at full precision, for boxes that have already been read.
//
// --trace prints four decimals, which is the right amount to read but not enough to compute with:
// two figures that print as 0.8000 can sit either side of a 0.80 bar, and an analysis that rounds
// before it compares reports the wrong side of it. That happened — two pairs out of 302 — and the
// fix cannot be a tolerance, because a tolerance turns a precision problem into a permanently
// fuzzy threshold that is harder to find the next time.
//
// So: the boxes come back out of a trace that already exists (no image is read again), and the
// numbers are recomputed by THE PRODUCTION FUNCTION rather than by a copy of its formula in the
// analysis script. The text is synthesised to the glyph count because that is all the estimate
// reads of it — the script is passed separately.
//
// Input, one box per line, whitespace-separated:  <Latin|Cjk> <width> <height> <glyphs>
if (args[0] == "--estimate-precision")
{
    if (args.Length < 2 || !File.Exists(args[1]))
    {
        Console.Error.WriteLine("usage: --estimate-precision <boxes.txt>   (lines of: Latin|Cjk W H glyphs)");
        return 1;
    }

    Console.WriteLine("script\twidth\theight\tglyphs\tboxEstimate\tpitchCandidate\tglyphHeight\tsource\tbranch\tpitchWon");

    foreach (var entry in File.ReadLines(args[1]))
    {
        var fields = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0 || entry.StartsWith('#'))
            continue;

        if (fields.Length != 4 ||
            !Enum.TryParse<OcrLayoutScript>(fields[0], out var estimateScript) ||
            !double.TryParse(fields[1], out var estimateWidth) ||
            !double.TryParse(fields[2], out var estimateHeight) ||
            !int.TryParse(fields[3], out var estimateGlyphs))
        {
            Console.Error.WriteLine($"skipped: {entry}");
            continue;
        }

        // Whatever character it is, the estimate only counts them. Kept to one script's worth of
        // character anyway, so a reader of the input cannot mistake which side it stands for.
        var estimateText = new string(estimateScript == OcrLayoutScript.Cjk ? '字' : 'a', estimateGlyphs);
        var estimateBox = new System.Windows.Rect(0, 0, estimateWidth, estimateHeight);
        var estimateHeightOut = OnnxOcrEngine.LayoutGlyphHeightFor(
            estimateScript, estimateBox, estimateText, out var estimateTrace);

        Console.WriteLine(
            $"{estimateScript}\t{estimateWidth:G17}\t{estimateHeight:G17}\t{estimateGlyphs}\t" +
            $"{estimateTrace.BoxEstimate:G17}\t" +
            $"{(estimateTrace.PitchCandidate is { } candidate ? candidate.ToString("G17") : "null")}\t" +
            $"{(estimateHeightOut is { } result ? result.ToString("G17") : "null")}\t" +
            $"{estimateTrace.Source}\t{estimateTrace.PitchBranchEntered}\t{estimateTrace.PitchSelected}");
    }

    return 0;
}

// The rendered comparison set: known text, at known fonts and sizes, with the two boxes that can
// be measured off the drawing itself. Writes the sheets and a manifest; no OCR runs here.
//
// LAYOUT BOX and INK BOX are different things and the manifest carries both, separately, because
// the estimate is a function of a rectangle and it matters enormously which rectangle. Neither is
// a detection box — that only exists after --glyph-samples-ocr has read the sheets back.
//
// Estimates are the production function's, called once per box, and the trace it fills in is what
// says which path each one took. Nothing here re-derives any part of the formula.
if (args[0] == "--glyph-samples")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("usage: --glyph-samples <outDir>");
        return 1;
    }

    var sampleDir = args[1];
    var samples = OcrHarness.GlyphSampleSheets.BuildSamples();
    var placedSamples = OcrHarness.GlyphSampleSheets.Render(sampleDir, samples, Console.Error);

    var manifestPath = Path.Combine(sampleDir, "samples.tsv");
    using (var manifest = new StreamWriter(manifestPath, false, new System.Text.UTF8Encoding(false)))
    {
        // Two estimate blocks per row, prefixed by which box they were computed from. One header
        // for both would leave the reader to guess, and guessing which box a number came from is
        // exactly the mistake this whole step exists to avoid.
        manifest.WriteLine(string.Join('\t', new string[][]
        {
            OcrHarness.GlyphSampleSheets.ManifestHeader,
            [.. OcrHarness.GlyphSampleSheets.EstimateHeader.Select(column => "layout_" + column)],
            [.. OcrHarness.GlyphSampleSheets.EstimateHeader.Select(column => "ink_" + column)],
        }.SelectMany(columns => columns)));

        foreach (var placed in placedSamples)
        {
            manifest.WriteLine(string.Join('\t',
                OcrHarness.GlyphSampleSheets.ManifestLine(placed),
                OcrHarness.GlyphSampleSheets.EstimateColumns(placed.Sample.Script, placed.LayoutBox, placed.Sample.Text),
                OcrHarness.GlyphSampleSheets.EstimateColumns(placed.Sample.Script, placed.InkBox, placed.Sample.Text)));
        }
    }

    Console.WriteLine($"samples: {placedSamples.Count} rows, {placedSamples.Select(p => p.Sheet).Distinct().Count()} sheets");
    Console.WriteLine($"manifest: {manifestPath}");
    Console.WriteLine($"no ink found: {placedSamples.Count(p => !p.InkFound)}");
    return 0;
}

// The same set read back through OCR, which is the only place a detection box appears.
//
// OCR changes more than the rectangle: it changes the text, the glyph count and therefore the
// script, and it drops, splits and merges lines. So each row is matched back to what was drawn and
// the four failure kinds are counted rather than dropped, and every matched row gets its estimate
// computed twice — once on the known text and once on what came back — so that the box's
// contribution and the recognition's can be told apart.
if (args[0] == "--glyph-samples-ocr")
{
    if (args.Length < 2 || !File.Exists(Path.Combine(args[1], "samples.tsv")))
    {
        Console.Error.WriteLine("usage: --glyph-samples-ocr <outDir>   (run --glyph-samples first)");
        return 1;
    }

    var ocrSampleDir = args[1];
    var manifestRows = File.ReadAllLines(Path.Combine(ocrSampleDir, "samples.tsv"))
        .Skip(1)
        .Where(line => line.Length > 0)
        .Select(OcrHarness.GlyphSampleSheets.ParseManifestLine)
        .ToList();

    using var sampleEngine = new OnnxOcrEngine();
    var sheetGroups = manifestRows.GroupBy(row => row.Sheet).ToList();
    int sampleMatched = 0, sampleMissed = 0, sampleSplit = 0, sampleMerged = 0, sampleSpurious = 0, sampleNoInk = 0;

    var ocrPath = Path.Combine(ocrSampleDir, "ocr.tsv");
    using (var ocrOut = new StreamWriter(ocrPath, false, new System.Text.UTF8Encoding(false)))
    {
        ocrOut.WriteLine(string.Join('\t', new string[][]
        {
            [
                "id", "sheet", "font", "sizePx", "knownScript", "content", "glyphs", "text",
                "status", "recText", "recScript", "recGlyphs", "textExact", "conf",
                "ocrX", "ocrY", "ocrW", "ocrH", "productionEst",
            ],
            [.. OcrHarness.GlyphSampleSheets.EstimateHeader.Select(column => "known_" + column)],
            [.. OcrHarness.GlyphSampleSheets.EstimateHeader.Select(column => "rec_" + column)],
        }.SelectMany(columns => columns)));

        foreach (var sheet in sheetGroups)
        {
            var sheetPath = Path.Combine(ocrSampleDir, sheet.Key);
            if (!File.Exists(sheetPath)) { Console.Error.WriteLine($"(missing sheet) {sheetPath}"); continue; }

            var rows = sheet.OrderBy(row => row.LayoutBox.Y).ToList();

            // The recognition model follows what was drawn, because reading a CJK sheet with the
            // Latin model is a different experiment from the one being run.
            var sheetLanguage = rows.Any(row => row.Sample.Script is OcrLayoutScript.Cjk or OcrLayoutScript.Mixed)
                ? "JA"
                : "EN";

            using var sheetImage = new Bitmap(sheetPath);
            var sheetBlocks = await sampleEngine.RecognizeAsync(sheetImage, sheetLanguage) ?? [];

            // Which drawn rows each detection covers, by how much of that row's ink it contains.
            // Ink rather than the layout box: the layout box carries the font's leading, which no
            // detector has ever drawn a rectangle around.
            var covers = sheetBlocks.ToDictionary(
                block => block,
                block => rows.Where(row =>
                    row.InkFound &&
                    Math.Max(0, Math.Min(block.LayoutBounds.Bottom, row.InkBox.Bottom) - Math.Max(block.LayoutBounds.Top, row.InkBox.Top))
                        >= row.InkBox.Height * 0.5 &&
                    Math.Min(block.LayoutBounds.Right, row.InkBox.Right) > Math.Max(block.LayoutBounds.Left, row.InkBox.Left))
                    .ToList());

            sampleSpurious += covers.Count(entry => entry.Value.Count == 0);

            foreach (var row in rows)
            {
                if (!row.InkFound) { sampleNoInk++; continue; }

                var candidates = covers.Where(entry => entry.Value.Contains(row)).Select(entry => entry.Key).ToList();

                string status;
                OcrTextBlock? chosen = null;

                if (candidates.Count == 0)
                {
                    status = "missed";
                    sampleMissed++;
                }
                else if (candidates.Any(block => covers[block].Count > 1))
                {
                    status = "merged";
                    sampleMerged++;
                }
                else if (candidates.Count > 1)
                {
                    status = "split";
                    sampleSplit++;
                }
                else
                {
                    status = "matched";
                    chosen = candidates[0];
                    sampleMatched++;
                }

                var recText = chosen?.Text ?? string.Empty;
                var recScript = chosen is null ? OcrLayoutScript.Unknown : LayoutScriptDetection.For(recText);
                var ocrBox = chosen?.LayoutBounds ?? default;

                ocrOut.WriteLine(string.Join('\t',
                    row.Sample.Id, row.Sheet, row.Sample.Font, row.Sample.SizePx, row.Sample.Script,
                    row.Sample.Content, row.Sample.Glyphs, row.Sample.Text,
                    status,
                    recText.Replace('\t', ' '),
                    recScript,
                    recText.Count(c => !char.IsWhiteSpace(c)),
                    string.Equals(recText.Trim(), row.Sample.Text, StringComparison.Ordinal),
                    chosen?.Confidence is { } confidence ? OcrHarness.GlyphSampleSheets.F(confidence) : "null",
                    OcrHarness.GlyphSampleSheets.F(ocrBox.X), OcrHarness.GlyphSampleSheets.F(ocrBox.Y),
                    OcrHarness.GlyphSampleSheets.F(ocrBox.Width), OcrHarness.GlyphSampleSheets.F(ocrBox.Height),
                    chosen?.LayoutGlyphHeight is { } production ? OcrHarness.GlyphSampleSheets.F(production) : "null",
                    // Known text on the OCR box, then recognised text on the same box: the first
                    // isolates what the detector did to the rectangle, the second is the whole
                    // pipeline. Subtracting one from the other is not "detection error" — the
                    // difference also carries the text and the script.
                    OcrHarness.GlyphSampleSheets.EstimateColumns(row.Sample.Script, ocrBox, row.Sample.Text),
                    OcrHarness.GlyphSampleSheets.EstimateColumns(recScript, ocrBox, recText)));
            }

            Console.WriteLine($"{sheet.Key}\t{rows.Count} rows\tlang={sheetLanguage}\t{sheetBlocks.Count} detections");
        }
    }

    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"matched : {sampleMatched}");
    Console.WriteLine($"missed  : {sampleMissed}");
    Console.WriteLine($"split   : {sampleSplit}   <- one drawn line, more than one detection");
    Console.WriteLine($"merged  : {sampleMerged}   <- a detection covering more than one drawn line");
    Console.WriteLine($"spurious: {sampleSpurious}   <- detections covering no drawn line");
    Console.WriteLine($"no ink  : {sampleNoInk}   <- nothing was drawn, so nothing could be matched");
    Console.WriteLine($"ocr: {ocrPath}");
    return 0;
}

if (args[0] == "--reject-audit")
{
    using var auditEngine = new OnnxOcrEngine();
    int framesRead = 0, framesLosing = 0, droppedTotal = 0, wouldMerge = 0, isolated = 0;

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var size = harnessSize
            ?? RealtimeDetectorSize.For(image.Width, image.Height, harnessMode).Primary;

        // The engine directly, not OcrService: OcrService is where the filter lives, and these are
        // the blocks as they exist before it runs.
        var raw = await auditEngine.TryRecognizeAsync(image, harnessLanguage, size);
        if (raw is null || raw.Count == 0) continue;

        framesRead++;

        var dropped = raw
            .Where(block => ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence))
            .ToList();
        if (dropped.Count == 0) continue;

        framesLosing++;
        droppedTotal += dropped.Count;

        var groupedUnfiltered = OcrTextBlockGrouper.Group(raw, GroupingProfile.Realtime);

        foreach (var block in dropped)
        {
            var fragment = block.Text.Trim();

            // Whatever the grouper merged this fragment into is what it would have become. Long
            // enough to be real is the same floor the filter itself uses, so the two rules are
            // being compared on one scale rather than two.
            var host = groupedUnfiltered.FirstOrDefault(group =>
                group.Text.Contains(fragment, StringComparison.Ordinal) &&
                group.Text.Trim().Length > fragment.Length &&
                group.Text.Trim().Length >= ShortReadingDetection.ShortTextLength);

            if (host is null) { isolated++; continue; }

            wouldMerge++;
            // The fragment itself, because the judgement this mode exists to support cannot be made
            // from a count: a 3-character reading is either the end of a word or a scrap of scenery,
            // and only looking at it says which.
            Console.WriteLine(
                $"  MERGES  {Path.GetFileName(path)} @{size}  conf={block.Confidence:0.00}  " +
                $"\"{fragment}\" -> line of {host.Text.Trim().Length}ch");
        }
    }

    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"frames read             : {framesRead}");
    Console.WriteLine($"frames losing a fragment: {framesLosing}");
    Console.WriteLine($"fragments dropped       : {droppedTotal}");
    Console.WriteLine($"  would have merged     : {wouldMerge}   <- what a narrowed rule would keep");
    Console.WriteLine($"  isolated              : {isolated}   <- what it would still drop");
    return 0;
}

// Every next-line verdict the grouper reached, with the geometry it judged on.
//
// The ordinary output shows which lines were joined and no more, so a paragraph that came back as
// four separate translations says nothing about which threshold refused it, or by how much. These
// thresholds are the whole of the grouper and they are tuned against real captures, so seeing the
// near misses is what makes tuning something other than guesswork: a run of pairs refused on
// "vertical gap" at 0.83 of a line is a different problem from one refused on "no continuation
// evidence" with the gap at 0.2.
//
// Numbers are in line heights, not pixels, so captures of different sizes can be read side by side.
// Each side's layout script is printed too, because that is what decides whether the size test
// compared glyph heights or fell back to the raw detection boxes.
if (args[0] == "--group-explain")
{
    using var explainEngine = new OnnxOcrEngine();

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        Console.WriteLine(new string('=', 78));
        Console.WriteLine($"IMAGE: {path}");

        using var image = new Bitmap(path);

        // Named on every image rather than once at the top, because these outputs get saved and
        // diffed against each other months apart, and a file that does not say which flow it is
        // about is a file that will eventually be compared with one about the other.
        List<OcrTextBlock>? raw;
        if (harnessRealtime || harnessSize is not null)
        {
            var size = harnessSize
                ?? RealtimeDetectorSize.For(image.Width, image.Height, harnessMode).Primary;
            // The mode is named because it picks the grouper, not just the thresholds: Subtitle
            // runs DialogueTextGrouper and Panel runs the screenshot grouper on the Realtime
            // profile. A file that does not say which one ran cannot be read against the other.
            Console.WriteLine($"FLOW: 即時翻譯 (detect={size}, mode={harnessMode})");
            raw = await explainEngine.TryRecognizeAsync(image, harnessLanguage, size);
        }
        else
        {
            // The screenshot flow's own entry point, so the size comes from the same place the app
            // gets it rather than from a number repeated here.
            // The mode is named except on the conservative one, whose thresholds are the ones every
            // capture was grouped on before modes existed — so an Interface run's output stays
            // comparable byte for byte with every file saved back then. Tied to which behaviour it
            // is rather than to which mode is default: the default moved in v2 and this rule did
            // not, because what it is protecting is the comparison, not the setting.
            Console.WriteLine(harnessLayoutMode == CaptureLayoutMode.Interface
                ? "FLOW: 截圖翻譯 (detect=screenshot)"
                : $"FLOW: 截圖翻譯 (detect=screenshot, mode={harnessLayoutMode})");
            raw = await explainEngine.RecognizeAsync(image, harnessLanguage);
        }

        if (raw is null || raw.Count == 0) { Console.WriteLine("  (nothing read)"); continue; }

        // The confidence floor OcrService applies before grouping on the realtime path only; the
        // screenshot path deliberately keeps everything. A size named on its own asks for realtime
        // sizing without claiming to be that pipeline, so it does not get the filter.
        if (harnessRealtime)
        {
            var kept = OcrService.RejectUnconvincingBlocks(raw);
            Console.WriteLine($"  realtime confidence filter: {raw.Count} -> {kept.Count} lines");
            raw = kept;
            if (raw.Count == 0) { Console.WriteLine("  (nothing survived)"); continue; }
        }

        // The profile the flow named above would really have used. Realtime has its own and never
        // takes the screenshot side's, so printing verdicts from the wrong one would be tuning
        // against thresholds the app does not run.
        var explainProfile = harnessRealtime
            ? GroupingProfile.Realtime
            : GroupingProfile.For(harnessLayoutMode);
        if (!harnessRealtime)
            raw = OcrService.PrepareScreenshotGrouping(image, raw, explainProfile);
        var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
        var groupingTrace = harnessTrace ? new GroupingTrace() : null;
        // Through the app's own realtime entry point rather than straight into the screenshot
        // grouper. Calling the grouper directly is what this diagnostic used to do, and on Subtitle
        // that reported the Panel branch's verdicts for a pipeline that never runs it — the
        // dialogue rules were invisible here for the whole round that changed them.
        var grouped = harnessRealtime
            ? OcrService.GroupRealtime(raw, image.Height, harnessMode, groupingTrace, decisions)
            : OcrTextBlockGrouper.Group(raw, explainProfile, decisions, groupingTrace);
        if (harnessRealtime && harnessMode == RealtimeBlockMode.Subtitle)
            Console.WriteLine(
                "  dialogue grouper: profile thresholds below do not apply; scene-sized boxes are " +
                "dropped inside GroupRealtime, so a line counted here may not have reached it");

        if (groupingTrace is not null)
        {
            // Which build actually ran. An exe on disk is not evidence that it came out of the tree
            // being reported on, and a scan whose build had failed has already been read as "the
            // change did nothing" once on this branch.
            var harnessAssembly = System.Reflection.Assembly.GetExecutingAssembly();
            Console.WriteLine(
                $"TRACE BUILD: {harnessAssembly.GetName().Name} " +
                $"{System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(harnessAssembly)?.InformationalVersion} " +
                $"built={File.GetLastWriteTimeUtc(harnessAssembly.Location):yyyy-MM-dd HH:mm:ss}Z");

            // The profile prints its own values rather than its name: one mode has meant more than
            // one set of thresholds since v2.4, so a run named only by its mode does not say what
            // it was judged on.
            Console.WriteLine(
                $"TRACE PROFILE: TightlySetMinTextSizeRatio={explainProfile.TightlySetMinTextSizeRatio:0.0000} " +
                $"WaiveLengthTestWhenSetSolid={explainProfile.WaiveLengthTestWhenSetSolid} " +
                $"SolidLineAdvanceWhenWrapped={explainProfile.SolidLineAdvanceWhenWrapped:0.0000} " +
                $"MinTextSizeRatio={OcrTextBlockGrouper.MinTextSizeRatio:0.0000}");
            Console.WriteLine(
                $"TRACE INPUT: {path} {image.Width}x{image.Height}px whole image, no ROI " +
                $"lang={harnessLanguage} mode={harnessLayoutMode} " +
                $"detect={(harnessRealtime || harnessSize is not null ? harnessSize?.ToString() ?? "realtime" : "screenshot")}");

            // What each field means, printed with the output rather than kept in a document
            // beside it: these files are read months later by whoever is holding the bug.
            Console.WriteLine(
                "  trace legend: box*0.82=the estimate before anything challenges it | " +
                "pitch=W/n*coefficient, computed whether or not it was read | " +
                "W-2H=distance from the width condition, which is a strict > | " +
                "n>=4,W>2H=the two halves of that condition, separately");
            Console.WriteLine(
                "  trace legend: branch=both halves held, so the pitch was consulted | " +
                "pitchWon=it came out lower and replaced the box | " +
                "short(applied,cand,won)=the one-to-three-glyph correction | " +
                "floor=the 1px floor was reached");
            Console.WriteLine(
                "  trace legend: src=which value came back — Box, Pitch, ShortText, Floor, " +
                "None (no estimate for this script), MergedPairwiseUpperMedian (merged one " +
                "fragment at a time, each merge keeping the upper median of the two), " +
                "NoneAfterMerge (the joined text is no longer of one script)");
            Console.WriteLine(
                "  trace legend: exact=the same box at full precision (G17), and whether the width " +
                "is exactly twice the height — the case a four-decimal print cannot tell from a near miss");
            Console.WriteLine("  trace --- blocks as the detector drew them, before same-line merging ---");
            foreach (var line in groupingTrace.Blocks)
            {
                // The production function, asked again on the same inputs, rather than the formula
                // written out a second time here: a harness that works out what the estimate
                // "would have" done is a harness that can be wrong about it in exactly the way this
                // whole step exists to stop.
                OnnxOcrEngine.LayoutGlyphHeightFor(
                    line.Block.LayoutScript, line.Block.LayoutBounds, line.Block.Text, out var estimate);
                WriteTracedLine(line, estimate);
            }

            Console.WriteLine("  trace --- lines the next-line rules were asked about ---");
            foreach (var line in groupingTrace.Lines)
            {
                if (line.SourceIds.Count == 1)
                {
                    // Same instance the block layer already traced: the merge keeps what it is
                    // handed when a row holds one box.
                    OnnxOcrEngine.LayoutGlyphHeightFor(
                        line.Block.LayoutScript, line.Block.LayoutBounds, line.Block.Text, out var estimate);
                    WriteTracedLine(line, estimate);
                    continue;
                }

                // Deliberately not re-estimated: running the single-line formula over the joined
                // box would print a number the grouper never saw. What it carries instead is the
                // result of merging the fragments one at a time, each merge taking the upper median
                // of the two heights in hand — which on two values is the LARGER of them, and over
                // three fragments is not the median of the three. Heights 10, 20 and 30 merge to
                // 30. The label said "median of members" for one round and that was not true.
                Console.WriteLine(
                    $"  trace {line.Id,-4} <- {string.Join("+", line.SourceIds),-14} " +
                    $"script={line.Block.LayoutScript,-7} {TraceBox(line.Block.LayoutBounds)} " +
                    $"n={ShortTextGlyphHeight.GlyphsIn(line.Block.Text),3} " +
                    $"glyph={TraceNumber(line.Block.LayoutGlyphHeight)} " +
                    // Null has its own name here. A merged line whose joined text is no longer of
                    // one script carries no estimate at all, and calling that a combination of its
                    // members would be the same untruth in the other direction.
                    $"src={(line.Block.LayoutGlyphHeight is null ? "NoneAfterMerge" : "MergedPairwiseUpperMedian")}");
                Console.WriteLine($"  trace      {TraceExact(line.Block.LayoutBounds)}");
                Console.WriteLine($"  trace      \"{Shorten(line.Block.Text)}\"");
            }
        }

        Console.WriteLine($"  lines read: {raw.Count}  ->  groups sent to translation: {grouped.Count}");

        // One aggregatable line per image, so a whole corpus can be summed without re-reading the
        // verdicts. The script census is on the lines as read, not on the groups, because that is
        // the population the size test pairs off against each other.
        int Census(OcrLayoutScript script) => raw.Count(block => block.LayoutScript == script);
        Console.WriteLine(
            $"SUMMARY	{path}	lang={harnessLanguage}	lines={raw.Count}	groups={grouped.Count}" +
            $"	latin={Census(OcrLayoutScript.Latin)}	cjk={Census(OcrLayoutScript.Cjk)}" +
            $"	mixed={Census(OcrLayoutScript.Mixed)}	unknown={Census(OcrLayoutScript.Unknown)}" +
            $"	text={string.Join(" | ", grouped.Select(block => block.Text.Trim()))}");
        for (var i = 0; i < grouped.Count; i++)
            Console.WriteLine($"  [{i}] lines={grouped[i].Lines.Count}  {grouped[i].Text}");

        // Each kind printed with the labels for what it actually measured. The two share a record
        // (see NextLineDecision) and fill several of its fields with different quantities, so one
        // format for both would put a horizontal gap under a heading saying "vertical" and print
        // two columns of zeroes that read as measurements. The threshold work on this branch is
        // aggregated out of these lines, and a run that has to be split on Kind afterwards should
        // say so on its face.
        Console.WriteLine("  --- verdicts: row = same line left to right, next = the line below ---");
        Console.WriteLine("  --- gaps and advances in line heights ---");
        // align is the left-edge delta, the one the alignment gate reads today; alignC and alignR
        // are the same misalignment measured on the centres and on the right edges, and only a
        // "next" verdict has them. bar is the advance a shorter final line is allowed before the
        // leading rule refuses it. All four are line heights, and none of them exist for "row".
        Console.WriteLine(
            "  --- next: align=left alignC=centre alignR=right alignMin=what the gate read, " +
            "bar=wrapped-final-line limit, solid=this group's set-solid limit ---");
        // Same columns, different quantities. Printing the screenshot legend over dialogue verdicts
        // would put a paragraph rule's name on a number no paragraph rule produced.
        if (harnessRealtime && harnessMode == RealtimeBlockMode.Subtitle)
            Console.WriteLine(
                "  --- dialogue mode: row hgap=horizontal gap, overlap=vertical overlap of the two " +
                "boxes; next size=height ratio, bar=the height floor it was judged on (0.60 for two " +
                "substantial rows, else 0.75), solid=the alignment ceiling (2.50 or 1.25) ---");
        foreach (var decision in decisions)
        {
            var verdict = decision.Joined ? "JOIN  " : "SPLIT ";
            Console.WriteLine(decision.Kind == "row"
                ? $"  row  {verdict} hgap={decision.VerticalGap,6:0.00} " +
                  $"overlap={decision.LeftDelta,5:0.00} width={decision.WidthRatio:0.00} " +
                  $"script={decision.PreviousScript}/{decision.CurrentScript}  [{decision.Rule}]"
                : $"  next {verdict} vgap={decision.VerticalGap,6:0.00} " +
                  $"align={decision.LeftDelta,6:0.00} alignC={decision.CenterDelta,6:0.00} " +
                  $"alignR={decision.RightDelta,6:0.00} alignMin={decision.AlignmentDelta,6:0.00} " +
                  $"size={decision.TextSizeRatio:0.00} " +
                  $"width={decision.WidthRatio:0.00} adv={decision.LineAdvance,5:0.00} " +
                  $"bar={decision.LeadingBar:0.00} solid={decision.SolidBar:0.00} " +
                  $"script={decision.PreviousScript}/{decision.CurrentScript}  [{decision.Rule}]");
            Console.WriteLine($"      \"{Shorten(decision.Previous)}\" + \"{Shorten(decision.Current)}\"");

            if (!harnessTrace)
                continue;

            // Same two ids the layers above and below use, so a verdict can be followed back to the
            // boxes it was made on and forward into the group it produced. The text is in the line
            // above and it is truncated, which is why it cannot serve.
            Console.WriteLine(decision.Kind == "row"
                ? $"  trace {decision.PreviousId} + {decision.CurrentId}  (blocks)"
                : $"  trace {decision.PreviousId} + {decision.CurrentId}  " +
                  $"size={decision.SizeBasis} prev={decision.PreviousSizeValue:0.0000} " +
                  $"cur={decision.CurrentSizeValue:0.0000} ratio={decision.TextSizeRatio:0.0000}  " +
                  $"dy={decision.AdvancePixels:0.0000} / {decision.AdvanceDenominator:0.0000} " +
                  $"= adv {decision.LineAdvance:0.0000}");
        }

        if (groupingTrace is not null)
        {
            Console.WriteLine("  trace --- groups, by line id ---");
            for (var i = 0; i < groupingTrace.Groups.Count; i++)
                Console.WriteLine($"  trace [{i}] {string.Join(" ", groupingTrace.Groups[i])}");
        }
    }

    return 0;

    static string Shorten(string text) =>
        text.Length <= 42 ? text : string.Concat(text.AsSpan(0, 40), "…");

    // One line of inputs and one of the path taken through the estimate. Both are printed for every
    // line whether or not anything interesting happened on it, because which lines are interesting
    // is the question the trace is being read to answer.
    static void WriteTracedLine(GroupingTrace.Line line, GlyphHeightTrace estimate)
    {
        var sources = line.SourceIds.Count > 0 ? $"<- {string.Join("+", line.SourceIds)}" : "";
        Console.WriteLine(
            $"  trace {line.Id,-4} {sources,-14} script={estimate.Script,-7} {TraceBox(line.Block.LayoutBounds)} " +
            $"n={estimate.GlyphCount,3} glyph={TraceNumber(estimate.Result)} src={estimate.Source}");

        // A script with no estimate has no intermediate values either, and printing zeroes for them
        // would read as measurements. The two width conditions are still real, and worth seeing:
        // they say that this line would have taken the pitch branch had it been asked.
        if (estimate.Source == GlyphHeightSource.None)
        {
            Console.WriteLine(
                $"  trace      no estimate for this script  " +
                $"W-2H={estimate.WidthMinusTwiceHeight:+0.0000;-0.0000;0.0000}  " +
                $"n>=4={estimate.HasEnoughGlyphs} W>2H={estimate.IsWideEnough}");
            Console.WriteLine($"  trace      {TraceExact(line.Block.LayoutBounds)}");
            Console.WriteLine($"  trace      \"{Shorten(line.Block.Text)}\"");
            return;
        }

        Console.WriteLine(
            $"  trace      box*0.82={estimate.BoxEstimate:0.0000} " +
            $"pitch={TraceNumber(estimate.PitchCandidate)} (W/n*{estimate.PitchCoefficient:0.00})  " +
            $"W-2H={estimate.WidthMinusTwiceHeight:+0.0000;-0.0000;0.0000}  " +
            $"n>=4={estimate.HasEnoughGlyphs} W>2H={estimate.IsWideEnough} " +
            $"branch={estimate.PitchBranchEntered} pitchWon={estimate.PitchSelected}  " +
            $"short(applied={estimate.ShortTextApplied},cand={TraceNumber(estimate.ShortTextCandidate)}," +
            $"won={estimate.ShortTextSelected})  floor={estimate.FloorApplied}");
        Console.WriteLine($"  trace      {TraceExact(line.Block.LayoutBounds)}");
        Console.WriteLine($"  trace      \"{Shorten(line.Block.Text)}\"");
    }

    // The width condition at full precision, and the equality asked directly.
    //
    // Four decimals cannot answer the question this trace was added for. "62.0000 vs 62.0000" is
    // what a box exactly on the line prints, and it is also what a box a ten-thousandth of a pixel
    // off it prints, and those two are estimated 36% apart. Printing G17 and the comparison itself
    // is the difference between reporting the boundary and guessing at it.
    static string TraceExact(System.Windows.Rect box) =>
        $"exact: W={box.Width:G17} H={box.Height:G17} " +
        $"W-2H={box.Width - box.Height * 2:G17} W==2H={box.Width == box.Height * 2}";

    static string TraceBox(System.Windows.Rect box) =>
        $"box=({box.X:0.0000},{box.Y:0.0000},{box.Width:0.0000},{box.Height:0.0000})";

    static string TraceNumber(double? value) => value is { } number ? $"{number:0.0000}" : "null";
}

// The vertical pipeline, which nothing else here can reach.
//
// Vertical writing is turned anticlockwise for the horizontal detector, grouped in that frame,
// mapped back, and then merged a second time into columns. Only that second pass decides what a
// reader of a Japanese page actually gets, and --group-explain never runs it: it calls the grouper
// directly, which is the horizontal path. So every number this harness has ever printed for
// vertical-image-ja was the horizontal pipeline's.
if (args[0] == "--vertical-explain")
{
    using var verticalEngine = new OnnxOcrEngine();

    foreach (var path in args.Skip(1))
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        // No mode flag, because the pipeline has no parameter for one: vertical text runs on its
        // own profile whatever the toolbar says. This was briefly wired to the flag, between
        // discovering that it had been pinned to one profile by accident and measuring what the
        // other profile actually did to vertical material — which was join balloons rather than the
        // lines inside them. --interface is accepted and ignored here.
        var columns = await OcrService.RecognizeVerticalAsync(
            verticalEngine, image, harnessLanguage, CancellationToken.None);

        Console.WriteLine(
            $"VERTICAL	{path}	lang={harnessLanguage}	columns={columns.Count}" +
            $"	scripts={string.Join(",", columns.Select(c => c.LayoutScript))}" +
            $"	text={string.Join(" | ", columns.Select(c => c.Text.Trim()))}");
    }

    return 0;
}

// Where a change in the user's selection first becomes a change in the answer.
//
// The complaint this exists for is old and was always reported the same way: the same text on the
// same screen reads differently depending on how much the user framed around it. It is now
// reproducible to one pixel, so the useful thing is no longer to reproduce it but to say WHICH
// LAYER moves first — the layers have different fixes, and one of them has none.
//
// So this walks the pipeline in order and reports each layer against the base ROI:
//
//   1 source pixels    the overlapping crop. Comes from one file, so it can only differ if the
//                      experiment is cropping wrongly.
//   2 preprocessing    the canvas AlignForDetector builds and the scale the library then applies.
//                      Both are arithmetic on the ROI's own dimensions.
//   3 detector input   the aligned bitmap's pixels over the overlap. If this moves, resizing moved
//                      the glyphs; if it does not, the tensor differs only in how much blank
//                      surrounds them, and a box that still changed changed inside the network.
//   4 detector boxes   DetectBoxesOnly, ahead of recognition and every filter, so a box that was
//                      never found can be told from a box that was found and read differently.
//   5 recognition      text on the boxes that survived.
//   6 grouping         how far all of the above is amplified by the thing that consumes it.
//
// Everything is compared in SOURCE IMAGE coordinates. Growing a ROI upwards moves every local
// coordinate by the amount it grew, and comparing those directly reports the whole page as moved.
if (args[0] == "--roi-stability")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine(
            "usage: --roi-stability <image> [--roi X,Y,W,H] [--grow up,down,left,right,all] [--steps 1,2,4,8,16,32]");
        return 1;
    }

    var roiPath = args[1];
    if (!File.Exists(roiPath)) { Console.Error.WriteLine($"(missing) {roiPath}"); return 1; }

    using var roiSource = new Bitmap(roiPath);

    // Defaults to a centre crop with room to grow on every side, so the mode says something on an
    // image nobody has framed by hand yet.
    var roiRect = new System.Drawing.Rectangle(
        roiSource.Width / 8, roiSource.Height / 8,
        roiSource.Width * 3 / 4, roiSource.Height * 3 / 4);

    var roiFlag = Array.IndexOf(args, "--roi");
    if (roiFlag >= 0 && roiFlag + 1 < args.Length)
    {
        var roiParts = args[roiFlag + 1].Split(',');
        if (roiParts.Length != 4 || !roiParts.All(part => int.TryParse(part, out _)))
        {
            Console.Error.WriteLine("usage: --roi X,Y,W,H");
            return 1;
        }

        roiRect = new System.Drawing.Rectangle(
            int.Parse(roiParts[0]), int.Parse(roiParts[1]),
            int.Parse(roiParts[2]), int.Parse(roiParts[3]));
    }

    var growFlag = Array.IndexOf(args, "--grow");
    var growSides = growFlag >= 0 && growFlag + 1 < args.Length
        ? args[growFlag + 1].Split(',').Select(side => side.Trim().ToLowerInvariant()).ToArray()
        : new[] { "down" };

    var roiStepFlag = Array.IndexOf(args, "--steps");
    var roiSteps = roiStepFlag >= 0 && roiStepFlag + 1 < args.Length
        ? args[roiStepFlag + 1].Split(',').Select(int.Parse).ToArray()
        : new[] { 1, 2, 4, 8, 16, 32 };

    using var roiEngine = new OnnxOcrEngine();

    // The aligned bitmaps have to outlive the probe that made them — layer 3 compares one ROI's
    // against another's — so they are held here and freed together at the end.
    var RoiProbes = new List<RoiProbed>();

    Console.WriteLine($"IMAGE: {roiPath}  ({roiSource.Width}x{roiSource.Height})");
    Console.WriteLine($"BASE ROI: {roiRect.X},{roiRect.Y} {roiRect.Width}x{roiRect.Height}");
    Console.WriteLine("FLOW: 截圖翻譯 (detect=screenshot)");
    Console.WriteLine();

    var roiBase = await RoiProbe(roiRect);
    Console.WriteLine(
        $"BASE  {roiBase.Roi.Width}x{roiBase.Roi.Height}  canvas={roiBase.Canvas}  scale={roiBase.Scale}  " +
        $"detBoxes={roiBase.Boxes.Count}  blocks={roiBase.Blocks.Count}  groups={roiBase.Groups}");
    Console.WriteLine(
        $"      box scores: {string.Join(" ", roiBase.Boxes.Select(box => box.Score.ToString("0.000")).OrderBy(s => s))}");
    Console.WriteLine($"      BoxScoreThresh={OnnxOcrEngine.ExportedThresholds.BoxScoreThresh}  BoxThresh={OnnxOcrEngine.ExportedThresholds.BoxThresh}");
    Console.WriteLine();
    Console.WriteLine(
        "grow  step  size        canvas       scale         input   det =/+/-      dH% 50/90/max   rec =/x/0     groups");
    Console.WriteLine(new string('-', 116));

    foreach (var side in growSides)
    {
        foreach (var step in roiSteps)
        {
            var grown = RoiGrow(roiRect, side, step);
            if (grown == roiRect)
            {
                Console.WriteLine($"{side,-5} +{step,-4} (no room left in the source image)");
                continue;
            }

            RoiReport(side, step, roiBase, await RoiProbe(grown));
        }
    }

    foreach (var probe in RoiProbes) probe.Aligned.Dispose();
    return 0;

    System.Drawing.Rectangle RoiGrow(System.Drawing.Rectangle rect, string side, int by)
    {
        var grow = side switch
        {
            "up" => (L: 0, T: by, R: 0, B: 0),
            "down" => (L: 0, T: 0, R: 0, B: by),
            "left" => (L: by, T: 0, R: 0, B: 0),
            "right" => (L: 0, T: 0, R: by, B: 0),
            _ => (L: by, T: by, R: by, B: by),
        };

        var x = Math.Max(0, rect.X - grow.L);
        var y = Math.Max(0, rect.Y - grow.T);
        var right = Math.Min(roiSource.Width, rect.Right + grow.R);
        var bottom = Math.Min(roiSource.Height, rect.Bottom + grow.B);

        return new System.Drawing.Rectangle(x, y, right - x, bottom - y);
    }

    async Task<RoiProbed> RoiProbe(System.Drawing.Rectangle rect)
    {
        using var crop = roiSource.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var sk = OnnxOcrEngine.ConvertToSkBitmap(crop);

        // The detector's own input, built the way the engine builds it. What used to stand here —
        // align, add the border back, then ask ScaleParam what the library would make of it —
        // described a pipeline the engine no longer runs: the downscale is now isotropic and done
        // before the library sees the image, and the library's own resize is the identity.
        var frame = OnnxOcrEngine.CreateDetectorFrame(sk, null);
        var aligned = frame.Bitmap;

        var boxes = roiEngine.DetectBoxesOnly(crop, harnessLanguage)
            .Select(box => (
                Bounds: new System.Windows.Rect(
                    box.Bounds.X + rect.X, box.Bounds.Y + rect.Y, box.Bounds.Width, box.Bounds.Height),
                box.Score))
            .ToList();

        var read = await roiEngine.RecognizeAsync(crop, harnessLanguage);
        var groups = OcrTextBlockGrouper.Group(read, harnessGroupingBaseline).Count;

        var blocks = read
            .Select(block => (
                Bounds: new System.Windows.Rect(
                    block.Bounds.X + rect.X, block.Bounds.Y + rect.Y,
                    block.Bounds.Width, block.Bounds.Height),
                block.Text))
            .ToList();

        var probed = new RoiProbed(
            rect,
            $"{aligned.Width}x{aligned.Height}",
            $"{frame.RatioX:0.0000}x{frame.RatioY:0.0000}",
            aligned,
            boxes,
            blocks,
            groups);

        RoiProbes.Add(probed);
        return probed;
    }

    void RoiReport(string side, int step, RoiProbed b, RoiProbed v)
    {
        var inputSame = RoiAlignedOverlapMatches(b, v);

        var used = new bool[v.Boxes.Count];
        var heightDeltas = new List<double>();
        var matchedCount = 0;
        var missingScores = new List<float>();
        foreach (var (box, score) in b.Boxes)
        {
            var best = -1;
            var bestIou = 0.35;
            for (var i = 0; i < v.Boxes.Count; i++)
            {
                if (used[i]) continue;
                var iou = RoiIou(box, v.Boxes[i].Bounds);
                if (iou <= bestIou) continue;
                bestIou = iou;
                best = i;
            }

            if (best < 0) { missingScores.Add(score); continue; }
            used[best] = true;
            matchedCount++;
            heightDeltas.Add(
                Math.Abs(v.Boxes[best].Bounds.Height - box.Height) / Math.Max(1, box.Height) * 100);
        }

        heightDeltas.Sort();
        var missing = b.Boxes.Count - matchedCount;
        var added = v.Boxes.Count - matchedCount;

        // Recognition keyed by position, because the question is whether the same place on the
        // screen came back saying the same thing.
        int same = 0, changed = 0, lost = 0;
        foreach (var block in b.Blocks)
        {
            var hit = v.Blocks
                .Select(other => (other, iou: RoiIou(block.Bounds, other.Bounds)))
                .Where(pair => pair.iou > 0.35)
                .OrderByDescending(pair => pair.iou)
                .Select(pair => (string?)pair.other.Text)
                .FirstOrDefault();

            if (hit is null) lost++;
            else if (hit.Trim() == block.Text.Trim()) same++;
            else changed++;
        }

        Console.WriteLine(
            $"{side,-5} +{step,-4} {v.Roi.Width}x{v.Roi.Height,-7} {v.Canvas,-12} {v.Scale,-13} " +
            $"{(inputSame ? "same" : "MOVED"),-7} " +
            $"{matchedCount}/{added}/{missing,-10} " +
            $"{RoiPct(heightDeltas, 0.5),4:0.0}/{RoiPct(heightDeltas, 0.9),4:0.0}/{RoiPct(heightDeltas, 1.0),4:0.0}   " +
            $"{same}/{changed}/{lost,-9} {v.Groups}{(v.Groups == b.Groups ? "" : "  <- changed")}" +
            (missingScores.Count == 0
                ? string.Empty
                : $"   missing box scores: {string.Join(" ", missingScores.Select(s => s.ToString("0.000")))}"));
    }

    // Layer 3. Compares the bitmap actually handed to the detector over the region the two ROIs
    // share, each read at its own local offset.
    static bool RoiAlignedOverlapMatches(RoiProbed a, RoiProbed b)
    {
        var overlap = System.Drawing.Rectangle.Intersect(a.Roi, b.Roi);
        if (overlap.Width <= 0 || overlap.Height <= 0) return false;

        for (var y = 0; y < overlap.Height; y++)
        {
            for (var x = 0; x < overlap.Width; x++)
            {
                if (a.Aligned.GetPixel(overlap.X - a.Roi.X + x, overlap.Y - a.Roi.Y + y) !=
                    b.Aligned.GetPixel(overlap.X - b.Roi.X + x, overlap.Y - b.Roi.Y + y))
                    return false;
            }
        }

        return true;
    }
}

// Candidate 1, measured but not shipped: separate what the user framed from what the detector is
// shown, and derive the second by snapping the first to a grid IN SOURCE IMAGE COORDINATES.
//
//   Logical ROI    what the user dragged. Still what the answer is about.
//   Analysis ROI   left/top snapped DOWN to a multiple of the grid, right/bottom snapped UP.
//
// The point is not that the canvas comes out a round size — padding a capture to a fixed canvas
// does that too, and it does not help, because two selections of different sizes still hold
// different pixels. Snapping in ABSOLUTE coordinates is different in kind: two Logical ROIs that
// land inside the same grid cells produce the SAME Analysis ROI, so the crop is the same crop, and
// the detector cannot tell them apart at all. What --roi-stability found — that everything up to
// the detector is already identical and the boxes move anyway — is exactly the failure this shape
// is meant to sidestep, by making the whole input identical rather than only the overlap.
//
// So the two questions this answers are:
//
//   1 while the Logical ROI has not crossed a grid line, is the answer bit-for-bit stable?
//   2 what does it cost — how often is a boundary crossed anyway, how big is the jump when it is,
//     and what does filtering the results back to the Logical ROI do to them?
//
// Question 2 is the one that decides whether this is worth shipping, because a grid does not remove
// the instability. It removes most of the OPPORTUNITIES for it, and enlarges what happens when one
// arrives — a crossing changes the input by a whole cell rather than by a pixel.
if (args[0] == "--roi-snap")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine(
            "usage: --roi-snap <image> [--roi X,Y,W,H] [--grid 32,64,128] [--grow down,right,all] [--steps 1,2,4,...]");
        return 1;
    }

    var snapPath = args[1];
    if (!File.Exists(snapPath)) { Console.Error.WriteLine($"(missing) {snapPath}"); return 1; }

    using var snapSource = new Bitmap(snapPath);

    var snapRoi = new System.Drawing.Rectangle(
        snapSource.Width / 8, snapSource.Height / 8,
        snapSource.Width * 3 / 4, snapSource.Height * 3 / 4);

    var snapRoiFlag = Array.IndexOf(args, "--roi");
    if (snapRoiFlag >= 0 && snapRoiFlag + 1 < args.Length)
    {
        var parts = args[snapRoiFlag + 1].Split(',');
        if (parts.Length != 4 || !parts.All(part => int.TryParse(part, out _)))
        {
            Console.Error.WriteLine("usage: --roi X,Y,W,H");
            return 1;
        }

        snapRoi = new System.Drawing.Rectangle(
            int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
    }

    var gridFlag = Array.IndexOf(args, "--grid");
    var snapGrids = gridFlag >= 0 && gridFlag + 1 < args.Length
        ? args[gridFlag + 1].Split(',').Select(int.Parse).ToArray()
        : new[] { 32, 64, 128 };

    var snapGrowFlag = Array.IndexOf(args, "--grow");
    var snapSides = snapGrowFlag >= 0 && snapGrowFlag + 1 < args.Length
        ? args[snapGrowFlag + 1].Split(',').Select(side => side.Trim().ToLowerInvariant()).ToArray()
        : new[] { "down", "right", "all" };

    var snapStepFlag = Array.IndexOf(args, "--steps");
    var snapSteps = snapStepFlag >= 0 && snapStepFlag + 1 < args.Length
        ? args[snapStepFlag + 1].Split(',').Select(int.Parse).ToArray()
        : new[] { 1, 2, 4, 8, 16, 32, 64 };

    using var snapEngine = new OnnxOcrEngine();

    // Held rather than disposed per probe: the byte-for-byte comparison needs the base's bitmap
    // alive while every variant is measured against it.
    var SnapProbes = new List<SnapProbed>();

    Console.WriteLine($"IMAGE: {snapPath}  ({snapSource.Width}x{snapSource.Height})");
    Console.WriteLine($"LOGICAL ROI: {snapRoi.X},{snapRoi.Y} {snapRoi.Width}x{snapRoi.Height}");
    Console.WriteLine("FLOW: 截圖翻譯 (detect=screenshot)");

    foreach (var grid in snapGrids)
    {
        var baseAnalysis = SnapTo(snapRoi, grid);
        var snapBase = await SnapProbe(snapRoi, baseAnalysis);

        Console.WriteLine();
        Console.WriteLine($"=== grid {grid} ===");
        Console.WriteLine(
            $"BASE  logical={Fmt(snapRoi)}  analysis={Fmt(baseAnalysis)}  " +
            $"detBoxes={snapBase.Boxes.Count}  groups={snapBase.All.Count}  " +
            $"kept={snapBase.Kept.Count}  straddling={snapBase.Straddling}");
        Console.WriteLine(
            "grow  step  logical        analysis         cell   crop   det =/+/-  dH% 50/90/max  ocr    kept  rec =/x/0  strad");
        Console.WriteLine(new string('-', 124));

        var crossings = 0;
        var totalSteps = 0;

        foreach (var side in snapSides)
        {
            foreach (var step in snapSteps)
            {
                var logical = SnapGrow(snapRoi, side, step);
                if (logical == snapRoi) continue;

                totalSteps++;
                var analysis = SnapTo(logical, grid);
                var crossed = analysis != baseAnalysis;
                if (crossed) crossings++;

                var probe = await SnapProbe(logical, analysis);
                SnapReport(side, step, grid, snapBase, probe, crossed);
            }
        }

        Console.WriteLine(
            $"      grid {grid}: {crossings} of {totalSteps} steps crossed a grid line " +
            $"({(totalSteps == 0 ? 0 : 100.0 * crossings / totalSteps):0.0}%)");
    }

    foreach (var probe in SnapProbes) probe.Aligned.Dispose();
    return 0;

    static string Fmt(System.Drawing.Rectangle r) => $"{r.X},{r.Y} {r.Width}x{r.Height}";

    // Outward on every side, so the Analysis ROI always contains the Logical one.
    System.Drawing.Rectangle SnapTo(System.Drawing.Rectangle rect, int grid)
    {
        var x = rect.X / grid * grid;
        var y = rect.Y / grid * grid;
        var right = Math.Min(snapSource.Width, (rect.Right + grid - 1) / grid * grid);
        var bottom = Math.Min(snapSource.Height, (rect.Bottom + grid - 1) / grid * grid);

        return new System.Drawing.Rectangle(x, y, right - x, bottom - y);
    }

    System.Drawing.Rectangle SnapGrow(System.Drawing.Rectangle rect, string side, int by)
    {
        var grow = side switch
        {
            "up" => (L: 0, T: by, R: 0, B: 0),
            "down" => (L: 0, T: 0, R: 0, B: by),
            "left" => (L: by, T: 0, R: 0, B: 0),
            "right" => (L: 0, T: 0, R: by, B: 0),
            _ => (L: by, T: by, R: by, B: by),
        };

        var x = Math.Max(0, rect.X - grow.L);
        var y = Math.Max(0, rect.Y - grow.T);
        var right = Math.Min(snapSource.Width, rect.Right + grow.R);
        var bottom = Math.Min(snapSource.Height, rect.Bottom + grow.B);

        return new System.Drawing.Rectangle(x, y, right - x, bottom - y);
    }

    async Task<SnapProbed> SnapProbe(System.Drawing.Rectangle logical, System.Drawing.Rectangle analysis)
    {
        using var crop = snapSource.Clone(analysis, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var sk = OnnxOcrEngine.ConvertToSkBitmap(crop);
        var aligned = OnnxOcrEngine.CreateDetectorFrame(sk, null).Bitmap;

        var read = await snapEngine.RecognizeAsync(crop, harnessLanguage);

        // Grouping runs on the whole Analysis ROI, which is the honest way round: it is what the
        // engine returned, and the capture-wide rules (SameLineGapThreshold, RepeatedRowLayout) are
        // supposed to see a whole layout. It is also where this design's own risk lives — those
        // rules now see content the user did not frame. Filtering first would trade that for the
        // opposite problem, a layout with holes in it.
        var grouped = OcrTextBlockGrouper.Group(read, harnessGroupingBaseline);

        var all = grouped
            .Select(block => (
                Bounds: new System.Windows.Rect(
                    block.Bounds.X + analysis.X, block.Bounds.Y + analysis.Y,
                    block.Bounds.Width, block.Bounds.Height),
                block.Text))
            .ToList();

        // Filter back to what the user framed. Centre-inside rather than intersects-at-all or
        // fully-inside: a box the user framed the middle of is a box they meant, one they clipped
        // the edge of is usually the neighbour's, and "fully inside" throws away every line the
        // selection deliberately cut through. The two rejected rules are counted as Straddling so
        // the cost of the choice is visible rather than assumed.
        var logicalRect = new System.Windows.Rect(
            logical.X, logical.Y, logical.Width, logical.Height);

        var kept = all
            .Where(block => logicalRect.Contains(
                new System.Windows.Point(
                    block.Bounds.X + block.Bounds.Width / 2,
                    block.Bounds.Y + block.Bounds.Height / 2)))
            .ToList();

        var straddling = all.Count(block =>
            block.Bounds.IntersectsWith(logicalRect) && !logicalRect.Contains(block.Bounds));

        // The detector's own boxes too, so the OCR layer can be judged before grouping and before
        // the filter — the three move for different reasons and reporting one number hides that.
        var boxes = snapEngine.DetectBoxesOnly(crop, harnessLanguage)
            .Select(box => new System.Windows.Rect(
                box.Bounds.X + analysis.X, box.Bounds.Y + analysis.Y,
                box.Bounds.Width, box.Bounds.Height))
            .ToList();

        var probed = new SnapProbed(logical, analysis, aligned, boxes, all, kept, straddling);
        SnapProbes.Add(probed);
        return probed;
    }

    void SnapReport(string side, int step, int grid, SnapProbed b, SnapProbed v, bool crossed)
    {
        // The claim under test: while no grid line has been crossed, the Analysis ROI is the same
        // rectangle of the same image, so the bitmap handed to the detector must be identical in
        // full — not merely over an overlap, which is all --roi-stability could ask.
        var cropIdentical = !crossed && SnapAlignedIdentical(b.Aligned, v.Aligned);

        // Three layers, kept apart on purpose, because they move for different reasons:
        //
        //   det    the detector's boxes over the whole Analysis ROI. Nothing of the user's
        //          selection reaches this, so it is the OCR's own stability.
        //   ocr    the grouped text over the whole Analysis ROI, likewise.
        //   kept   what survives filtering back to the Logical ROI. This one moves whenever the
        //          selection edge moves across a block, WHICH IS CORRECT — the user really did
        //          include something they had not before. Reporting it together with the two above
        //          is what made the first draft of this table look like instability when the OCR
        //          had not moved at all.
        var detSame = b.Boxes.Count == v.Boxes.Count &&
                      b.Boxes.Zip(v.Boxes).All(pair => pair.First == pair.Second);
        var ocrSame = b.All.Count == v.All.Count &&
                      b.All.Zip(v.All).All(pair =>
                          pair.First.Bounds == pair.Second.Bounds &&
                          pair.First.Text == pair.Second.Text);

        var heightDeltas = new List<double>();
        var used = new bool[v.Boxes.Count];
        var matchedBoxes = 0;
        foreach (var box in b.Boxes)
        {
            var best = -1;
            var bestIou = 0.35;
            for (var i = 0; i < v.Boxes.Count; i++)
            {
                if (used[i]) continue;
                var iou = RoiIou(box, v.Boxes[i]);
                if (iou <= bestIou) continue;
                bestIou = iou;
                best = i;
            }

            if (best < 0) continue;
            used[best] = true;
            matchedBoxes++;
            heightDeltas.Add(
                Math.Abs(v.Boxes[best].Height - box.Height) / Math.Max(1, box.Height) * 100);
        }

        heightDeltas.Sort();

        int same = 0, changed = 0, lost = 0;
        foreach (var block in b.Kept)
        {
            var hit = v.Kept
                .Select(other => (other, iou: RoiIou(block.Bounds, other.Bounds)))
                .Where(pair => pair.iou > 0.35)
                .OrderByDescending(pair => pair.iou)
                .Select(pair => (string?)pair.other.Text)
                .FirstOrDefault();

            if (hit is null) lost++;
            else if (hit.Trim() == block.Text.Trim()) same++;
            else changed++;
        }

        Console.WriteLine(
            $"{side,-5} +{step,-4} {Fmt(v.Logical),-14} {Fmt(v.Analysis),-16} " +
            $"{(crossed ? "CROSS" : "same "),-6} {(crossed ? "-" : cropIdentical ? "IDENT" : "DIFF!"),-6} " +
            $"{(detSame ? "same" : $"{matchedBoxes}/{v.Boxes.Count - matchedBoxes}/{b.Boxes.Count - matchedBoxes}"),-10} " +
            $"{RoiPct(heightDeltas, 0.5),4:0.0}/{RoiPct(heightDeltas, 0.9),4:0.0}/{RoiPct(heightDeltas, 1.0),4:0.0}  " +
            $"{(ocrSame ? "same" : "MOVED"),-6} " +
            $"{v.Kept.Count,-5} {same}/{changed}/{lost,-8} {v.Straddling}");
    }

    static bool SnapAlignedIdentical(SkiaSharp.SKBitmap a, SkiaSharp.SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        return a.GetPixelSpan().SequenceEqual(b.GetPixelSpan());
    }
}

// Candidate 2, measured but not shipped: fix what the DETECTOR is shown to the whole source image,
// and let the user's selection decide only which of its boxes go any further.
//
//   Logical ROI    what the user dragged. Still what the answer is about.
//   Analysis frame the whole source image, every time. It does not move when the selection does.
//
// Candidate 1 (--roi-snap) made the detector's input identical only while the selection stayed
// inside one grid cell, and paid for it three ways: a bigger jump when a line was crossed, text
// truncated at the Analysis ROI's edge, and unrelated content inside it changing how the rest was
// grouped. This shape has no cell to cross — the frame is the same frame for every selection on the
// image — and nothing is truncated, because nothing is cropped. The selection is applied AFTER
// detection, so recognition and grouping still see only what the user framed, which is what keeps
// the unrelated content out.
//
// Deliberately NOT what this measures: recognising the whole screen. Only detection is full-frame.
//
// The risk it has instead is resolution. ImgResize is a cap on the detector's long side, so a small
// ROI is usually handed over at 1.0 while a whole 4K screen cannot be — and a box found on a
// downscaled page is a different box. That is question B, and it is the one that decides this.
//
//   A  stability: does the full-frame arm stop moving when the selection grows?
//   B  cost:      what does the same ROI lose against today's crop-the-ROI-and-detect baseline,
//                 and what does it cost in time and memory?
if (args[0] == "--roi-fullframe")
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine(
            "usage: --roi-fullframe <image> [--roi X,Y,W,H] [--grow up,down,left,right,all] [--steps 1,2,4,8,16,32]");
        return 1;
    }

    var ffPath = args[1];
    if (!File.Exists(ffPath)) { Console.Error.WriteLine($"(missing) {ffPath}"); return 1; }

    using var ffSource = new Bitmap(ffPath);

    var ffRect = new System.Drawing.Rectangle(
        ffSource.Width / 8, ffSource.Height / 8,
        ffSource.Width * 3 / 4, ffSource.Height * 3 / 4);

    var ffRoiFlag = Array.IndexOf(args, "--roi");
    if (ffRoiFlag >= 0 && ffRoiFlag + 1 < args.Length)
    {
        var parts = args[ffRoiFlag + 1].Split(',');
        if (parts.Length != 4 || !parts.All(part => int.TryParse(part, out _)))
        {
            Console.Error.WriteLine("usage: --roi X,Y,W,H");
            return 1;
        }

        ffRect = new System.Drawing.Rectangle(
            int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
    }

    var ffGrowFlag = Array.IndexOf(args, "--grow");
    var ffGrowSides = ffGrowFlag >= 0 && ffGrowFlag + 1 < args.Length
        ? args[ffGrowFlag + 1].Split(',').Select(side => side.Trim().ToLowerInvariant()).ToArray()
        : new[] { "down", "right", "all" };

    // The corpus has no screen wide enough for ImgResize to shrink, and that shrink is the whole of
    // question B's risk. This forces the full-frame arm's detection to a smaller cap — 1024 on a
    // 1900px screen is the 0.53 a 4K screen would get — WITHOUT touching the baseline arm, which is
    // what makes the two comparable.
    var ffSizeFlag = Array.IndexOf(args, "--ff-size");
    int? ffSize = ffSizeFlag >= 0 && ffSizeFlag + 1 < args.Length
        ? int.Parse(args[ffSizeFlag + 1])
        : null;

    var ffExplain = args.Contains("--explain");

    var ffStepFlag = Array.IndexOf(args, "--steps");
    var ffSteps = ffStepFlag >= 0 && ffStepFlag + 1 < args.Length
        ? args[ffStepFlag + 1].Split(',').Select(int.Parse).ToArray()
        : new[] { 1, 2, 4, 8, 16, 32 };

    using var ffEngine = new OnnxOcrEngine();

    Console.WriteLine($"IMAGE: {ffPath}  ({ffSource.Width}x{ffSource.Height})");
    Console.WriteLine($"BASE ROI: {ffRect.X},{ffRect.Y} {ffRect.Width}x{ffRect.Height}");
    Console.WriteLine("FLOW: 截圖翻譯 (detect=screenshot)");
    Console.WriteLine();

    // Nothing below means anything unless recognition over supplied boxes is the same recognition
    // the app runs. Checked here rather than asserted in a comment: the detector's own boxes on the
    // ROI crop, read back through the seam, against what RecognizeAsync makes of the same crop.
    using (var seamCrop = ffSource.Clone(ffRect, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
    {
        using var seamSession = ffEngine.BeginDetection(seamCrop, harnessLanguage);
        var viaSeam = seamSession.Recognize(Enumerable.Range(0, seamSession.Boxes.Count).ToList());
        var viaApp = await ffEngine.RecognizeAsync(seamCrop, harnessLanguage);

        var seamText = string.Join(" | ", viaSeam.Select(block => block.Text));
        var appText = string.Join(" | ", viaApp.Select(block => block.Text));
        Console.WriteLine(
            $"SEAM CHECK  boxes={seamSession.Boxes.Count}  seam={viaSeam.Count} app={viaApp.Count}  " +
            (seamText == appText ? "text IDENTICAL" : "text DIFFERS"));
        if (seamText != appText)
        {
            Console.WriteLine($"   seam: {seamText}");
            Console.WriteLine($"   app : {appText}");
        }

        Console.WriteLine();
    }

    // The one detection the whole mode is built on. Timed and measured alone, because in a shipped
    // version of this it would run once per capture no matter how the selection then moved.
    GC.Collect();
    GC.WaitForPendingFinalizers();
    var ffBeforeMemory = Environment.WorkingSet;
    var ffDetectWatch = System.Diagnostics.Stopwatch.StartNew();
    using var ffSession = ffEngine.BeginDetection(ffSource, harnessLanguage, ffSize);
    var ffAllBoxes = ffSession.Boxes;
    ffDetectWatch.Stop();
    var ffAfterMemory = Environment.WorkingSet;

    using (var ffSk = OnnxOcrEngine.ConvertToSkBitmap(ffSource))
    using (var ffFrame = OnnxOcrEngine.CreateDetectorFrame(ffSk, ffSize))
    {
        // The isotropic downscale the engine applied before the library saw the image. It is 1.0
        // whenever the page already fits, and this is the line that says whether question B has
        // anything to answer on this image.
        var ffAligned = ffFrame.Bitmap;

        Console.WriteLine(
            $"FULL FRAME DETECT  canvas={ffAligned.Width}x{ffAligned.Height}  " +
            $"scale={ffFrame.RatioX:0.0000}x{ffFrame.RatioY:0.0000}  boxes={ffAllBoxes.Count}  " +
            $"{ffDetectWatch.ElapsedMilliseconds}ms  workingSet {(ffAfterMemory - ffBeforeMemory) / 1024 / 1024:+0;-0;0}MB");
    }

    Console.WriteLine();
    Console.WriteLine("A. STABILITY — the full-frame arm against its own base ROI");
    Console.WriteLine(
        "grow  step  size        kept  strad  det =/+/-      dH% 50/90/max   rec =/x/0     groups");
    Console.WriteLine(new string('-', 104));

    var ffBase = await FullFrameProbe(ffRect);
    Console.WriteLine(
        $"BASE  -     {ffBase.Roi.Width}x{ffBase.Roi.Height,-7} {ffBase.Boxes.Count,-5} {ffBase.Straddling,-6} " +
        $"-              -               -             {ffBase.Groups}");

    var ffVariants = new List<(string Side, int Step, FullFrameProbed Probe)>();
    foreach (var side in ffGrowSides)
    {
        foreach (var step in ffSteps)
        {
            var grown = FullFrameGrow(ffRect, side, step);
            if (grown == ffRect)
            {
                Console.WriteLine($"{side,-5} +{step,-4} (no room left in the source image)");
                continue;
            }

            var probe = await FullFrameProbe(grown);
            ffVariants.Add((side, step, probe));
            FullFrameStabilityReport(side, step, ffBase, probe);
        }
    }

    Console.WriteLine();
    Console.WriteLine("B. COST & ACCURACY — the full-frame arm against today's crop-the-ROI baseline");
    Console.WriteLine("   det ff/roi is how many boxes each arm has INSIDE the logical ROI; the rest");
    Console.WriteLine("   compares the full-frame arm's answer to the baseline's on the same ROI.");
    Console.WriteLine(
        "roi                canvas(roi)  scale(roi)    det ff/roi  =/+/-        dH% 50/90/max   rec =/x/0     chars ff/roi   ff rec ms  roi det+rec ms");
    Console.WriteLine(new string('-', 152));

    FullFrameCostReport(ffBase);
    foreach (var (_, _, probe) in ffVariants) FullFrameCostReport(probe);

    return 0;

    System.Drawing.Rectangle FullFrameGrow(System.Drawing.Rectangle rect, string side, int by)
    {
        var grow = side switch
        {
            "up" => (L: 0, T: by, R: 0, B: 0),
            "down" => (L: 0, T: 0, R: 0, B: by),
            "left" => (L: by, T: 0, R: 0, B: 0),
            "right" => (L: 0, T: 0, R: by, B: 0),
            _ => (L: by, T: by, R: by, B: by),
        };

        var x = Math.Max(0, rect.X - grow.L);
        var y = Math.Max(0, rect.Y - grow.T);
        var right = Math.Min(ffSource.Width, rect.Right + grow.R);
        var bottom = Math.Min(ffSource.Height, rect.Bottom + grow.B);

        return new System.Drawing.Rectangle(x, y, right - x, bottom - y);
    }

    // The candidate itself: filter the one full-frame detection down to the selection, then read and
    // group only what is left. Everything is already in source-image coordinates, because the
    // detection was run on the source image.
    async Task<FullFrameProbed> FullFrameProbe(System.Drawing.Rectangle logical)
    {
        var logicalRect = new System.Windows.Rect(
            logical.X, logical.Y, logical.Width, logical.Height);

        // Centre-inside, the same rule --roi-snap filters with, so the two candidates' numbers can
        // be read against each other.
        var kept = Enumerable.Range(0, ffAllBoxes.Count)
            .Where(index => logicalRect.Contains(
                new System.Windows.Point(
                    ffAllBoxes[index].Bounds.X + ffAllBoxes[index].Bounds.Width / 2,
                    ffAllBoxes[index].Bounds.Y + ffAllBoxes[index].Bounds.Height / 2)))
            .ToList();

        // Boxes the selection cuts through. Under this shape they arrive WHOLE — the detector saw
        // the entire line — which is exactly what --roi-snap could not do, so the count is here to
        // show how often that difference is in play rather than to flag a problem.
        var straddling = ffAllBoxes.Count(box =>
            box.Bounds.IntersectsWith(logicalRect) && !logicalRect.Contains(box.Bounds));

        var recognitionWatch = System.Diagnostics.Stopwatch.StartNew();
        var read = ffSession.Recognize(kept);
        recognitionWatch.Stop();

        var groups = OcrTextBlockGrouper.Group(read, harnessGroupingBaseline).Count;

        // Today's behaviour, for the same selection: crop, detect on the crop, recognise, group.
        using var crop = ffSource.Clone(logical, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var baselineWatch = System.Diagnostics.Stopwatch.StartNew();
        var baselineBoxes = ffEngine.DetectBoxesOnly(crop, harnessLanguage)
            .Select(box => new System.Windows.Rect(
                box.Bounds.X + logical.X, box.Bounds.Y + logical.Y, box.Bounds.Width, box.Bounds.Height))
            .ToList();
        var baselineRead = await ffEngine.RecognizeAsync(crop, harnessLanguage);
        baselineWatch.Stop();

        var baselineBlocks = baselineRead
            .Select(block => (
                Bounds: new System.Windows.Rect(
                    block.Bounds.X + logical.X, block.Bounds.Y + logical.Y,
                    block.Bounds.Width, block.Bounds.Height),
                block.Text))
            .ToList();

        using var baselineSk = OnnxOcrEngine.ConvertToSkBitmap(crop);
        using var baselineFrame = OnnxOcrEngine.CreateDetectorFrame(baselineSk, null);
        var baselineAligned = baselineFrame.Bitmap;

        return new FullFrameProbed(
            logical,
            kept.Select(index => ffAllBoxes[index].Bounds).ToList(),
            straddling,
            read.Select(block => (block.Bounds, block.Text)).ToList(),
            groups,
            recognitionWatch.ElapsedMilliseconds,
            baselineBoxes,
            baselineBlocks,
            baselineWatch.ElapsedMilliseconds,
            $"{baselineAligned.Width}x{baselineAligned.Height}",
            $"{baselineFrame.RatioX:0.0000}x{baselineFrame.RatioY:0.0000}");
    }

    void FullFrameStabilityReport(string side, int step, FullFrameProbed b, FullFrameProbed v)
    {
        var (matched, added, missing, heightDeltas) = FullFrameMatchBoxes(
            b.Boxes,
            v.Boxes);
        var (same, changed, lost) = FullFrameMatchText(b.Blocks, v.Blocks);

        Console.WriteLine(
            $"{side,-5} +{step,-4} {v.Roi.Width}x{v.Roi.Height,-7} {v.Boxes.Count,-5} {v.Straddling,-6} " +
            $"{matched}/{added}/{missing,-10} " +
            $"{RoiPct(heightDeltas, 0.5),4:0.0}/{RoiPct(heightDeltas, 0.9),4:0.0}/{RoiPct(heightDeltas, 1.0),4:0.0}   " +
            $"{same}/{changed}/{lost,-9} {v.Groups}{(v.Groups == b.Groups ? "" : "  <- changed")}");
    }

    void FullFrameCostReport(FullFrameProbed p)
    {
        // The baseline's boxes are already only inside the ROI — it never saw anything else — so the
        // comparison is the full-frame arm's kept boxes against all of them.
        var (matched, added, missing, heightDeltas) = FullFrameMatchBoxes(
            p.BaselineBoxes,
            p.Boxes);
        var (same, changed, lost) = FullFrameMatchText(p.BaselineBlocks, p.Blocks);

        Console.WriteLine(
            $"{p.Roi.X},{p.Roi.Y} {p.Roi.Width}x{p.Roi.Height,-8} {p.BaselineCanvas,-12} {p.BaselineScale,-13} " +
            $"{p.Boxes.Count}/{p.BaselineBoxes.Count,-9} " +
            $"{matched}/{added}/{missing,-11} " +
            $"{RoiPct(heightDeltas, 0.5),4:0.0}/{RoiPct(heightDeltas, 0.9),4:0.0}/{RoiPct(heightDeltas, 1.0),4:0.0}   " +
            $"{same}/{changed}/{lost,-9} " +
            $"{FullFrameChars(p.Blocks)}/{FullFrameChars(p.BaselineBlocks),-9} " +
            $"{p.RecognitionMs,-10} {p.BaselineMs}");

        if (!ffExplain) return;

        // Every block the two arms disagree about, so the counts above can be read rather than
        // trusted. Baseline first, because it is what ships today.
        foreach (var block in p.BaselineBlocks)
        {
            var hit = p.Blocks
                .Select(other => (other, iou: RoiIou(block.Bounds, other.Bounds)))
                .Where(pair => pair.iou > 0.35)
                .OrderByDescending(pair => pair.iou)
                .Select(pair => (string?)pair.other.Text)
                .FirstOrDefault();

            if (hit is null)
            {
                // Not just that the baseline had it and the full-frame arm did not, but WHY. Three
                // different things look identical in a "roi only" line and want different answers:
                // the selection edge cut across the line's width, it cut through the line's height
                // (every glyph sliced), or the full frame merged the fragment into a longer line
                // whose centre then fell outside. The box that overlaps it, and where its centre is,
                // says which.
                var overlapping = ffAllBoxes
                    .Select((box, index) => (box, index, iou: RoiIou(block.Bounds, box.Bounds)))
                    .Where(candidate => candidate.box.Bounds.IntersectsWith(block.Bounds))
                    .OrderByDescending(candidate => candidate.iou)
                    .FirstOrDefault();

                var logical = new System.Windows.Rect(p.Roi.X, p.Roi.Y, p.Roi.Width, p.Roi.Height);
                var why = "no full-frame box overlaps it";
                if (overlapping.box.Bounds.Width > 0)
                {
                    var ffBox = overlapping.box.Bounds;
                    var centre = new System.Windows.Point(
                        ffBox.X + ffBox.Width / 2, ffBox.Y + ffBox.Height / 2);
                    var horizontallyOut = centre.X < logical.X || centre.X > logical.Right;
                    var verticallyOut = centre.Y < logical.Y || centre.Y > logical.Bottom;
                    why =
                        $"ff box {ffBox.X:0},{ffBox.Y:0} {ffBox.Width:0}x{ffBox.Height:0} " +
                        $"centre {centre.X:0},{centre.Y:0} out={(horizontallyOut ? "L/R" : "")}{(verticallyOut ? "T/B" : "")}" +
                        $" wider={(ffBox.Width > block.Bounds.Width * 1.2 ? "yes" : "no")}";
                }

                Console.WriteLine(
                    $"      roi only : {block.Text}");
                Console.WriteLine(
                    $"                 roi box {block.Bounds.X:0},{block.Bounds.Y:0} " +
                    $"{block.Bounds.Width:0}x{block.Bounds.Height:0}  |  {why}");
            }
            else if (hit.Trim() != block.Text.Trim())
            {
                Console.WriteLine($"      roi      : {block.Text}");
                Console.WriteLine($"      fullframe: {hit}");
            }
        }

        foreach (var block in p.Blocks)
        {
            var matchedByBaseline = p.BaselineBlocks.Any(other => RoiIou(block.Bounds, other.Bounds) > 0.35);
            if (!matchedByBaseline) Console.WriteLine($"      ff only  : {block.Text}");
        }
    }

    static int FullFrameChars(IReadOnlyList<(System.Windows.Rect Bounds, string Text)> blocks) =>
        blocks.Sum(block => block.Text.Count(character => !char.IsWhiteSpace(character)));

    // Greedy IoU pairing, the same 0.35 the other two ROI modes use, so "matched" means the same
    // thing in all three tables.
    static (int Matched, int Added, int Missing, List<double> HeightDeltas) FullFrameMatchBoxes(
        IReadOnlyList<System.Windows.Rect> reference, IReadOnlyList<System.Windows.Rect> candidate)
    {
        var used = new bool[candidate.Count];
        var heightDeltas = new List<double>();
        var matched = 0;

        foreach (var box in reference)
        {
            var best = -1;
            var bestIou = 0.35;
            for (var i = 0; i < candidate.Count; i++)
            {
                if (used[i]) continue;
                var iou = RoiIou(box, candidate[i]);
                if (iou <= bestIou) continue;
                bestIou = iou;
                best = i;
            }

            if (best < 0) continue;
            used[best] = true;
            matched++;
            heightDeltas.Add(Math.Abs(candidate[best].Height - box.Height) / Math.Max(1, box.Height) * 100);
        }

        heightDeltas.Sort();
        return (matched, candidate.Count - matched, reference.Count - matched, heightDeltas);
    }

    static (int Same, int Changed, int Lost) FullFrameMatchText(
        IReadOnlyList<(System.Windows.Rect Bounds, string Text)> reference,
        IReadOnlyList<(System.Windows.Rect Bounds, string Text)> candidate)
    {
        int same = 0, changed = 0, lost = 0;
        foreach (var block in reference)
        {
            var hit = candidate
                .Select(other => (other, iou: RoiIou(block.Bounds, other.Bounds)))
                .Where(pair => pair.iou > 0.35)
                .OrderByDescending(pair => pair.iou)
                .Select(pair => (string?)pair.other.Text)
                .FirstOrDefault();

            if (hit is null) lost++;
            else if (hit.Trim() == block.Text.Trim()) same++;
            else changed++;
        }

        return (same, changed, lost);
    }
}

if (args[0] == "--scale-sweep")
{
    var sweepArgs = args.Skip(1).ToList();

    if (OnnxOcrEngine.DetectorOverride is null)
        Console.WriteLine("detector: shipped (ocrmodels/onnx/shared/det.onnx)");

    using var sweepOcr = new OcrService();

    foreach (var path in sweepArgs)
    {
        if (!File.Exists(path)) { Console.WriteLine($"(missing) {path}"); continue; }

        using var image = new Bitmap(path);
        var native = Math.Max(image.Width, image.Height);
        var aspect = image.Height > 0 ? (double)image.Width / image.Height : 0;

        Console.WriteLine(new string('=', 78));
        Console.WriteLine(
            $"{Path.GetFileName(path)}  {image.Width}x{image.Height}  ratio={aspect:0.00}");

        // What the app itself would pick for this block, so the sweep can be read against it.
        var (primary, fallbacks) = RealtimeDetectorSize.For(image.Width, image.Height, harnessMode);
        Console.WriteLine($"  app picks: primary={primary} fallbacks=[{string.Join(",", fallbacks)}]");

        // Stepped in whole percent, not by adding 0.05 to a double: the accumulated error moved
        // 0.50 to 0.4999, which rounds to a different 32-pixel stride and quietly sweeps a size the
        // app would never ask for — right where this measurement is most sensitive.
        for (var percent = 30; percent <= 100; percent += 5)
        {
            var fraction = percent / 100.0;
            // Same rounding the app uses, so the numbers here are the ones it would really ask for.
            var size = fraction >= 1.0 ? native : Math.Max(320, ((int)(native * fraction) + 31) / 32 * 32);

            List<OcrTextBlock>? blocks = null;
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 0; attempt < 20 && blocks is null; attempt++)
            {
                elapsed.Restart();
                blocks = await sweepOcr.TryRecognizeAsync(image, "EN", size);
            }

            elapsed.Stop();

            // A watched region does not use everything the engine hands back: a box as tall as the
            // block is the detector having collapsed, and a one-character reading is scenery. Both
            // are dropped in RealtimeTranslationSession, after OcrService, so a sweep that skips
            // them reports sizes as working that the application would have called empty — measured
            // on the shipped detector, five of these frames read their subtitle at the very size the
            // application had recorded as reading nothing.
            var kept = blocks?
                .Where(block =>
                    !CollapsedDetection.IsCollapsed(block.Bounds.Height, image.Height, block.Text) &&
                    !ShortReadingDetection.IsTooShort(block.Text) &&
                    !ShortReadingDetection.IsUnconvincingShortText(block.Text, block.Confidence))
                .ToList();

            var mark = size == primary ? " <- primary" : fallbacks.Contains(size) ? " <- fallback" : "";
            var text = kept is null || kept.Count == 0
                ? ""
                : "  " + string.Join(" | ", kept.Select(b => b.Text.Replace("\n", " ")));

            // chars and ms, because neither question this sweep exists for can be answered without
            // both: a size is only counted as reading the subtitle past a character floor (icons and
            // scenery misreads come back short), and the size that reads most is not the size to pick
            // if it costs several times as much per pass.
            var chars = kept?.Sum(b => b.Text.Count(c => !char.IsWhiteSpace(c))) ?? 0;
            var dropped = (blocks?.Count ?? 0) - (kept?.Count ?? 0);

            Console.WriteLine(
                $"  {fraction:0.00} -> {size,5} : {kept?.Count ?? -1} box dropped={dropped} chars={chars,3} " +
                $"{elapsed.ElapsedMilliseconds,5}ms{mark}{text}");
        }
    }

    return 0;
}

using var ocr = new OcrService();
var translator = new GTranslateProvider(new MicrosoftTranslator());

foreach (var path in args)
{
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"IMAGE: {path}");
    if (!System.IO.File.Exists(path))
    {
        Console.WriteLine("  (file not found)");
        continue;
    }

    using var bitmap = new Bitmap(path);
    List<OcrTextBlock> blocks;
    try
    {
        blocks = await ocr.RecognizeAsync(bitmap, "EN");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  OCR FAILED: {ex.Message}");
        continue;
    }

    Console.WriteLine($"  grouped blocks: {blocks.Count}");
    for (var i = 0; i < blocks.Count; i++)
    {
        var b = blocks[i];
        Console.WriteLine(
            $"  [{i}] bounds=({b.Bounds.X:0},{b.Bounds.Y:0},{b.Bounds.Width:0},{b.Bounds.Height:0}) lines={b.Lines.Count}");
        Console.WriteLine($"       EN: {b.Text}");
    }

    try
    {
        var (translated, _) = await translator.TranslateAsync(blocks, "EN", "ZH-HANT", "");
        Console.WriteLine("  --- translations (Microsoft) ---");
        foreach (var t in translated)
            Console.WriteLine($"  EN: {t.OriginalText}\n  ZH: {t.TranslatedText}\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  TRANSLATE FAILED: {ex.Message}");
    }
}

// Shared by --roi-stability and --roi-snap, which compare the same things about the same boxes.
static double RoiPct(IReadOnlyList<double> sorted, double at) =>
    sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(at * (sorted.Count - 1)))];

static double RoiIou(System.Windows.Rect a, System.Windows.Rect b)
{
    var overlap = System.Windows.Rect.Intersect(a, b);
    if (overlap.IsEmpty) return 0;
    var inter = overlap.Width * overlap.Height;
    return inter / (a.Width * a.Height + b.Width * b.Height - inter);
}

return 0;

/// <summary>
/// One Logical ROI answered under both arms, in source-image coordinates. The full-frame arm boxes
/// are a filtered view of ONE detection shared by every ROI; the baseline has its own.
/// </summary>
internal sealed record FullFrameProbed(
    System.Drawing.Rectangle Roi,
    IReadOnlyList<System.Windows.Rect> Boxes,
    int Straddling,
    IReadOnlyList<(System.Windows.Rect Bounds, string Text)> Blocks,
    int Groups,
    long RecognitionMs,
    IReadOnlyList<System.Windows.Rect> BaselineBoxes,
    IReadOnlyList<(System.Windows.Rect Bounds, string Text)> BaselineBlocks,
    long BaselineMs,
    string BaselineCanvas,
    string BaselineScale);

/// <summary>
/// One (Logical, Analysis) pair's answer, in source-image coordinates. <c>All</c> is everything the
/// Analysis ROI produced; <c>Kept</c> is what survives filtering back to the Logical one.
/// </summary>
internal sealed record SnapProbed(
    System.Drawing.Rectangle Logical,
    System.Drawing.Rectangle Analysis,
    SkiaSharp.SKBitmap Aligned,
    IReadOnlyList<System.Windows.Rect> Boxes,
    IReadOnlyList<(System.Windows.Rect Bounds, string Text)> All,
    IReadOnlyList<(System.Windows.Rect Bounds, string Text)> Kept,
    int Straddling);

/// <summary>
/// One ROI's answer at every layer --roi-stability compares, in source-image coordinates.
/// </summary>
internal sealed record RoiProbed(
    System.Drawing.Rectangle Roi,
    string Canvas,
    string Scale,
    SkiaSharp.SKBitmap Aligned,
    IReadOnlyList<(System.Windows.Rect Bounds, float Score)> Boxes,
    IReadOnlyList<(System.Windows.Rect Bounds, string Text)> Blocks,
    int Groups);
