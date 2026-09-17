using System.Diagnostics;
using System.Drawing;
using System.IO;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

// Can a cheap detection answer "is there any text here at all" well enough to replace a full pass?
//
// The realtime loop pays a complete recognition — detect plus recognise, at the size the mode asks
// for — twice over for a question that needs neither: the blind scan a region with no known text
// runs on a timer, and the full rescan that catches text appearing outside the watched strips. Both
// only want a yes or no, and detection is 85% of what they spend to get it.
//
// So: run detection alone, at a fraction of the size, and see whether the answer survives. The
// corpus is already labelled for this — a frame named primaryok held text the shipped size read,
// and one named unread held nothing any size could read, which the test-image README is explicit
// about ("reading something on these is a bad thing, not a good one").
//
// What this prints is the raw material for a threshold, not a verdict: per size, how many boxes came
// back and what the best one scored, on both halves of the corpus. If the two halves do not separate
// at any size, the idea is dead and nothing needs implementing to find that out.
internal static class TextPresenceProbe
{
    internal static int Run(string[] args)
    {
        var panel = args.Contains("--panel");
        args = [.. args.Where(argument => argument != "--panel")];
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --text-presence [--panel] <lang> <image.png> [more.png ...]");
            return 1;
        }

        var language = args[0];
        var mode = panel ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
        using var engine = new OnnxOcrEngine();

        // Fractions of the region's own long side. The lowest is well under anything the shipped
        // sizes use; the highest is what the mode would ask for, which is the thing to beat.
        double[] fractions = [0.20, 0.30, 0.40, 0.50];

        var rows = new List<(bool HasText, int Size, double Fraction, int Boxes, float Best, double Ms)>();

        foreach (var path in args.Skip(1))
        {
            var name = Path.GetFileName(path);
            // rescued frames are text the primary size missed and a fallback found: text, but the
            // hardest kind, so they are counted with the positives rather than set aside.
            var hasText = name.Contains("primaryok") || name.Contains("rescued");
            if (!hasText && !name.Contains("unread")) continue;

            using var bitmap = new Bitmap(path);
            var native = Math.Max(bitmap.Width, bitmap.Height);
            var (primary, _) = RealtimeDetectorSize.For(bitmap.Width, bitmap.Height, mode);

            Console.Write($"{name,-46} {(hasText ? "TEXT " : "empty")}");
            foreach (var fraction in fractions)
            {
                var size = Math.Max(320, (int)(native * fraction + 31) / 32 * 32);
                var (boxes, best, ms) = Detect(engine, bitmap, language, size);
                rows.Add((hasText, size, fraction, boxes, best, ms));
                Console.Write($"  {fraction:0.00}:{boxes,2}@{best:0.00}/{ms:0}ms");
            }

            var reference = Detect(engine, bitmap, language, primary);
            rows.Add((hasText, primary, 1.0, reference.Boxes, reference.Best, reference.Ms));
            Console.WriteLine($"  | primary({primary}):{reference.Boxes,2}@{reference.Best:0.00}/{reference.Ms:0}ms");
        }

        Console.WriteLine();
        Console.WriteLine("fraction  size    | text: found / total  median boxes | empty: found / total  median boxes | ms");
        foreach (var fraction in fractions.Append(1.0))
        {
            var set = rows.Where(r => Math.Abs(r.Fraction - fraction) < 0.001).ToList();
            if (set.Count == 0) continue;
            var text = set.Where(r => r.HasText).ToList();
            var empty = set.Where(r => !r.HasText).ToList();
            Console.WriteLine(
                $"{(fraction >= 1.0 ? "primary" : fraction.ToString("0.00")),-9} " +
                $"{(int)set.Average(r => r.Size),-7} | " +
                $"{text.Count(r => r.Boxes > 0),4} / {text.Count,-4} {Median(text),13} | " +
                $"{empty.Count(r => r.Boxes > 0),5} / {empty.Count,-4} {Median(empty),14} | " +
                $"{set.Average(r => r.Ms):0}ms");
        }
        return 0;
    }

    private static int Median(List<(bool HasText, int Size, double Fraction, int Boxes, float Best, double Ms)> set)
    {
        if (set.Count == 0) return 0;
        var counts = set.Select(r => r.Boxes).OrderBy(n => n).ToList();
        return counts[counts.Count / 2];
    }

    // Detection only — no recognition, no filtering. Fastest of three, the first being the one that
    // also pays for whatever the runtime lazily sets up.
    private static (int Boxes, float Best, double Ms) Detect(
        OnnxOcrEngine engine, Bitmap bitmap, string language, int size)
    {
        var ms = double.MaxValue;
        IReadOnlyList<(System.Windows.Rect Bounds, float Score)> boxes = [];
        for (var run = 0; run < 3; run++)
        {
            var watch = Stopwatch.StartNew();
            boxes = engine.DetectBoxesOnly(bitmap, language, size);
            ms = Math.Min(ms, watch.Elapsed.TotalMilliseconds);
        }
        return (boxes.Count, boxes.Count == 0 ? 0f : boxes.Max(b => b.Score), ms);
    }
}
