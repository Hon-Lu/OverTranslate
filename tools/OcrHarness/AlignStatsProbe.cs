using System.IO;
using System.Text;
using OverTranslate.Layout;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;

/// <summary>
/// 譯文對齊 over the 自動 corpus: every group 標準 makes of a cached frame, where it was drawn before
/// the value existed and where 自動 draws it now.
/// </summary>
/// <remarks>
/// <c>--auto-probe align cache.json out.txt</c> — the totals per label and the way each group is
/// drawn, then one line per group whose alignment changes. Before is a band centred on its source
/// for a group read as a passage and rows from their left edge for one read as items; a group on a
/// tilted card is set along the card either way and is not counted. Grouping is 標準's own, so the
/// groups are those of report.own.txt.
/// </remarks>
internal static class AlignStatsProbe
{
    public static int Run(AutoModeProbe.Capture[] cache, string outPath)
    {
        var profile = ResolvedProfile.Standard;
        var totals = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        var changes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var lines = new StringBuilder("label\tread\tbefore\tafter\trows\tset\timage\tsize\tbounds\ttext\n");
        foreach (var capture in cache)
        {
            var raw = capture.Blocks.Select(b => b.Restore()).ToList();
            var groups = OcrService.GroupRealtime(raw, capture.Height, profile);
            var set = Path.GetFileName(Path.GetDirectoryName(capture.Image))!;
            foreach (var group in groups)
            {
                if (group.Tilt is not null) continue;
                var drawnAs = group.ReadAs ?? RealtimeBlockMode.Subtitle;
                var before = drawnAs == RealtimeBlockMode.Panel ? SourceAlignment.Left : SourceAlignment.Center;
                var after = RealtimeSourceAlignment.For(profile.Align, drawnAs, group.SourceLineBounds) ?? before;
                int rows = group.SourceLineBounds?.Count ?? 1;
                var key = $"{capture.Label} {(drawnAs == RealtimeBlockMode.Panel ? "P" : "D")}";
                if (!totals.TryGetValue(key, out var total)) totals[key] = total = new int[3];
                total[0]++;
                if (rows > 1) total[1]++;
                if (after == before) continue;
                total[2]++;
                var change = $"{key} {before}->{after}";
                changes[change] = changes.GetValueOrDefault(change) + 1;
                lines.AppendLine(string.Join('\t', capture.Label, drawnAs == RealtimeBlockMode.Panel ? "P" : "D", before, after, rows,
                    set, Path.GetFileName(capture.Image), capture.Size, group.Bounds,
                    group.Text.Replace("\r", "").Replace("\n", " ⏎ ")));
            }
        }

        var summary = new StringBuilder("label read  groups  multi-row  changed\n");
        foreach (var (key, t) in totals) summary.AppendLine($"{key,-10} {t[0],6} {t[1],10} {t[2],8}");
        summary.AppendLine($"{"all",-10} {totals.Values.Sum(t => t[0]),6} {totals.Values.Sum(t => t[1]),10} {totals.Values.Sum(t => t[2]),8}");
        summary.AppendLine().AppendLine("change                  groups");
        foreach (var (key, count) in changes) summary.AppendLine($"{key,-22} {count,6}");
        File.WriteAllText(outPath, summary.ToString());
        File.WriteAllText(Path.ChangeExtension(outPath, ".tsv"), lines.ToString());
        Console.Write(summary);
        return 0;
    }
}
