using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using SkiaSharp;

// Where a symbol at the start or end of a horizontal line gets lost: 行首／行尾的「……」「——」「。」「！？」.
//
// Every framing of every image is read the way the app reads it — the realtime sizes RealtimeDetectorSize
// picks for Subtitle and for Panel (primary and fallbacks), and the screenshot flow — and each stage of
// the pipeline is kept so a loss can be pinned on the first one that does not have the symbol:
//
//   raw      what recognition returned for each detector box, before any filter
//   filters  ApplyBlockFilters (inside the engine)
//   unconv   RejectUnconvincingBlocks (realtime, before grouping)
//   group    GroupRealtime / GroupScreenshot
//   final    RejectCollapsedBlocks + RejectShortReadings (realtime only)
//
// A loss already present in `raw` is then told apart by the pixels: (a) ink of the line's own colour
// carries on past the box (the box stopped short of it), (b) the box reaches the ink and recognition
// still did not say it. A loss that appears later is (c), and the stage is named.
//
// Manifest, one image per line, tab separated:   path <TAB> X,Y,W,H | full <TAB> line / line / ...
// The ROI is what the user dragged; --jitter reads it again with each edge moved 8px either way.
internal static class EdgePunctuationProbe
{
    private const int Jitter = 8;

    internal static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--regress") return Regress(args[1..]);
        if (args.Length > 0 && args[0] == "--cost") return Cost(args[1..]);
        var verbose = args.Contains("--verbose");
        if (args.Contains("--ends-trace")) HorizontalLineEnds.Trace = s => Console.WriteLine("        ends " + s);
        var jitter = args.Contains("--jitter");
        var flows = args.Contains("--shot-only") ? new[] { "shot" }
            : args.Contains("--realtime-only") ? new[] { "sub", "panel" }
            : new[] { "sub", "panel", "shot" };
        args = [.. args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))];
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --edge-punct <lang> <manifest.tsv> [--jitter] [--verbose] [--shot-only|--realtime-only]");
            return 1;
        }

        var language = args[0];
        var manifest = File.ReadAllLines(args[1], Encoding.UTF8)
            .Where(l => l.Trim().Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .ToList();
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(args[1]))!;

        using var engine = new OnnxOcrEngine();
        var tally = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var perImage = new List<string>();

        foreach (var row in manifest)
        {
            var path = Path.IsPathRooted(row[0]) ? row[0] : Path.Combine(baseDir, row[0]);
            using var image = new Bitmap(path);
            var roi = row[1].Trim() == "full"
                ? new Rectangle(0, 0, image.Width, image.Height)
                : ParseRect(row[1]);
            var expected = row[2].Split(" / ").Select(s => s.Trim()).ToArray();
            Console.WriteLine(new string('=', 100));
            Console.WriteLine($"IMAGE {Path.GetFileName(path)}  roi={roi.X},{roi.Y},{roi.Width},{roi.Height}");
            foreach (var e in expected) Console.WriteLine($"  expect: {e}");

            var ok = 0; var total = 0; var boundsOk = 0; var boundsTotal = 0;
            foreach (var (tag, rect) in Framings(roi, image.Size, jitter))
            {
                using var crop = image.Clone(rect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var pixels = OnnxOcrEngine.ConvertToSkBitmap(crop);
                foreach (var flow in flows)
                {
                    foreach (var size in Sizes(flow, crop.Width, crop.Height))
                    {
                        var r = Read(engine, crop, pixels, language, flow, size);
                        var verdicts = new List<string>();
                        foreach (var line in expected)
                        {
                            var v = Judge(line, r, pixels);
                            total++;
                            if (v.Text == "ok") ok++;
                            if (v.Bounds is not null)
                            {
                                boundsTotal++;
                                if (v.Bounds == true) boundsOk++;
                            }
                            verdicts.Add(v.Summary);
                            tally[$"{v.Text}"] = tally.GetValueOrDefault(v.Text) + 1;
                        }

                        Console.WriteLine($"  {tag,-7} {flow,-5} {(size?.ToString() ?? "shot"),5}  {string.Join("  ||  ", verdicts)}");
                        if (verbose) Dump(r);
                    }
                }
            }

            var summary = $"{Path.GetFileName(path)}: text ok {ok}/{total}, bounds cover {boundsOk}/{boundsTotal}";
            perImage.Add(summary);
            Console.WriteLine("  SUMMARY " + summary);
        }

        Console.WriteLine();
        Console.WriteLine("TOTALS");
        foreach (var line in perImage) Console.WriteLine("  " + line);
        foreach (var (k, v) in tally) Console.WriteLine($"  {k,-40} {v}");
        return 0;
    }

    private static Rectangle ParseRect(string text)
    {
        var p = text.Split(',').Select(int.Parse).ToArray();
        return new Rectangle(p[0], p[1], p[2], p[3]);
    }

    private static IEnumerable<(string Tag, Rectangle Rect)> Framings(Rectangle roi, Size image, bool jitter)
    {
        var seen = new HashSet<Rectangle>();
        IEnumerable<(string, int, int, int, int)> moves = jitter
            ? [
                ("base", 0, 0, 0, 0),
                ("L-8", -Jitter, 0, 0, 0), ("L+8", Jitter, 0, 0, 0),
                ("T-8", 0, -Jitter, 0, 0), ("T+8", 0, Jitter, 0, 0),
                ("R+8", 0, 0, Jitter, 0), ("R-8", 0, 0, -Jitter, 0),
                ("B+8", 0, 0, 0, Jitter), ("B-8", 0, 0, 0, -Jitter),
                ("grow8", -Jitter, -Jitter, Jitter, Jitter), ("shrink8", Jitter, Jitter, -Jitter, -Jitter),
            ]
            : [("base", 0, 0, 0, 0)];
        foreach (var (tag, dl, dt, dr, db) in moves)
        {
            var left = Math.Max(0, roi.Left + dl);
            var top = Math.Max(0, roi.Top + dt);
            var right = Math.Min(image.Width, roi.Right + dr);
            var bottom = Math.Min(image.Height, roi.Bottom + db);
            var rect = Rectangle.FromLTRB(left, top, right, bottom);
            if (seen.Add(rect)) yield return (tag, rect);
        }
    }

    private static IEnumerable<int?> Sizes(string flow, int width, int height)
    {
        if (flow == "shot") { yield return null; yield break; }
        var (primary, fallbacks) = RealtimeDetectorSize.For(
            width, height, flow == "sub" ? RealtimeBlockMode.Subtitle : RealtimeBlockMode.Panel);
        yield return primary;
        foreach (var f in fallbacks) yield return f;
    }

    internal sealed record Reading(
        string Flow,
        int? Size,
        IReadOnlyList<(System.Windows.Rect Bounds, float Score)> Boxes,
        IReadOnlyList<(System.Windows.Rect Bounds, string Text, float Score)> Raw,
        IReadOnlyList<OcrTextBlock> Filters,
        IReadOnlyList<OcrTextBlock> Unconvincing,
        IReadOnlyList<OcrTextBlock> Grouped,
        IReadOnlyList<OcrTextBlock> Final,
        Func<System.Windows.Rect, float?> Probability);

    internal static Reading Read(
        OnnxOcrEngine engine, Bitmap crop, SKBitmap pixels, string language, string flow, int? size)
    {
        using var session = engine.BeginDetection(crop, language, size, repairRows: true, keepProbability: true);
        var blocks = session.Recognize([.. Enumerable.Range(0, session.Boxes.Count)], out var recognised).ToList();
        var raw = recognised
            .Select(b =>
            {
                var xs = b.BoxPoints.Select(p => p.X).ToArray();
                var ys = b.BoxPoints.Select(p => p.Y).ToArray();
                return (new System.Windows.Rect(xs.Min(), ys.Min(), xs.Max() - xs.Min(), ys.Max() - ys.Min()),
                    string.Concat(b.Chars ?? []), b.CharScores is { Length: > 0 } s ? s.Average() : 0f);
            })
            .OrderBy(b => b.Item1.Top).ThenBy(b => b.Item1.Left)
            .ToList();

        List<OcrTextBlock> unconv, grouped, final;
        if (flow == "shot")
        {
            unconv = blocks;
            grouped = TiltedOverlap.Level(OcrService.GroupScreenshot(pixels, blocks, GroupingProfile.General));
            final = grouped;
        }
        else
        {
            var mode = flow == "sub" ? RealtimeBlockMode.Subtitle : RealtimeBlockMode.Panel;
            unconv = OcrService.RejectUnconvincingBlocks(blocks);
            grouped = TiltedOverlap.Level(OcrService.GroupRealtime(blocks, crop.Height, mode));
            final = RealtimeTranslationSession.RejectShortReadings(
                RealtimeTranslationSession.RejectCollapsedBlocks(grouped, crop.Height, 0), 0);
        }

        // The map dies with the session, so whatever the judge may ask about is read now: the strips
        // beside every box.
        var cache = new Dictionary<System.Windows.Rect, float?>();
        foreach (var box in session.Boxes)
        {
            var h = box.Bounds.Height;
            foreach (var r in Around(box.Bounds, h))
                cache[r] = session.MaxProbability(r);
        }

        return new Reading(flow, size, session.Boxes, raw, blocks, unconv, grouped, final,
            r => cache.TryGetValue(r, out var v) ? v : null);
    }

    // The strips beside a box that the judge asks the map about: left and right, a line height wide.
    private static IEnumerable<System.Windows.Rect> Around(System.Windows.Rect box, double h)
    {
        yield return new System.Windows.Rect(box.Right, box.Top, h * 1.5, box.Height);
        yield return new System.Windows.Rect(Math.Max(0, box.Left - h * 1.5), box.Top, Math.Min(box.Left, h * 1.5), box.Height);
    }

    internal sealed record Verdict(string Text, bool? Bounds, string Summary);

    // ---------------------------------------------------------------------------------------------
    // Judging one expected line against one reading.

    private const string EdgeChars = ".—!?。、)」』(「『~";

    internal static string Normalize(string s)
    {
        var n = s.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder();
        foreach (var ch in n)
        {
            if (char.IsWhiteSpace(ch)) continue;
            var c = ch switch
            {
                '―' or '—' or '–' or '－' or '-' or '‐' or 'ー' or '一' or '─' or '━' => '—',
                '・' or '･' or '·' or '‧' or '∙' or '•' or '，' or ',' => '.',
                '｡' => '。',
                '［' or '[' or '【' => '(',
                ']' or '】' => ')',
                _ => ch,
            };
            // Runs of dots or dashes are one symbol: the count is the recogniser's guess at spacing.
            if ((c == '.' || c == '—') && sb.Length > 0 && sb[^1] == c) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static (string Lead, string Core, string Trail) Split(string normalized)
    {
        var a = 0;
        while (a < normalized.Length && EdgeChars.Contains(normalized[a])) a++;
        var b = normalized.Length;
        while (b > a && EdgeChars.Contains(normalized[b - 1])) b--;
        return (normalized[..a], normalized[a..b], normalized[b..]);
    }

    // Whether a set of blocks says the whole line, edges included, and where its core sits.
    private static (bool Core, bool Lead, bool Trail) Says(string line, IEnumerable<string> texts)
    {
        var (lead, core, trail) = Split(Normalize(line));
        var all = Normalize(string.Concat(texts));
        // A core shorter than two characters is matched with its neighbouring symbols as well.
        var at = all.IndexOf(core, StringComparison.Ordinal);
        if (core.Length == 0) return (false, false, false);
        if (at < 0)
        {
            // The first or last character of the line itself cut off is an edge loss too (二 of
            // 二階堂 in user-4): counted as the lead or the trail going.
            if (core.Length < 4) return (false, false, false);
            var cut = all.IndexOf(core[1..], StringComparison.Ordinal);
            if (cut >= 0)
                return (true, false, trail.Length == 0 ||
                    all[(cut + core.Length - 1)..].StartsWith(trail, StringComparison.Ordinal));
            cut = all.IndexOf(core[..^1], StringComparison.Ordinal);
            if (cut >= 0)
                return (true, lead.Length == 0 || all[..cut].EndsWith(lead, StringComparison.Ordinal), false);
            return (false, false, false);
        }
        var before = all[..at];
        var after = all[(at + core.Length)..];
        return (true, lead.Length == 0 || before.EndsWith(lead, StringComparison.Ordinal),
            trail.Length == 0 || after.StartsWith(trail, StringComparison.Ordinal));
    }

    private static Verdict Judge(string line, Reading r, SKBitmap pixels)
    {
        var (lead, core, trail) = Split(Normalize(line));
        var stages = new (string Name, IEnumerable<string> Texts)[]
        {
            ("raw", r.Raw.Select(b => b.Text)),
            ("filters", Ordered(r.Filters)),
            ("unconv", Ordered(r.Unconvincing)),
            ("group", Ordered(r.Grouped)),
            ("final", Ordered(r.Final)),
        };

        var finalSays = Says(line, stages[^1].Texts);
        var bounds = BoundsCover(line, r, pixels, core);
        var shortName = core.Length > 4 ? core[..4] : core;
        if (finalSays is { Core: true, Lead: true, Trail: true })
            return new Verdict("ok", bounds?.Covered, $"{shortName}:ok{BoundsNote(bounds)}");

        if (!finalSays.Core)
        {
            var coreStage = stages.FirstOrDefault(s => !Says(line, s.Texts).Core).Name;
            return new Verdict($"core-lost@{coreStage}", null, $"{shortName}:CORE-LOST@{coreStage}");
        }

        var which = !finalSays.Lead && !finalSays.Trail ? "both" : !finalSays.Lead ? "lead" : "trail";
        var edge = which == "lead" ? lead : which == "trail" ? trail : lead + "|" + trail;

        // The first stage that no longer says it.
        var first = stages.First(s =>
        {
            var says = Says(line, s.Texts);
            return !(says.Core && (finalSays.Lead || says.Lead) && (finalSays.Trail || says.Trail));
        }).Name;

        if (first != "raw")
            return new Verdict($"c:{which}@{first}", bounds?.Covered, $"{shortName}:c-{which}[{edge}]@{first}");

        // Lost before any filter: did the box stop short of the ink, or reach it and read nothing?
        var cls = InkVerdict(line, r, pixels, core, which);
        return new Verdict($"{cls.Class}:{which}", bounds?.Covered,
            $"{shortName}:{cls.Class}-{which}[{edge}] {cls.Detail}");
    }

    private static string BoundsNote((bool Covered, string Detail)? b) =>
        b is null ? "" : b.Value.Covered ? "" : $"(bounds short {b.Value.Detail})";

    private static IEnumerable<string> Ordered(IEnumerable<OcrTextBlock> blocks) =>
        blocks.OrderBy(b => b.Bounds.Top).ThenBy(b => b.Bounds.Left).Select(b => b.Text);

    // The raw box that holds most of the line's core.
    private static (System.Windows.Rect Bounds, string Text, float Score)? CoreBox(Reading r, string core)
    {
        (System.Windows.Rect, string, float)? best = null;
        var bestScore = 0;
        foreach (var b in r.Raw)
        {
            var n = Normalize(b.Text);
            var common = CommonRun(n, core);
            if (common > bestScore) { bestScore = common; best = b; }
        }
        return bestScore >= Math.Min(2, core.Length) ? best : null;
    }

    private static int CommonRun(string a, string b)
    {
        var best = 0;
        var dp = new int[b.Length + 1];
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = 0;
            for (var j = 1; j <= b.Length; j++)
            {
                var tmp = dp[j];
                dp[j] = a[i - 1] == b[j - 1] ? prev + 1 : 0;
                best = Math.Max(best, dp[j]);
                prev = tmp;
            }
        }
        return best;
    }

    // Where the line's ink really ends, measured on the pixels alone: the text colour taken from the
    // brightest tenth of the core box (every corpus this probe is for has light text), then a walk
    // along the box's band that tolerates gaps of up to 1.5 line heights.
    internal static (int Left, int Right)? InkExtent(SKBitmap pixels, System.Windows.Rect box)
    {
        var top = Math.Max(0, (int)box.Top);
        var bottom = Math.Min(pixels.Height, (int)Math.Ceiling(box.Bottom));
        var left = Math.Max(0, (int)box.Left);
        var right = Math.Min(pixels.Width, (int)Math.Ceiling(box.Right));
        if (bottom - top < 4 || right - left < 4) return null;
        var lums = new List<int>();
        for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
                lums.Add(Lum(pixels.GetPixel(x, y)));
        lums.Sort();
        var bright = lums[(int)(lums.Count * 0.95)];
        var threshold = bright - 50;
        if (threshold < 120) return null;
        var h = bottom - top;
        // The band's middle 70%, so the line above and below cannot lend it ink.
        var bandTop = top + h * 15 / 100;
        var bandBottom = bottom - h * 15 / 100;
        bool Col(int x)
        {
            for (var y = bandTop; y < bandBottom; y++)
                if (Lum(pixels.GetPixel(x, y)) >= threshold) return true;
            return false;
        }

        var maxGap = (int)(h * 1.5);
        var budget = h * 4;
        var inkRight = right - 1;
        while (inkRight >= left && !Col(inkRight)) inkRight--;
        for (int x = right, gap = 0; x < Math.Min(pixels.Width, right + budget) && gap <= maxGap; x++)
        {
            if (Col(x)) { inkRight = x; gap = 0; } else gap++;
        }
        var inkLeft = left;
        while (inkLeft < right && !Col(inkLeft)) inkLeft++;
        for (int x = left - 1, gap = 0; x >= Math.Max(0, left - budget) && gap <= maxGap; x--)
        {
            if (Col(x)) { inkLeft = x; gap = 0; } else gap++;
        }
        return (inkLeft, inkRight + 1);
    }

    private static int Lum(SKColor c) => (c.Red * 77 + c.Green * 150 + c.Blue * 29) >> 8;

    private static (string Class, string Detail) InkVerdict(
        string line, Reading r, SKBitmap pixels, string core, string which)
    {
        var box = CoreBox(r, core);
        if (box is null) return ("?", "no core box");
        var ink = InkExtent(pixels, box.Value.Bounds);
        if (ink is null) return ("?", "no ink measure");
        var (inkLeft, inkRight) = ink.Value;
        // The line may have come back in several boxes; what covers it is all of them on its band.
        var c = box.Value.Bounds;
        var band = r.Boxes.Select(o => o.Bounds)
            .Where(o => Math.Min(o.Bottom, c.Bottom) - Math.Max(o.Top, c.Top) > Math.Min(o.Height, c.Height) * 0.5 &&
                o.Right > inkLeft && o.Left < inkRight)
            .ToList();
        if (!band.Contains(c)) band.Add(c);
        var unionLeft = band.Min(o => o.Left);
        var unionRight = band.Max(o => o.Right);
        var several = band.Count > 1 ? $" boxes={band.Count}" : "";
        var details = new List<string>();
        var cls = "b";
        var h = c.Height;
        string P(System.Windows.Rect strip)
        {
            var p = r.Probability(strip);
            return p is null ? "-" : p.Value.ToString("0.00", CultureInfo.InvariantCulture);
        }
        if (which is "trail" or "both")
        {
            var past = inkRight - unionRight;
            details.Add($"R:box={unionRight:0} ink={inkRight} past={past:0}px p={P(new System.Windows.Rect(c.Right, c.Top, h * 1.5, c.Height))}{several}");
            cls = past > 2 ? "a" : past > -4 ? "b-tight" : "b-inside";
        }
        if (which is "lead" or "both")
        {
            var past = unionLeft - inkLeft;
            details.Add($"L:box={unionLeft:0} ink={inkLeft} past={past:0}px p={P(new System.Windows.Rect(Math.Max(0, c.Left - h * 1.5), c.Top, Math.Min(c.Left, h * 1.5), c.Height))}{several}");
            var lc = past > 2 ? "a" : past > -4 ? "b-tight" : "b-inside";
            cls = which == "both" && cls != lc ? cls + "/" + lc : lc;
        }
        return (cls, string.Join(" ", details));
    }

    // Whether the final block's line rectangle reaches the ink at both ends of the line.
    private static (bool Covered, string Detail)? BoundsCover(string line, Reading r, SKBitmap pixels, string core)
    {
        var box = CoreBox(r, core);
        if (box is null) return null;
        var ink = InkExtent(pixels, box.Value.Bounds);
        if (ink is null) return null;
        var b = box.Value.Bounds;
        var lines = r.Final.SelectMany(f => f.Lines)
            .Where(l => Math.Min(l.Bottom, b.Bottom) - Math.Max(l.Top, b.Top) > b.Height * 0.5)
            .ToList();
        if (lines.Count == 0) return (false, "no line");
        var left = lines.Min(l => l.Left);
        var right = lines.Max(l => l.Right);
        var covered = left <= ink.Value.Left + 2 && right >= ink.Value.Right - 2;
        return (covered, $"line {left:0}-{right:0} ink {ink.Value.Left}-{ink.Value.Right}");
    }

    private static void Dump(Reading r)
    {
        foreach (var b in r.Boxes)
            Console.WriteLine($"        box  ({b.Bounds.Left:0},{b.Bounds.Top:0})-({b.Bounds.Right:0},{b.Bounds.Bottom:0}) s={b.Score:0.00}");
        foreach (var b in r.Raw)
            Console.WriteLine($"        raw  ({b.Bounds.Left:0},{b.Bounds.Top:0})-({b.Bounds.Right:0},{b.Bounds.Bottom:0}) {b.Score:0.00} \"{b.Text}\"");
        foreach (var b in r.Final)
            Console.WriteLine($"        fin  ({b.Bounds.Left:0},{b.Bounds.Top:0})-({b.Bounds.Right:0},{b.Bounds.Bottom:0}) \"{b.Text.Replace("\n", " / ")}\"");
    }

    // ---------------------------------------------------------------------------------------------
    // Regression: every image of a corpus read the way the app reads it, one canonical line per
    // image and flow, so a run with --no-ends and a run without can be diffed line by line.
    //
    // Config, tab separated: folder <TAB> default language <TAB> flows (sub,panel,shot,vsub,vshot).
    // A file named en-*, ja-*, jp-*, ko-*, zh-* is read in that language whatever the default.
    private static int Regress(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --edge-punct --regress <root> <config.tsv> <out.txt> [--no-ends]");
            return 1;
        }

        var root = args[0];
        var rows = File.ReadAllLines(args[1], Encoding.UTF8)
            .Where(l => l.Trim().Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .ToList();
        using var ocr = new OcrService();
        using var output = new StreamWriter(args[2], false, new UTF8Encoding(false));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;
        foreach (var row in rows)
        {
            var folder = Path.Combine(root, row[0]);
            var flows = row[2].Split(',');
            foreach (var path in Directory.GetFiles(folder)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal))
            {
                Bitmap image;
                try { image = new Bitmap(path); }
                catch { continue; }
                using (image)
                {
                    var name = Path.GetFileName(path);
                    var language = name.Split('-')[0].ToLowerInvariant() switch
                    {
                        "en" => "EN", "ja" or "jp" => "JA", "ko" => "KO", "zh" => "ZH-HANT",
                        _ => row[1],
                    };
                    foreach (var flow in flows)
                    {
                        var blocks = RegressRead(ocr, image, language, flow);
                        output.WriteLine($"{row[0]}/{name}\t{flow}\t" + string.Join(" ; ", blocks
                            .OrderBy(b => b.Bounds.Top).ThenBy(b => b.Bounds.Left)
                            .Select(b => $"{b.Text.Replace("\n", " / ")} [{b.Bounds.X:0},{b.Bounds.Y:0},{b.Bounds.Width:0},{b.Bounds.Height:0}]")));
                    }
                    count++;
                }
            }
        }
        Console.WriteLine($"{count} images in {timer.Elapsed.TotalSeconds:0}s");
        return 0;
    }

    private static List<OcrTextBlock> RegressRead(OcrService ocr, Bitmap image, string language, string flow)
    {
        switch (flow)
        {
            case "shot":
                return ocr.RecognizeAsync(image, language).GetAwaiter().GetResult();
            case "vshot":
                return ocr.RecognizeAsync(image, language, verticalText: true).GetAwaiter().GetResult();
        }

        var vertical = flow == "vsub";
        var mode = flow == "panel" ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
        var orientation = vertical ? RealtimeTextOrientation.Vertical : RealtimeTextOrientation.Horizontal;
        var (primary, fallbacks) = RealtimeDetectorSize.For(image.Width, image.Height, mode, orientation);
        // As RealtimeTranslationSession: the primary size, then each fallback while nothing is read.
        foreach (var size in new[] { primary }.Concat(fallbacks))
        {
            List<OcrTextBlock>? read = null;
            for (var attempt = 0; attempt < 20 && read is null; attempt++)
                read = ocr.TryRecognizeAsync(image, language, size, default, mode, orientation).GetAwaiter().GetResult();
            read ??= [];
            if (!vertical) read = RealtimeTranslationSession.RejectCollapsedBlocks(read, image.Height, 0);
            read = RealtimeTranslationSession.RejectShortReadings(read, 0);
            if (read.Count > 0) return read;
        }
        return [];
    }

    // ---------------------------------------------------------------------------------------------
    // Cost: what the walk adds to one read, fastest of twenty. Two numbers per image: the walk on
    // its own over the detector's boxes (the honest figure, since a whole read jitters by more than
    // the walk costs), and the whole realtime read with and without it, for scale.
    private static int Cost(string[] args)
    {
        var panel = args.Contains("--panel");
        args = [.. args.Where(a => a != "--panel")];
        var language = args[0];
        var mode = panel ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
        using var engine = new OnnxOcrEngine();
        using var ocr = new OcrService();
        foreach (var path in args.Skip(1))
        {
            using var image = new Bitmap(path);
            using var pixels = OnnxOcrEngine.ConvertToSkBitmap(image);
            var (primary, _) = RealtimeDetectorSize.For(image.Width, image.Height, mode);

            HorizontalLineEnds.Enabled = false;
            IReadOnlyList<(System.Windows.Rect Bounds, float Score)> boxes;
            using (var session = engine.BeginDetection(image, language, primary)) boxes = session.Boxes;
            HorizontalLineEnds.Enabled = true;
            var detector = boxes.Select(b => new RapidOcrNet.TextBox
            {
                Score = b.Score,
                BoxPoints =
                [
                    new((int)b.Bounds.Left, (int)b.Bounds.Top), new((int)b.Bounds.Right, (int)b.Bounds.Top),
                    new((int)b.Bounds.Right, (int)b.Bounds.Bottom), new((int)b.Bounds.Left, (int)b.Bounds.Bottom),
                ],
            }).ToList();
            var bounds = boxes.Select(b => b.Bounds).ToList();
            var walk = Fastest(() => HorizontalLineEnds.Apply(pixels, bounds, detector, 1.0, 1.0, 0, out _));

            double Read(bool on)
            {
                HorizontalLineEnds.Enabled = on;
                return Fastest(() => ocr.TryRecognizeAsync(image, language, primary, default, mode).GetAwaiter().GetResult());
            }
            var off = Read(false);
            var with = Read(true);
            Console.WriteLine($"{Path.GetFileName(path)} {image.Width}x{image.Height} detect={primary} boxes={boxes.Count}  " +
                $"walk={walk:0.00}ms  read off={off:0.0}ms on={with:0.0}ms");
        }
        return 0;

        static double Fastest(Action action)
        {
            action();
            var best = double.MaxValue;
            for (var i = 0; i < 20; i++)
            {
                var t = System.Diagnostics.Stopwatch.StartNew();
                action();
                best = Math.Min(best, t.Elapsed.TotalMilliseconds);
            }
            return best;
        }
    }
}
