using System.Drawing;
using Microsoft.ML.OnnxRuntime;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// ogkalu/comic-text-and-bubble-detector (RT-DETR-v2), converted to fp16: every speech bubble on the
/// page, and every block of text inside or outside one.
/// </summary>
/// <remarks>
/// <para>Input is always 640×640, the whole page squashed to it without cropping — the way it was
/// trained. The export's anchors are fixed at that size: 512, 800, 960 and 1024 were all measured and
/// every one collapsed (recall 0.22–0.48).</para>
///
/// <para>Score floor 0.3. 0.3, 0.4 and 0.5 read identically on all three corpora; below it only noise
/// comes in (ja2: 1 noise group at 0.3, 5 at 0.25, 8 at 0.2, 19 at 0.15) and nothing real — the small
/// title 剣聖 is still missing at 0.15.</para>
///
/// <para>The fp16 file keeps TopK, GatherElements and Cast in fp32 and had the 49 duplicate Casts the
/// conversion tool inserted removed. Against the fp32 original it is 12ms faster, uses 190MB less video
/// memory and is half the download, for one sentence on ja2 read in two pieces instead of one.</para>
/// </remarks>
internal sealed class MangaTextDetector : IDisposable
{
    private const int InputSize = 640;
    private const float ScoreFloor = 0.3f;
    // Boxes thinner than this are slivers, not text.
    private const float MinimumSide = 4;
    // RT-DETR sometimes answers text_bubble and text_free for the same text; the lower score goes.
    private const double DuplicateOverlap = 0.7;

    private readonly InferenceSession _session;

    internal MangaTextDetector(string path, SessionOptions options) =>
        _session = new InferenceSession(path, options);

    public void Dispose() => _session.Dispose();

    /// <param name="rgb">The whole page as packed RGB (see <see cref="PillowImage.Rgb"/>).</param>
    internal MangaDetections Detect(byte[] rgb, int width, int height)
    {
        var small = PillowImage.Resize(rgb, width, height, 3, InputSize, InputSize);
        int plane = InputSize * InputSize;
        var input = new float[3 * plane];
        for (int p = 0; p < plane; p++)
        {
            input[p] = small[p * 3] / 255f;
            input[plane + p] = small[p * 3 + 1] / 255f;
            input[2 * plane + p] = small[p * 3 + 2] / 255f;
        }

        using var run = new RunOptions();
        using var images = OrtValue.CreateTensorValueFromMemory(input, [1, 3, InputSize, InputSize]);
        using var sizes = OrtValue.CreateTensorValueFromMemory(new long[] { width, height }, [1, 2]);
        using var outputs = _session.Run(run, ["images", "orig_target_sizes"], [images, sizes], _session.OutputNames);

        var names = _session.OutputNames.ToList();
        var labels = outputs[names.IndexOf("labels")].GetTensorDataAsSpan<long>();
        var boxes = outputs[names.IndexOf("boxes")].GetTensorDataAsSpan<float>();
        var scores = outputs[names.IndexOf("scores")].GetTensorDataAsSpan<float>();

        var found = new List<MangaBox>();
        for (int i = 0; i < scores.Length; i++)
        {
            if (scores[i] < ScoreFloor) continue;
            float x0 = Math.Max(0, boxes[i * 4]), y0 = Math.Max(0, boxes[i * 4 + 1]);
            float x1 = Math.Min(width, boxes[i * 4 + 2]), y1 = Math.Min(height, boxes[i * 4 + 3]);
            if (x1 - x0 < MinimumSide || y1 - y0 < MinimumSide) continue;
            found.Add(new MangaBox((MangaBoxKind)labels[i], scores[i], RectangleF.FromLTRB(x0, y0, x1, y1)));
        }

        return new MangaDetections(
            WithoutDuplicates(found.Where(box => box.Kind != MangaBoxKind.Bubble)),
            [.. found.Where(box => box.Kind == MangaBoxKind.Bubble)]);
    }

    /// <summary>Highest score first; a box mostly covered by one already kept is the same text.</summary>
    internal static List<MangaBox> WithoutDuplicates(IEnumerable<MangaBox> text)
    {
        var kept = new List<MangaBox>();
        foreach (var box in text.OrderByDescending(box => box.Score))
        {
            if (kept.Any(other =>
                {
                    var shared = RectangleF.Intersect(other.Bounds, box.Bounds);
                    return (double)shared.Width * shared.Height >
                           DuplicateOverlap * Math.Min(Area(other.Bounds), Area(box.Bounds));
                }))
                continue;
            kept.Add(box);
        }

        return kept;
    }

    private static double Area(RectangleF r) => (double)r.Width * r.Height;
}

internal enum MangaBoxKind
{
    Bubble = 0,
    TextInBubble = 1,
    TextOutsideBubble = 2,
}

internal readonly record struct MangaBox(MangaBoxKind Kind, float Score, RectangleF Bounds);

/// <param name="Text">Text blocks, highest score first — the order they are read in.</param>
/// <param name="Bubbles">Speech bubble outlines; used only to tell which blocks belong together.</param>
internal sealed record MangaDetections(List<MangaBox> Text, List<MangaBox> Bubbles);
