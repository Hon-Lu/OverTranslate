using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

/// <summary>
/// 自動 against the two modes it chooses between: reads each image once, groups it all three ways,
/// and reports where 自動 parts from each mode and which of the groups in question it took.
/// </summary>
/// <remarks>
/// <c>--auto-probe capture cache.json @list.txt</c> — each line of the list is
/// <c>path TAB language TAB label</c>, the label D (a passage: subtitles, dialogue, comics),
/// P (separate items: a panel, a menu, a chat) or M (both on one screen). OCR runs once, at the
/// size the labelled mode would have used; an M image is read at both.
/// <c>--auto-probe replay cache.json report.txt</c> — no OCR, so a rule can be changed and judged
/// in seconds. Beside the report it writes <c>report.extras.tsv</c>, one line for every group the
/// two groupers disagreed over, <c>report.groups.txt</c>, 自動's own answer per image — the
/// file to diff between two builds — <c>report.modes.txt</c>, the 字幕 and 面板 groupers'
/// answers with their boxes, and <c>report.own.txt</c>, the frame grouped as a session on the mode
/// would group it.
/// <c>--auto-probe replay cache.json report.txt --profile mode.json</c> — the same, grouped with a
/// mode's values instead of 標準's. The file is one mode as the settings file keeps a custom one
/// (<see cref="RealtimeProfile"/>): only the values it sets differ from its BasedOn preset. 自動 reads
/// with the mode's own values; the 字幕 and 面板 columns are the same values with 合併程度 set to
/// 整段 and 逐項, whatever the mode's own is — report.own.txt is that one. Only the grouping values
/// can be replayed: timing, 譯文保留 and 辨識解析度 act between frames or before recognition, which
/// the cache does not hold.
/// <c>--auto-probe drops cache.json lines.tsv [--profile mode.json]</c> — every detected line and
/// the filter that took it out (confidence floor, box across the block, 忽略小字), beside the
/// frame's main text height: what each step of those two values costs.
/// <c>--auto-probe extremes cache.json out-dir</c> — every value at both ends of its range, one
/// replay each, into out-dir; fails if any of them throws.
/// <c>--auto-probe confidence cache.json out-dir</c> — 短句辨識門檻 at every percent it offers; see
/// <see cref="ConfidenceSweepProbe"/>.
/// </remarks>
internal static class AutoModeProbe
{
    internal sealed record Capture(
        string Image, string Language, string Label, int Width, int Height, int Size,
        GroupingPrototype.Block[] Blocks);

    internal static async Task<int> Run(string[] args)
    {
        if (args.Length < 3)
            throw new ArgumentException(
                "--auto-probe capture cache.json @list | replay cache.json report.txt [--profile mode.json] | drops cache.json lines.tsv [--profile mode.json] | extremes cache.json out-dir | slider cache.json out-dir | confidence cache.json out-dir");
        if (args[0] == "detector") return await DetectorFigureProbe.Run(args[1].TrimStart('@'), args[2]);
        if (args[0] == "capture")
        {
            using var engine = new OnnxOcrEngine();
            var captures = new List<Capture>();
            foreach (var entry in File.ReadAllLines(args[2].TrimStart('@')).Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var fields = entry.Split('\t');
                using var bitmap = new Bitmap(fields[0]);
                var modes = fields[2] switch
                {
                    "D" => new[] { RealtimeBlockMode.Subtitle },
                    "P" => [RealtimeBlockMode.Panel],
                    _ => [RealtimeBlockMode.Subtitle, RealtimeBlockMode.Panel],
                };
                foreach (var mode in modes)
                {
                    var size = RealtimeDetectorSize.For(bitmap.Width, bitmap.Height, mode).Primary;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    var raw = await engine.TryRecognizeAsync(bitmap, fields[1], size)
                        ?? throw new InvalidOperationException("No OCR slot");
                    captures.Add(new(fields[0], fields[1], fields[2], bitmap.Width, bitmap.Height, size,
                        raw.Select(GroupingPrototype.Block.From).ToArray()));
                    Console.WriteLine($"OCR {fields[2]} {size} {raw.Count,3} {timer.ElapsedMilliseconds,5}ms {fields[0]}");
                }
            }
            File.WriteAllText(args[1], JsonSerializer.Serialize(captures));
            return 0;
        }

        var cache = JsonSerializer.Deserialize<Capture[]>(File.ReadAllText(args[1]))!;
        if (args[0] == "extremes") return Extremes(cache, args[2]);
        if (args[0] == "slider") return MergeSliderProbe.Run(cache, args[2]);
        if (args[0] == "mergemax") return MergeMaxProbe.Run(cache, args[2]);
        if (args[0] == "confidence") return ConfidenceSweepProbe.Run(cache, args[2]);

        var profile = ResolvedProfile.Standard;
        int at = Array.IndexOf(args, "--profile");
        if (at > 0)
        {
            if (at + 1 >= args.Length) throw new ArgumentException("--profile needs a file");
            profile = Load(args[at + 1]);
        }
        Console.WriteLine(profile.Describe());
        if (args[0] == "drops")
        {
            Console.Write(Drops(cache, profile, args[2]));
            return 0;
        }
        Console.Write(Replay(cache, profile, args[2]));
        return 0;
    }

    /// <summary>
    /// Every detected line of every cached frame and the filter that took it out, if one did: the
    /// mode's confidence floor (短句辨識門檻), the box across the block, or 忽略小字 — with the frame's
    /// main text height beside it, so each step of a filter can be judged by what it drops.
    /// </summary>
    /// <returns>Lines dropped by each filter, per label.</returns>
    private static string Drops(Capture[] cache, ResolvedProfile profile, string tsvPath)
    {
        var lines = new StringBuilder("label\tset\timage\tsize\tfate\tconfidence\theight\tmain\ttext\n");
        var totals = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var capture in cache)
        {
            var raw = capture.Blocks.Select(b => b.Restore()).ToList();
            var confident = OcrService.RejectUnconvincingBlocks(raw, profile.ConfidenceFloor);
            var whole = confident
                .Where(b => !CollapsedDetection.IsCollapsed(b.Bounds.Height, capture.Height, b.Text)).ToList();
            var kept = OcrService.IgnoreSmallText(whole, profile.IgnoreSmallFraction);
            double main = whole.Count > 0 ? OcrService.MainTextHeight(whole) : 0;
            var afterConfidence = new HashSet<OcrTextBlock>(confident, ReferenceEqualityComparer.Instance);
            var afterCollapse = new HashSet<OcrTextBlock>(whole, ReferenceEqualityComparer.Instance);
            var afterSmall = new HashSet<OcrTextBlock>(kept, ReferenceEqualityComparer.Instance);

            var set = Path.GetFileName(Path.GetDirectoryName(capture.Image))!;
            if (!totals.TryGetValue(capture.Label, out var total)) totals[capture.Label] = total = new int[5];
            total[0] += raw.Count;
            foreach (var block in raw)
            {
                string fate =
                    !afterConfidence.Contains(block) ? "conf" :
                    !afterCollapse.Contains(block) ? "collapsed" :
                    !afterSmall.Contains(block) ? "small" : "kept";
                total[fate switch { "conf" => 1, "collapsed" => 2, "small" => 3, _ => 4 }]++;
                lines.Append(string.Join('\t', capture.Label, set, Path.GetFileName(capture.Image), capture.Size, fate,
                    block.Confidence?.ToString("0.000") ?? "", block.LayoutBounds.Height.ToString("0"),
                    main.ToString("0"), Flat(block.Text))).Append('\n');
            }
        }
        File.WriteAllText(tsvPath, lines.ToString());
        var summary = new StringBuilder("label  lines   conf collapsed  small   kept\n");
        foreach (var (label, t) in totals)
            summary.AppendLine($"{label,-5} {t[0],6} {t[1],6} {t[2],9} {t[3],6} {t[4],6}");
        return summary.ToString();
    }

    /// <summary>A mode as the settings file keeps a custom one, resolved the way a session would.</summary>
    internal static ResolvedProfile Load(string path)
    {
        var stored = JsonSerializer.Deserialize<RealtimeProfile>(File.ReadAllText(path))
            ?? throw new ArgumentException($"{path} holds no mode");
        if (string.IsNullOrWhiteSpace(stored.Id)) stored.Id = Path.GetFileNameWithoutExtension(path);
        return ResolvedProfile.Resolve(stored);
    }

    // Each value at both ends of its range, alone, then the combinations nobody has run: every
    // grouping value at its loosest, and at its tightest. A throw anywhere fails the run.
    private static int Extremes(Capture[] cache, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var cases = new List<(string Name, RealtimeProfile Stored)>();
        void Add(string name, Action<RealtimeProfile> set)
        {
            var stored = new RealtimeProfile { Id = name };
            set(stored);
            cases.Add((name, stored));
        }

        for (int level = RealtimeMergeLevels.Min; level <= RealtimeMergeLevels.Max; level++)
        {
            int merge = level;
            Add($"merge-{level}", p => p.MergeLevel = merge);
        }
        Add("unpunctuated-off", p => p.MergeUnpunctuated = false);
        foreach (var percent in new[] { RealtimeProfile.MinConfidencePercent, RealtimeProfile.MaxConfidencePercent })
            Add($"confidence-{percent}", p => p.ConfidencePercent = percent);
        Add("settle-auto", p => p.SettleSeconds = RealtimePresets.Automatic);
        Add("settle-0", p => p.SettleSeconds = 0);
        Add($"settle-max", p => p.SettleSeconds = RealtimeProfile.MaxSettleSeconds);
        Add("clear-min", p => p.ClearAfterEmptyReads = RealtimeProfile.MinClearAfterEmptyReads);
        Add("clear-max", p => p.ClearAfterEmptyReads = RealtimeProfile.MaxClearAfterEmptyReads);
        Add("detector-min", p => p.DetectorPercent = RealtimeProfile.MinDetectorPercent);
        Add("detector-max", p => p.DetectorPercent = RealtimeProfile.MaxDetectorPercent);
        Add("small-max", p => p.IgnoreSmallPercent = RealtimeProfile.MaxIgnoreSmallPercent);
        // Each grouping at the loosest and the tightest of everything else; 自動判斷 at the end of its
        // own steps that goes the same way.
        foreach (var (name, loosest, tightest) in new[]
        {
            ("lines", RealtimeMergeLevels.Min, RealtimeMergeLevels.Min),
            ("items", RealtimeMergeLevels.Items, RealtimeMergeLevels.Items),
            ("auto", RealtimeMergeLevels.AutomaticHighest, RealtimeMergeLevels.AutomaticLowest),
            ("passage", RealtimeMergeLevels.Passage, RealtimeMergeLevels.Passage),
            ("merged", RealtimeMergeLevels.Max, RealtimeMergeLevels.Max),
        })
        {
            Add($"loosest-{name}", p =>
            {
                p.MergeLevel = loosest;
                p.ConfidencePercent = RealtimeProfile.MinConfidencePercent;
            });
            Add($"tightest-{name}", p =>
            {
                p.MergeLevel = tightest;
                p.ConfidencePercent = RealtimeProfile.MaxConfidencePercent;
                p.MergeUnpunctuated = false;
                p.IgnoreSmallPercent = RealtimeProfile.MaxIgnoreSmallPercent;
            });
        }

        int failed = 0;
        var summary = new StringBuilder();
        foreach (var (name, stored) in cases)
        {
            File.WriteAllText(Path.Combine(outDir, name + ".json"), JsonSerializer.Serialize(stored));
            try
            {
                var profile = ResolvedProfile.Resolve(stored);
                var totals = Replay(cache, profile, Path.Combine(outDir, name + ".txt"));
                summary.AppendLine($"=== {name}: {profile.Describe()}").Append(totals);
                Console.WriteLine($"ok   {name}");
            }
            catch (Exception ex)
            {
                failed++;
                summary.AppendLine($"=== {name}: THREW {ex}");
                Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        File.WriteAllText(Path.Combine(outDir, "extremes.txt"), summary.ToString());
        Console.WriteLine($"{cases.Count - failed}/{cases.Count} replayed the whole cache without throwing");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Groups every cached frame with <paramref name="profile"/> and writes the reports.</summary>
    /// <returns>The summary table.</returns>
    private static string Replay(Capture[] cache, ResolvedProfile profile, string reportPath)
    {
        var report = new StringBuilder();
        var extras = new StringBuilder();
        var answers = new StringBuilder();
        var byMode = new StringBuilder();
        var own = new StringBuilder();
        var totals = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        var passage = profile with { Merge = RealtimeMerge.Passage };
        var items = profile with { Merge = RealtimeMerge.Items };
        foreach (var capture in cache)
        {
            List<OcrTextBlock> Raw() => capture.Blocks.Select(b => b.Restore()).ToList();
            var reading = RealtimeAutoLayout.Read(OcrService.FilterForRealtime(Raw(), capture.Height, profile), profile.Auto);
            var asDialogue = OcrService.GroupRealtime(Raw(), capture.Height, passage);
            var asPanel = OcrService.GroupRealtime(Raw(), capture.Height, items);
            // The mode's own grouping, as a session on it would group the frame: on 合併程度's two
            // ends the only one of these that runs them.
            var asProfile = OcrService.GroupRealtime(Raw(), capture.Height, profile);
            bool sameAsDialogue = Same(reading.Groups, asDialogue);
            bool sameAsPanel = Same(reading.Groups, asPanel);

            var set = Path.GetFileName(Path.GetDirectoryName(capture.Image))!;
            var key = $"{capture.Label} {set}";
            if (!totals.TryGetValue(key, out var total)) totals[key] = total = new int[8];
            total[0]++;
            total[1] += reading.Sparse ? 1 : 0;
            total[2] += reading.Candidates.Count;
            total[3] += reading.Candidates.Count(c => c.Passage);
            total[4] += reading.Groups.Count;
            total[5] += reading.Groups.Count(g => g.ReadAs == RealtimeBlockMode.Subtitle);
            if (sameAsDialogue) total[6]++;
            if (sameAsPanel) total[7]++;

            var name = $"{capture.Label} {set}/{Path.GetFileName(capture.Image)} size={capture.Size}";
            var verdict = sameAsDialogue && sameAsPanel ? "=both" : sameAsDialogue ? "=D" : sameAsPanel ? "=P" : "MIXED";
            report.AppendLine($"=== {name} {capture.Width}x{capture.Height} blocks={capture.Blocks.Length} zones={reading.Zones} {verdict}");
            answers.AppendLine($"=== {name}");
            foreach (var group in reading.Groups)
            {
                report.AppendLine($"  {Letter(group.ReadAs)} {Flat(group.Text)}");
                answers.AppendLine($"  {Letter(group.ReadAs)} {Flat(group.Text)}");
            }
            byMode.AppendLine($"=== {name}");
            foreach (var group in asDialogue) byMode.AppendLine($"  字幕 {group.Bounds} {Flat(group.Text)}");
            foreach (var group in asPanel) byMode.AppendLine($"  面板 {group.Bounds} {Flat(group.Text)}");
            own.AppendLine($"=== {name}");
            foreach (var group in asProfile) own.AppendLine($"  {Letter(group.ReadAs ?? profile.Layout)} {group.Bounds} {Flat(group.Text)}");
            if (!sameAsDialogue)
                foreach (var text in asDialogue.Select(g => g.Text).Except(reading.Groups.Select(g => g.Text)))
                    report.AppendLine($"      only 字幕: {Flat(text)}");
            if (!sameAsPanel)
                foreach (var text in asPanel.Select(g => g.Text).Except(reading.Groups.Select(g => g.Text)))
                    report.AppendLine($"      only 面板: {Flat(text)}");

            foreach (var candidate in reading.Candidates)
            {
                var s = candidate.Shape;
                extras.AppendLine(string.Join('\t', capture.Label, set, candidate.Passage ? "D" : "P", reading.Zones,
                    s.Rows, s.MaxPerRow, s.MaxAdvance.ToString("0.00"), s.MinAdvance.ToString("0.00"),
                    s.Height.ToString("0"), s.CharactersPerRow.ToString("0.0"), s.LongestRow, s.Marks,
                    s.Wordy ? 1 : 0, s.WidestAspect.ToString("0.0"),
                    Path.GetFileName(capture.Image), Flat(candidate.Group.Output.Text)));
            }
        }

        var summary = new StringBuilder();
        summary.AppendLine("label set                              images sparse cand  ->D groups  asD  =字幕  =面板");
        foreach (var (key, t) in totals)
            summary.AppendLine($"{key,-40} {t[0],5} {t[1],5} {t[2],5} {t[3],4} {t[4],6} {t[5],4} {t[6],5} {t[7],5}");
        // The mode's line only when it is not 標準, so a replay of 標準 writes the very bytes it did
        // before modes existed and two builds can still be compared file for file.
        var header = profile == ResolvedProfile.Standard ? "" : profile.Describe() + Environment.NewLine;
        File.WriteAllText(reportPath, header + summary + Environment.NewLine + report);
        File.WriteAllText(Path.ChangeExtension(reportPath, ".groups.txt"), answers.ToString());
        File.WriteAllText(Path.ChangeExtension(reportPath, ".modes.txt"), byMode.ToString());
        File.WriteAllText(Path.ChangeExtension(reportPath, ".own.txt"), own.ToString());
        File.WriteAllText(Path.ChangeExtension(reportPath, ".extras.tsv"),
            "label\tset\tverdict\tzones\trows\tperRow\tmaxAdv\tminAdv\th\tcpr\tlongest\tmarks\twordy\taspect\timage\ttext\n" + extras);
        return summary.ToString();
    }

    private static bool Same(List<OcrTextBlock> a, List<OcrTextBlock> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First.Text == pair.Second.Text && pair.First.Bounds == pair.Second.Bounds);

    private static string Letter(RealtimeBlockMode? mode) => mode switch
    {
        RealtimeBlockMode.Subtitle => "D",
        RealtimeBlockMode.Panel => "P",
        _ => "-",
    };

    private static string Flat(string text) => text.Replace("\r", "").Replace("\n", " ⏎ ");
}
