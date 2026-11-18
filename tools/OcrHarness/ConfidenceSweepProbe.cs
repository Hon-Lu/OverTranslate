using System.IO;
using System.Text;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

/// <summary>
/// 短句辨識門檻 at every whole percent it offers: what each one drops from the cached frames, and
/// whether a higher one ever keeps what a lower one dropped.
/// </summary>
/// <remarks>
/// <c>--auto-probe confidence cache.json out-dir</c>. No OCR — the cache was read once. Two places
/// read the threshold on text written across, and both are counted, per label (D/P/M):
/// <list type="bullet">
/// <item>lines, before grouping (<see cref="OcrService.RejectUnconvincingBlocks"/>) — the one the
/// replay sees;</item>
/// <item>groups, after grouping on the same mode, by the session's own check
/// (<see cref="RealtimeTranslationSession.RejectShortReadings"/>), counting only the groups the
/// threshold took — not the ones under two characters, which go whatever it is.</item>
/// </list>
/// Writes sweep.tsv (one row a percent) and summary.txt; also checks, frame by frame, that every
/// line dropped at one percent is dropped at the next, and that 80 drops exactly the lines and
/// groups the measured floor (<see cref="ShortReadingDetection.ShortTextConfidenceFloor"/>) does.
/// Exits 1 if either fails.
/// </remarks>
internal static class ConfidenceSweepProbe
{
    internal static int Run(AutoModeProbe.Capture[] cache, string outDir)
    {
        Directory.CreateDirectory(outDir);
        string[] labels = ["D", "P", "M"];
        var rows = new StringBuilder("percent\tfloor\t" +
            string.Join('\t', labels.Select(l => $"{l}_lines")) + "\t" +
            string.Join('\t', labels.Select(l => $"{l}_groups")) + "\n");

        // Per frame, the lines (by index) the previous percent dropped.
        var previous = new HashSet<int>[cache.Length];
        int lineViolations = 0;
        var violationLog = new StringBuilder();
        var groupsAt = new Dictionary<int, int[]>();
        var dropsAt80 = new HashSet<int>[cache.Length];
        var groupsAt80 = new List<OcrTextBlock>[cache.Length];

        for (int percent = RealtimeProfile.MinConfidencePercent; percent <= RealtimeProfile.MaxConfidencePercent; percent++)
        {
            var profile = ResolvedProfile.Resolve(new RealtimeProfile { Id = $"c{percent}", ConfidencePercent = percent });
            double floor = profile.ConfidenceFloor;
            var lines = new int[labels.Length];
            var groups = new int[labels.Length];

            for (int i = 0; i < cache.Length; i++)
            {
                var capture = cache[i];
                int label = Array.IndexOf(labels, capture.Label);
                var raw = capture.Blocks.Select(b => b.Restore()).ToList();
                var kept = new HashSet<OcrTextBlock>(OcrService.RejectUnconvincingBlocks(raw, floor), ReferenceEqualityComparer.Instance);
                var dropped = Enumerable.Range(0, raw.Count).Where(k => !kept.Contains(raw[k])).ToHashSet();
                lines[label] += dropped.Count;

                if (previous[i] is { } before && !before.IsSubsetOf(dropped))
                {
                    lineViolations++;
                    foreach (var k in before.Except(dropped))
                        violationLog.AppendLine($"{percent}\t{Path.GetFileName(capture.Image)}\t{raw[k].Confidence:0.000}\t{raw[k].Text}");
                }
                previous[i] = dropped;

                var grouped = OcrService.GroupRealtime(capture.Blocks.Select(b => b.Restore()).ToList(), capture.Height, profile);
                groups[label] += grouped.Count(g =>
                    !ShortReadingDetection.IsTooShort(g.Text) &&
                    ShortReadingDetection.IsUnconvincingShortText(g.Text, g.Confidence, floor));
                var shown = RealtimeTranslationSession.RejectShortReadings(grouped, 0, floor);

                if (percent == RealtimePresets.ConfidencePercent)
                {
                    dropsAt80[i] = dropped;
                    groupsAt80[i] = shown;
                }
            }
            groupsAt[percent] = groups;
            rows.Append($"{percent}\t{floor:0.00}\t{string.Join('\t', lines)}\t{string.Join('\t', groups)}\n");
        }

        // 80 against the measured floor itself, as 標準 resolves it.
        int sameAs80 = 0, differentAt80 = 0;
        var standard = ResolvedProfile.Standard;
        for (int i = 0; i < cache.Length; i++)
        {
            var capture = cache[i];
            var raw = capture.Blocks.Select(b => b.Restore()).ToList();
            var kept = new HashSet<OcrTextBlock>(
                OcrService.RejectUnconvincingBlocks(raw, ShortReadingDetection.ShortTextConfidenceFloor), ReferenceEqualityComparer.Instance);
            var dropped = Enumerable.Range(0, raw.Count).Where(k => !kept.Contains(raw[k])).ToHashSet();
            var shown = RealtimeTranslationSession.RejectShortReadings(
                OcrService.GroupRealtime(capture.Blocks.Select(b => b.Restore()).ToList(), capture.Height, standard),
                0, ShortReadingDetection.ShortTextConfidenceFloor);
            bool same = dropped.SetEquals(dropsAt80[i]) &&
                shown.Count == groupsAt80[i].Count &&
                shown.Zip(groupsAt80[i]).All(pair => pair.First.Text == pair.Second.Text && pair.First.Bounds == pair.Second.Bounds);
            if (same) sameAs80++; else differentAt80++;
        }

        // Groups need not fall in step: a dropped line can split a group in two, or let two join.
        int groupSteps = 0;
        var groupNotes = new StringBuilder();
        for (int percent = RealtimeProfile.MinConfidencePercent + 1; percent <= RealtimeProfile.MaxConfidencePercent; percent++)
            for (int l = 0; l < labels.Length; l++)
                if (groupsAt[percent][l] < groupsAt[percent - 1][l])
                {
                    groupSteps++;
                    groupNotes.AppendLine($"  groups {labels[l]} {percent - 1}→{percent}: {groupsAt[percent - 1][l]}→{groupsAt[percent][l]}");
                }

        var summary = new StringBuilder();
        summary.AppendLine($"frames {cache.Length}: " + string.Join(", ", labels.Select(l => $"{l} {cache.Count(c => c.Label == l)}")));
        summary.AppendLine($"line drops monotonic: {(lineViolations == 0 ? "yes" : $"NO, {lineViolations} frame-steps")}");
        summary.AppendLine($"group drops never fewer at a higher percent: {(groupSteps == 0 ? "yes" : $"NO, {groupSteps} steps")}");
        summary.Append(groupNotes);
        summary.AppendLine($"80% against the measured floor: {sameAs80} frames identical, {differentAt80} different");
        File.WriteAllText(Path.Combine(outDir, "sweep.tsv"), rows.ToString());
        File.WriteAllText(Path.Combine(outDir, "violations.tsv"), "percent\timage\tconfidence\ttext\n" + violationLog);
        File.WriteAllText(Path.Combine(outDir, "summary.txt"), summary.ToString());
        Console.Write(summary);
        return lineViolations == 0 && differentAt80 == 0 ? 0 : 1;
    }
}
