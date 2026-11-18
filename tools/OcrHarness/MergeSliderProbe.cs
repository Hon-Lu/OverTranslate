using System.IO;
using System.Text;
using System.Windows;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

/// <summary>
/// The nine steps of a single "how much to join" slider — 不合併, 逐項, 自動 1–5, 整段, 合併 —
/// replayed over the cache, to see whether each step joins everything the one before it did.
/// </summary>
/// <remarks>
/// <para><c>--auto-probe slider cache.json out-dir</c>. Each step is the app's own grouping at that
/// 合併程度 (<see cref="RealtimeMergeLevels"/>). The two ends were first simulated here before the
/// app had them: 不合併 as the panel grouper's lines, 整個區塊 as every line of the frame in one
/// group.</para>
///
/// <para>Steps are compared as join relations: for each frame, the set of detected-block pairs a
/// step put in one group. Step i is monotone over step i−1 when its set holds the other's. Blocks
/// are matched to a step's groups by geometry (the group whose box covers at least half the block,
/// preferring one whose text holds the block's), so every step is judged on the same blocks
/// whatever its own filters did; a block a step dropped takes no part in that comparison.</para>
///
/// <para>Three candidate fixes for 整段 are measured beside the plain steps: 整段 ∪ 自動 5 and
/// 整段 ∪ 逐項 (the join relations united, closed transitively), and 自動 with every passage limit
/// let go. Each is compared with its neighbours and with plain 整段 both ways. Writes summary.txt,
/// instances.tsv (every group a step parted that the step before joined), joins.tsv and
/// splits.tsv (the same-row decisions every step shares). SLIDER_EXPLAIN=name prints the dialogue
/// grouper's verdicts on the frames whose file name holds it.</para>
/// </remarks>
internal static class MergeSliderProbe
{
    private static readonly string[] Names =
    [
        "none", "items", "auto1", "auto2", "auto3", "auto4", "auto5", "passage", "merged",
        // Candidate fixes for 整段, measured beside it.
        "passage+auto5", "passage+items", "auto-max",
    ];

    // Each step against the next, then each fix against its neighbours and against plain 整段.
    private static readonly (int Left, int Right)[] Comparisons =
    [
        (0, 1), (1, 2), (2, 3), (3, 4), (4, 5), (5, 6), (6, 7), (7, 8),
        (6, 9), (9, 8), (7, 9), (9, 7),
        (6, 10), (10, 8), (7, 10), (10, 7),
        (6, 11), (11, 8), (7, 11), (11, 7),
    ];

    internal static int Run(AutoModeProbe.Capture[] cache, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var profiles = new ResolvedProfile?[Names.Length];
        for (int level = RealtimeMergeLevels.Min; level <= RealtimeMergeLevels.Max; level++)
            profiles[level] = Profile(level);
        // 自動 with every limit on a passage let go: as near as its rules come to taking every group
        // the dialogue grouper joined for a passage, while keeping the panel grouper's own joins.
        var loosest = Profile(RealtimeMergeLevels.AutomaticHighest);
        profiles[11] = loosest with
        {
            Auto = loosest.Auto with
            {
                SparseZones = 1_000_000, StandsOut = 0, LetteringZones = 1_000_000,
                LetteringAspect = 1e9, PassageLeading = 1e9,
            },
        };

        var images = Comparisons.Select(_ => new Dictionary<string, int>()).ToArray();
        var pairs = Comparisons.Select(_ => new Dictionary<string, int>()).ToArray();
        var totalPairs = new Dictionary<string, long[]>();
        var groupCounts = new Dictionary<string, long[]>();
        var unmatched = new Dictionary<string, long[]>();
        var frames = new Dictionary<string, int>();
        var instances = new StringBuilder("step\tpairs\tlabel\tset\timage\tsize\tleft\tright\n");
        var joins = new StringBuilder("label\tset\timage\tgap\theightRatio\tleft\tright\tline\n");
        var splits = new StringBuilder("label\tset\timage\tgap\theightRatio\tleft\tright\n");

        foreach (var capture in cache)
        {
            var label = capture.Label;
            var set = Path.GetFileName(Path.GetDirectoryName(capture.Image))!;
            var file = Path.GetFileName(capture.Image);
            frames[label] = frames.GetValueOrDefault(label) + 1;
            List<OcrTextBlock> Raw() => capture.Blocks.Select(b => b.Restore()).ToList();

            var raw = Raw();
            var universe = new HashSet<OcrTextBlock>(
                OcrService.FilterForRealtime(raw, capture.Height, ResolvedProfile.Standard), ReferenceEqualityComparer.Instance);
            var inUniverse = raw.Select(universe.Contains).ToArray();

            var assigned = new int[Names.Length][];
            var groupTexts = new List<string>[Names.Length];
            for (int s = 0; s < Names.Length; s++)
            {
                if (s is 9 or 10)
                {
                    assigned[s] = Union(assigned[s == 9 ? 6 : 1], assigned[7]);
                    groupTexts[s] = Enumerable.Range(0, raw.Count)
                        .Select(root => Flat(string.Join(" ", raw.Where((_, i) => assigned[s][i] == root).Select(b => b.Text))))
                        .ToList();
                }
                else
                {
                    var groups = OcrService.GroupRealtime(Raw(), capture.Height, profiles[s]!);
                    assigned[s] = Assign(raw, inUniverse, groups);
                    groupTexts[s] = groups.Select(g => Flat(g.Text)).ToList();
                }
                if (!totalPairs.TryGetValue(label, out var tp)) totalPairs[label] = tp = new long[Names.Length];
                tp[s] += CountPairs(assigned[s]);
                if (!groupCounts.TryGetValue(label, out var gc)) groupCounts[label] = gc = new long[Names.Length];
                gc[s] += assigned[s].Where(a => a >= 0).Distinct().Count();
                // Blocks this step's groups could not be matched to: the comparison leaves them out.
                if (!unmatched.TryGetValue(label, out var um)) unmatched[label] = um = new long[Names.Length];
                um[s] += Enumerable.Range(0, raw.Count).Count(i => inUniverse[i] && assigned[s][i] < 0);
            }

            for (int c = 0; c < Comparisons.Length; c++)
            {
                var (l, r) = Comparisons[c];
                var broken = Broken(assigned[l], assigned[r]);
                if (broken.Count == 0) continue;
                images[c][label] = images[c].GetValueOrDefault(label) + 1;
                pairs[c][label] = pairs[c].GetValueOrDefault(label) + broken.Sum(b => b.Pairs);
                foreach (var (leftGroup, rightGroups, count) in broken)
                    instances.Append(string.Join('\t', $"{Names[l]}>{Names[r]}", count, label, set, file, capture.Size,
                        groupTexts[l][leftGroup],
                        string.Join(" ‖ ", rightGroups.Select(g => g < 0 ? "(dropped)" : groupTexts[r][g])))).Append('\n');
            }

            SameRow(label, set, file, raw, inUniverse, assigned[0], joins, splits);

            // Why 整段 parted what 逐項 joined: the dialogue grouper's own verdicts on a named frame.
            if (Environment.GetEnvironmentVariable("SLIDER_EXPLAIN") is { Length: > 0 } explain && file.Contains(explain))
            {
                var decisions = new List<OcrTextBlockGrouper.NextLineDecision>();
                OcrService.GroupRealtime(Raw(), capture.Height, profiles[7]!, null, decisions);
                Console.WriteLine($"--- {label} {set}/{file} size={capture.Size}");
                foreach (var d in decisions.Where(d => d.Kind != "row"))
                    Console.WriteLine($"  {(d.Joined ? "JOIN" : "part")} {d.Rule,-24} gap={d.VerticalGap:0.00} size={d.TextSizeRatio:0.00} " +
                        $"| {Flat(d.Previous)} || {Flat(d.Current)}");
            }
        }

        var summary = new StringBuilder();
        summary.AppendLine("frames: " + string.Join("  ", frames.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}")));
        summary.AppendLine();
        summary.AppendLine("step                     groups D/P/M              joined pairs D/P/M");
        for (int s = 0; s < Names.Length; s++)
            summary.AppendLine($"{Names[s],-14} {Row(groupCounts, s),28} {Row(totalPairs, s),36}   unmatched {Row(unmatched, s)}");
        summary.AppendLine();
        summary.AppendLine("left>right (pairs left joined, right parted)  frames D/P/M        pairs D/P/M");
        for (int c = 0; c < Comparisons.Length; c++)
        {
            var (l, r) = Comparisons[c];
            summary.AppendLine($"{Names[l] + ">" + Names[r],-28} {Cell(images[c]),24} {Cell(pairs[c]),24}");
        }
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());
        File.WriteAllText(Path.Combine(outDir, "instances.tsv"), instances.ToString());
        File.WriteAllText(Path.Combine(outDir, "joins.tsv"), joins.ToString());
        File.WriteAllText(Path.Combine(outDir, "splits.tsv"), splits.ToString());
        Console.Write(summary);
        return 0;
    }

    private static ResolvedProfile Profile(int level) =>
        ResolvedProfile.Resolve(new RealtimeProfile { Id = "slider", MergeLevel = level });

    internal static int[] Assign(List<OcrTextBlock> raw, bool[] inUniverse, List<OcrTextBlock> groups)
    {
        var result = Enumerable.Repeat(-1, raw.Count).ToArray();
        var boxes = groups.Select(Box).ToList();
        var texts = groups.Select(g => Norm(g.Text)).ToList();
        for (int i = 0; i < raw.Count; i++)
        {
            if (!inUniverse[i]) continue;
            var b = Box(raw[i]);
            double area = Math.Max(1e-6, b.Width * b.Height);
            var t = Norm(raw[i].Text);
            int best = -1;
            (int Text, double Frac, double Area) bestKey = default;
            for (int g = 0; g < groups.Count; g++)
            {
                var inter = Rect.Intersect(b, boxes[g]);
                if (inter.IsEmpty) continue;
                double frac = inter.Width * inter.Height / area;
                if (frac < 0.5) continue;
                var key = (t.Length > 0 && texts[g].Contains(t, StringComparison.Ordinal) ? 1 : 0,
                    Math.Round(frac * 20) / 20, boxes[g].Width * boxes[g].Height);
                if (best < 0 || key.Item1 > bestKey.Text ||
                    key.Item1 == bestKey.Text && (key.Item2 > bestKey.Frac ||
                        key.Item2 == bestKey.Frac && key.Item3 < bestKey.Area))
                {
                    best = g;
                    bestKey = key;
                }
            }
            result[i] = best;
        }
        return result;
    }

    // Groups of the left step whose blocks the right step parted, with the right step's groups they
    // went to and how many pairs that broke. Blocks either step left out are not counted.
    internal static List<(int Left, List<int> Right, int Pairs)> Broken(int[] left, int[] right)
    {
        var result = new List<(int, List<int>, int)>();
        foreach (var group in Enumerable.Range(0, left.Length).Where(i => left[i] >= 0 && right[i] >= 0).GroupBy(i => left[i]))
        {
            var members = group.ToList();
            int broken = 0;
            for (int a = 0; a < members.Count; a++)
                for (int b = a + 1; b < members.Count; b++)
                    if (right[members[a]] != right[members[b]]) broken++;
            if (broken > 0) result.Add((group.Key, members.Select(m => right[m]).Distinct().ToList(), broken));
        }
        return result;
    }

    private static int[] Union(int[] a, int[] b)
    {
        int n = a.Length;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        foreach (var source in new[] { a, b })
            foreach (var group in Enumerable.Range(0, n).Where(i => a[i] >= 0 && b[i] >= 0).GroupBy(i => source[i]))
            {
                int first = group.First();
                foreach (var i in group) parent[Find(i)] = Find(first);
            }
        return Enumerable.Range(0, n).Select(i => a[i] >= 0 && b[i] >= 0 ? Find(i) : -1).ToArray();
    }

    internal static long CountPairs(int[] assigned) =>
        assigned.Where(a => a >= 0).GroupBy(a => a).Sum(g => (long)g.Count() * (g.Count() - 1) / 2);

    // Left–right decisions of 不合併 (which every step shares): pieces joined into one line, and
    // neighbours on one row left apart.
    private static void SameRow(string label, string set, string file, List<OcrTextBlock> raw, bool[] inUniverse,
        int[] line, StringBuilder joins, StringBuilder splits)
    {
        var kept = Enumerable.Range(0, raw.Count).Where(i => inUniverse[i] && line[i] >= 0).ToList();
        foreach (var i in kept)
        {
            var a = Box(raw[i]);
            // The nearest block to the right on the same row.
            int next = -1;
            double nearest = double.MaxValue;
            foreach (var j in kept)
            {
                if (j == i) continue;
                var b = Box(raw[j]);
                if (b.Left < a.Left + a.Width / 2) continue;
                if (Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) < Math.Min(a.Height, b.Height) * 0.6) continue;
                double gap = b.Left - a.Right;
                if (gap < nearest) { nearest = gap; next = j; }
            }
            if (next < 0) continue;
            var c = Box(raw[next]);
            double h = Math.Min(a.Height, c.Height);
            double ratio = Math.Min(a.Height, c.Height) / Math.Max(a.Height, c.Height);
            var row = string.Join('\t', label, set, file, (nearest / Math.Max(1, h)).ToString("0.00"), ratio.ToString("0.00"),
                Flat(raw[i].Text), Flat(raw[next].Text));
            if (line[i] == line[next]) joins.Append(row).Append('\t').Append(line[i]).Append('\n');
            else splits.Append(row).Append('\n');
        }
    }

    internal static Rect Box(OcrTextBlock block) =>
        block.LayoutBounds.Width > 0 && block.LayoutBounds.Height > 0 ? block.LayoutBounds : block.Bounds;

    private static string Norm(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    internal static string Flat(string text) => text.Replace("\r", "").Replace("\n", " ⏎ ").Replace("\t", " ");

    private static string Cell(Dictionary<string, int> byLabel) =>
        $"{byLabel.GetValueOrDefault("D"),5} {byLabel.GetValueOrDefault("P"),5} {byLabel.GetValueOrDefault("M"),5}";

    private static string Row(Dictionary<string, long[]> byLabel, int s) =>
        string.Join(" ", new[] { "D", "P", "M" }.Select(l => (byLabel.TryGetValue(l, out var v) ? v[s] : 0).ToString().PadLeft(8)));
}
