using System.Diagnostics;
using System.Drawing;
using System.IO;
using OverTranslate.Services.Realtime;

// What one poll costs when it decides NOT to recognise anything.
//
// The realtime loop looks at every watched region every 250ms, and most of those looks end at the
// fingerprint: the pixels have not moved, so nothing is recognised. That path is what sets the
// floor on how fast the loop could poll — whatever it costs, it is paid by every region on every
// tick whether or not anything happened — and it has never been measured on its own.
//
// The grab is not included: it comes from the live screen and cannot be replayed from a file. What
// this covers is the half that can be, which is also the half that scales with the region's size.
internal static class PollCostProbe
{
    internal static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: --poll-cost <image.png> [more.png ...]");
            return 1;
        }

        Console.WriteLine($"{"image",-52} {"size",-12} {"whole",8} {"strips",8}");
        foreach (var path in args)
        {
            using var bitmap = new Bitmap(path);

            // Two shapes, because the loop uses both. A region with nothing known in it is
            // summarised whole; once a pass has found text, only the strips it sits in are, which
            // is the common case during a session and the cheaper one.
            var whole = Fastest(bitmap, null);
            var bands = Fastest(bitmap, Strips(bitmap));

            Console.WriteLine($"{Path.GetFileName(path),-52} {bitmap.Width}x{bitmap.Height,-7} " +
                $"{whole,6:0.00}ms {bands,6:0.00}ms");
        }
        return 0;
    }

    // Two bands across the middle of the region, the shape a subtitle or a pair of dialogue lines
    // leaves behind: a fifth of the height each, which is what the strips of a read line look like.
    private static List<Rectangle> Strips(Bitmap bitmap)
    {
        var height = Math.Max(1, bitmap.Height / 5);
        return
        [
            new Rectangle(0, bitmap.Height / 3, bitmap.Width, height),
            new Rectangle(0, bitmap.Height / 3 + height, bitmap.Width, height),
        ];
    }

    // Fastest of twenty, for the same reason the repair's cost is measured that way: an average
    // over a machine that is also running inference is mostly scheduling noise.
    private static double Fastest(Bitmap bitmap, IReadOnlyList<Rectangle>? areas)
    {
        var best = double.MaxValue;
        for (var run = 0; run < 20; run++)
        {
            var watch = Stopwatch.StartNew();
            FrameFingerprint.Capture(bitmap, areas);
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return best;
    }
}
