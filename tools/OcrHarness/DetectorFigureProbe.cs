using System.Drawing;
using System.IO;
using System.Text;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

/// <summary>
/// 字幕・漫畫 and 遊戲介面・聊天室 read with their detector size as a figure (85%, 68%) against the
/// same presets with it left to 自動, through the real recogniser: the size chain the session asks
/// for, each size read until one finds something, and the grouped result.
/// </summary>
/// <remarks>
/// <c>--auto-probe detector @list.txt out.tsv</c> — each line of the list is
/// <c>path TAB language TAB label</c>, as for capture. Every image is read four ways (two presets ×
/// figure and 自動), horizontal; a line per image and preset says whether the sizes and the
/// reading came out identical. Exit 1 on any difference.
/// </remarks>
internal static class DetectorFigureProbe
{
    internal static async Task<int> Run(string listPath, string outPath)
    {
        using var engine = new OnnxOcrEngine();
        var report = new StringBuilder("label\tpreset\twidth\theight\tsizes(figure)\tsizes(auto)\tsameSizes\tsameReading\tgroups\timage\n");
        int compared = 0, differing = 0;
        foreach (var entry in File.ReadAllLines(listPath).Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var fields = entry.Split('\t');
            using var bitmap = new Bitmap(fields[0]);
            foreach (var id in new[] { RealtimePresets.SubtitleId, RealtimePresets.InterfaceId })
            {
                var figure = ResolvedProfile.Resolve(id, []);
                var automatic = ResolvedProfile.Resolve(new RealtimeProfile
                {
                    Id = id, BasedOn = id,
                    SettleSeconds = RealtimePresets.Automatic, DetectorPercent = RealtimePresets.Automatic,
                });

                var (a, aSizes) = await Read(engine, bitmap, fields[1], figure);
                var (b, bSizes) = await Read(engine, bitmap, fields[1], automatic);
                bool sameSizes = aSizes.SequenceEqual(bSizes);
                bool sameReading = a.Count == b.Count &&
                    a.Zip(b).All(p => p.First.Text == p.Second.Text && p.First.Bounds == p.Second.Bounds);
                compared++;
                if (!sameSizes || !sameReading) differing++;
                report.AppendLine(string.Join('\t', fields[2], id, bitmap.Width, bitmap.Height,
                    string.Join(",", aSizes), string.Join(",", bSizes), sameSizes, sameReading, a.Count, fields[0]));
                Console.WriteLine($"{(sameSizes && sameReading ? "same" : "DIFF")} {id,-9} {fields[2]} {bitmap.Width}x{bitmap.Height} [{string.Join(",", aSizes)}] {Path.GetFileName(fields[0])}");
            }
        }
        File.WriteAllText(outPath, report.ToString());
        Console.WriteLine($"{compared - differing}/{compared} identical");
        return differing == 0 ? 0 : 1;
    }

    // The session's order: the first size, then each fallback while nothing has been read.
    private static async Task<(List<OcrTextBlock> Groups, List<int> Sizes)> Read(
        OnnxOcrEngine engine, Bitmap bitmap, string language, ResolvedProfile profile)
    {
        var state = new RealtimeRegionState();
        var (primary, fallbacks) = RealtimeDetectorSize.For(
            bitmap.Width, bitmap.Height, state.ReadsAs(profile.Layout), RealtimeTextOrientation.Horizontal,
            profile.DetectorFraction);
        var sizes = new List<int>();
        foreach (var size in new[] { primary }.Concat(fallbacks))
        {
            sizes.Add(size);
            var raw = await engine.TryRecognizeAsync(bitmap, language, size)
                ?? throw new InvalidOperationException("No OCR slot");
            var groups = TiltedOverlap.Level(OcrService.GroupRealtime(raw, bitmap.Height, profile));
            if (groups.Count > 0) return (groups, sizes);
        }
        return ([], sizes);
    }
}
