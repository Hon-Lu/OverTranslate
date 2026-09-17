using System.Drawing;
using System.IO;
using System.Text.Json;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;

// Does the split detect/recognise seam return what the library's own one-shot Detect returns?
//
// RecognizeCore calls RapidOcr.Detect, which detects and recognises in one library call and leaves
// no point between the two. DetectionSession reimplements that split so a caller can see the boxes
// first — and until now it was only ever used by this harness, so nothing depended on the two
// agreeing. Repairing boxes before recognition needs the seam on the shipped screenshot path, which
// makes the agreement load-bearing: every capture where no repair fires must read exactly as it
// reads today, or the repair's small blast radius is an illusion created by measuring the seam
// against itself.
internal static class SessionEquivalenceProbe
{
    internal static int Run(string[] args)
    {
        var language = args[0];
        var output = args[1];
        var differing = 0;
        var rows = new List<object>();
        using var engine = new OnnxOcrEngine();

        foreach (var path in args.Skip(2))
        {
            using var bitmap = new Bitmap(path);

            var shipped = engine.RecognizeAsync(bitmap, language).GetAwaiter().GetResult();

            List<OcrTextBlock> seam;
            using (var session = engine.BeginDetection(bitmap, language))
                seam = session.Recognize(Enumerable.Range(0, session.Boxes.Count).ToArray()).ToList();

            // Text and geometry both, because a seam that reads the same words out of boxes a few
            // pixels apart is not the same input to grouping, which is where this has to be safe.
            var texts = !shipped.Select(b => b.Text).SequenceEqual(seam.Select(b => b.Text));
            var boxes = !shipped.Select(Box).SequenceEqual(seam.Select(Box));
            if (texts || boxes) differing++;

            rows.Add(new
            {
                path,
                language,
                shippedCount = shipped.Count,
                seamCount = seam.Count,
                textsDiffer = texts,
                boxesDiffer = boxes,
                shipped = shipped.Select(b => new { b.Text, box = Box(b) }).ToArray(),
                seam = seam.Select(b => new { b.Text, box = Box(b) }).ToArray(),
            });

            Console.WriteLine(
                $"SEAM {(texts || boxes ? "DIFFERS" : "same   ")} " +
                $"{shipped.Count}/{seam.Count} {Path.GetFileName(path)}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(
            output,
            JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true })
                .ReplaceLineEndings("\r\n"));
        return differing == 0 ? 0 : 1;
    }

    private static string Box(OcrTextBlock block) =>
        $"{block.Bounds.X:0.##},{block.Bounds.Y:0.##},{block.Bounds.Width:0.##},{block.Bounds.Height:0.##}";
}
