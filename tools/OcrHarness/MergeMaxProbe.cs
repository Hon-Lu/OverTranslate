using System.IO;
using System.Text;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;

/// <summary>
/// 合併程度's last step, 合併, against 整段 and against the 整個區塊 it replaced: whether it joins
/// everything 整段 does, what it joins beyond that, and on which frames.
/// </summary>
/// <remarks>
/// <c>--auto-probe mergemax cache.json out-dir</c>. Writes summary.txt and extra.tsv (every group
/// 合併 made out of several of 整段's, with how many pairs that joined); prints the named frames'
/// groups at both steps. 整個區塊 is every line of the frame in one group, as the app had it.
/// </remarks>
internal static class MergeMaxProbe
{
    // The frames 整段 parts across a button or an arrow beside the dialogue (monotonic.md).
    private static readonly string[] Watched =
    [
        "ja-aa-nameplate-region.png", "ja-aa-nameplate-b-region.png", "ja-aa-nameplate-c-region.png",
        "en-uma-narration-region.png", "ja-va11-2line-list-region.png",
    ];

    internal static int Run(AutoModeProbe.Capture[] cache, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var passage = ResolvedProfile.Resolve(new RealtimeProfile { Id = "p", MergeLevel = RealtimeMergeLevels.Passage });
        var merged = ResolvedProfile.Resolve(new RealtimeProfile { Id = "m", MergeLevel = RealtimeMergeLevels.Max });

        var frames = new Dictionary<string, int>();
        var pairs7 = new Dictionary<string, long>();
        var pairs8 = new Dictionary<string, long>();
        var pairsWhole = new Dictionary<string, long>();
        var groups7 = new Dictionary<string, long>();
        var groups8 = new Dictionary<string, long>();
        var broken = new Dictionary<string, (int Frames, long Pairs)>();
        var extra = new Dictionary<string, (int Frames, long Pairs)>();
        var extraWhole = new Dictionary<string, (int Frames, long Pairs)>();
        var instances = new StringBuilder("pairs\tlabel\tset\timage\tsize\tmerged\tpassage parts\n");
        var watched = new StringBuilder();

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

            var g7 = OcrService.GroupRealtime(Raw(), capture.Height, passage);
            var g8 = OcrService.GroupRealtime(Raw(), capture.Height, merged);
            var a7 = MergeSliderProbe.Assign(raw, inUniverse, g7);
            var a8 = MergeSliderProbe.Assign(raw, inUniverse, g8);
            var whole = inUniverse.Select(u => u ? 0 : -1).ToArray();

            long p7 = MergeSliderProbe.CountPairs(a7), p8 = MergeSliderProbe.CountPairs(a8), pw = MergeSliderProbe.CountPairs(whole);
            pairs7[label] = pairs7.GetValueOrDefault(label) + p7;
            pairs8[label] = pairs8.GetValueOrDefault(label) + p8;
            pairsWhole[label] = pairsWhole.GetValueOrDefault(label) + pw;
            groups7[label] = groups7.GetValueOrDefault(label) + g7.Count;
            groups8[label] = groups8.GetValueOrDefault(label) + g8.Count;

            var lost = MergeSliderProbe.Broken(a7, a8);
            if (lost.Count > 0) Add(broken, label, lost.Sum(l => l.Pairs));
            var gained = MergeSliderProbe.Broken(a8, a7);
            if (gained.Count > 0)
            {
                Add(extra, label, gained.Sum(g => g.Pairs));
                foreach (var (left, right, count) in gained)
                    instances.Append(string.Join('\t', count, label, set, file, capture.Size,
                        MergeSliderProbe.Flat(g8[left].Text),
                        string.Join(" ‖ ", right.Select(r => r < 0 ? "(dropped)" : MergeSliderProbe.Flat(g7[r].Text))))).Append('\n');
            }
            var gainedWhole = MergeSliderProbe.Broken(whole, a7);
            if (gainedWhole.Count > 0) Add(extraWhole, label, gainedWhole.Sum(g => g.Pairs));
            foreach (var (_, _, count) in lost)
                instances.Append(string.Join('\t', -count, label, set, file, capture.Size, "(整段 joined, 合併 parted)", "")).Append('\n');

            if (Watched.Contains(file))
            {
                watched.AppendLine($"--- {label} {set}/{file} size={capture.Size}");
                foreach (var g in g7) watched.AppendLine($"  7 {MergeSliderProbe.Flat(g.Text)}");
                foreach (var g in g8) watched.AppendLine($"  8 {MergeSliderProbe.Flat(g.Text)}");
            }
        }

        var summary = new StringBuilder();
        summary.AppendLine("frames: " + string.Join("  ", frames.OrderBy(p => p.Key).Select(p => $"{p.Key} {p.Value}")));
        summary.AppendLine($"groups 整段 D/P/M  {Row(groups7)}");
        summary.AppendLine($"groups 合併 D/P/M  {Row(groups8)}");
        summary.AppendLine($"pairs  整段       {Row(pairs7)}");
        summary.AppendLine($"pairs  合併       {Row(pairs8)}");
        summary.AppendLine($"pairs  整個區塊   {Row(pairsWhole)}");
        summary.AppendLine($"整段 joined, 合併 parted (frames | pairs)   {Cell(broken)}");
        summary.AppendLine($"合併 joined, 整段 parted (frames | pairs)   {Cell(extra)}");
        summary.AppendLine($"整個區塊 joined, 整段 parted (frames | pairs) {Cell(extraWhole)}");
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary + Environment.NewLine + watched);
        File.WriteAllText(Path.Combine(outDir, "extra.tsv"), instances.ToString());
        Console.Write(summary);
        Console.Write(watched);
        return 0;
    }

    private static void Add(Dictionary<string, (int Frames, long Pairs)> into, string label, long pairs)
    {
        var (f, p) = into.GetValueOrDefault(label);
        into[label] = (f + 1, p + pairs);
    }

    private static string Row(Dictionary<string, long> byLabel) =>
        string.Join(" ", new[] { "D", "P", "M" }.Select(l => byLabel.GetValueOrDefault(l).ToString().PadLeft(8)));

    private static string Cell(Dictionary<string, (int Frames, long Pairs)> byLabel) =>
        string.Join(" ", new[] { "D", "P", "M" }.Select(l => byLabel.GetValueOrDefault(l).Frames.ToString().PadLeft(5))) + " | " +
        string.Join(" ", new[] { "D", "P", "M" }.Select(l => byLabel.GetValueOrDefault(l).Pairs.ToString().PadLeft(7)));
}
