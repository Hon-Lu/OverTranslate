using System.Drawing;
using System.IO;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;

// What ChromaticBoxRepair would do if the realtime path ran it, with the detector size held still.
//
// The repair is screenshot-only today, and its remarks give the reason: "a frame missed there is
// repaired by the next one 250ms later". Whether that holds is a question about the pixels, and it
// cannot be answered by comparing the two flows as shipped — they read at different detector sizes,
// so any difference is the size and the repair together. This holds the size at what
// RealtimeDetectorSize would ask for and toggles nothing but the repair.
//
// Prints, per image: the text at the realtime size as shipped, the same size with the repair, and
// the screenshot flow's own reading beside them as the reference the repair was tuned on.
internal static class RealtimeRepairProbe
{
    internal static int Run(string[] args)
    {
        var panel = args.Contains("--panel");
        // The screenshot flow's reading of the same capture, for judging which way a change went.
        // It costs a third read of every image and answers nothing about the realtime path on its
        // own — the sizes differ — so a corpus run for regressions leaves it off.
        var reference = args.Contains("--reference");
        args = [.. args.Where(argument => argument is not ("--panel" or "--reference"))];
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --realtime-repair [--panel] <lang> <image.png> [more.png ...]");
            return 1;
        }

        var language = args[0];
        var mode = panel ? RealtimeBlockMode.Panel : RealtimeBlockMode.Subtitle;
        using var engine = new OnnxOcrEngine();

        var changed = 0;
        var longer = 0;
        var shorter = 0;
        var seamDiffers = 0;

        foreach (var path in args.Skip(1))
        {
            using var bitmap = new Bitmap(path);
            var (primary, _) = RealtimeDetectorSize.For(bitmap.Width, bitmap.Height, mode);

            var shipped = Read(engine, bitmap, language, primary, repair: false);
            var repaired = Read(engine, bitmap, language, primary, repair: true);

            // What the realtime path returned before this branch: the library's one-shot Detect,
            // no seam and no repair. Kept as the comparison it was — the two agreeing on captures
            // where nothing is repaired is what makes the change's blast radius the repair itself
            // rather than the move onto the seam. It now goes through the seam as well, so the
            // shipped reading and this one differ only where a row was repaired.

            var oneShot = (engine.TryRecognizeAsync(bitmap, language, primary)
                    .GetAwaiter().GetResult() ?? [])
                .OrderBy(block => block.Bounds.Top).ThenBy(block => block.Bounds.Left)
                .Select(block => block.Text)
                .Where(text => text.Length > 0)
                .ToList();
            var seamMoved = !shipped.SequenceEqual(oneShot);
            if (seamMoved) seamDiffers++;

            var moved = !shipped.SequenceEqual(repaired);
            if (moved)
            {
                changed++;
                var delta = repaired.Sum(Length) - shipped.Sum(Length);
                if (delta > 0) longer++;
                else if (delta < 0) shorter++;
            }

            Console.WriteLine();
            Console.WriteLine("=".PadRight(78, '='));
            Console.WriteLine($"IMAGE: {Path.GetFileName(path)}  {bitmap.Width}x{bitmap.Height}  " +
                $"detect={primary}  mode={mode}  {(moved ? "CHANGED" : "same")}" +
                (seamMoved ? "  SEAM-DIFFERS" : ""));
            Dump("  realtime         ", shipped);
            if (seamMoved) Dump("  realtime one-shot", oneShot);
            if (moved) Dump("  realtime+repair  ", repaired);
            if (reference) Dump("  screenshot       ", Read(engine, bitmap, language, null, repair: true));
        }

        Console.WriteLine();
        Console.WriteLine($"TOTAL images={args.Length - 1} changed={changed} " +
            $"(more text={longer}, less text={shorter}) seam-differs={seamDiffers}");
        return 0;
    }

    private static void Dump(string label, IReadOnlyList<string> lines)
    {
        Console.WriteLine($"{label}{lines.Count} block(s), {lines.Sum(Length)} chars");
        foreach (var line in lines) Console.WriteLine($"      {line}");
    }

    // Reading order, so two readings of the same capture are comparable line by line: the boxes are
    // returned in the detector's own order, which is not the page's.
    private static List<string> Read(
        OnnxOcrEngine engine, Bitmap bitmap, string language, int? size, bool repair)
    {
        // One call with every box, not one call per box: the filters after recognition read the
        // whole page — a lone ideograph is only stripped where nothing else CJK survives — so
        // recognising boxes one at a time judges each of them in a context the app never has.
        using var session = engine.BeginDetection(bitmap, language, size, repairRows: repair);
        return [.. session.Recognize([.. Enumerable.Range(0, session.Boxes.Count)])
            .OrderBy(block => block.Bounds.Top)
            .ThenBy(block => block.Bounds.Left)
            .Select(block => block.Text)
            .Where(text => text.Length > 0)];
    }

    private static int Length(string text) => text.Count(c => !char.IsWhiteSpace(c));
}
