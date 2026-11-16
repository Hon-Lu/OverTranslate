using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
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
/// two groupers disagreed over, and <c>report.groups.txt</c>, 自動's own answer per image — the
/// file to diff between two builds.
/// </remarks>
internal static class AutoModeProbe
{
    internal sealed record Capture(
        string Image, string Language, string Label, int Width, int Height, int Size,
        GroupingPrototype.Block[] Blocks);

    internal static async Task<int> Run(string[] args)
    {
        if (args.Length < 3) throw new ArgumentException("--auto-probe capture cache.json @list | replay cache.json report.txt");
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

        var report = new StringBuilder();
        var extras = new StringBuilder();
        var answers = new StringBuilder();
        var totals = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        foreach (var capture in JsonSerializer.Deserialize<Capture[]>(File.ReadAllText(args[1]))!)
        {
            List<OcrTextBlock> Raw() => capture.Blocks.Select(b => b.Restore()).ToList();
            var reading = RealtimeAutoLayout.Read(OcrService.FilterForRealtime(Raw(), capture.Height));
            var asDialogue = OcrService.GroupRealtime(Raw(), capture.Height, RealtimeBlockMode.Subtitle);
            var asPanel = OcrService.GroupRealtime(Raw(), capture.Height, RealtimeBlockMode.Panel);
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
        Console.Write(summary);
        File.WriteAllText(args[2], summary.ToString() + Environment.NewLine + report);
        File.WriteAllText(Path.ChangeExtension(args[2], ".groups.txt"), answers.ToString());
        File.WriteAllText(Path.ChangeExtension(args[2], ".extras.tsv"),
            "label\tset\tverdict\tzones\trows\tperRow\tmaxAdv\tminAdv\th\tcpr\tlongest\tmarks\twordy\taspect\timage\ttext\n" + extras);
        return 0;
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
