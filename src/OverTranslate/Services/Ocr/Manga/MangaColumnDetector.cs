using System.Drawing;
using Microsoft.ML.OnnxRuntime;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// CRAFT (clovaai, MIT) over one block at a time: where its characters are, and what joins them,
/// for <see cref="MangaColumnCuts"/> to find its columns.
/// </summary>
/// <remarks>
/// <para>Exported from the <c>craft_mlt_25k</c> weights to ONNX at fp16 with float inputs and
/// outputs (<c>tools/MangaModelExport/export_craft.py</c>); fp16 and fp32 found the same columns on
/// every block measured. The output is half the input's size each way: a region score and a link
/// score for every two pixels.</para>
///
/// <para>Always one size in: DirectML builds its kernels for the shape it is given, and a block of
/// a new shape took 52ms against 8 for one it had seen. So the block is scaled to fit
/// <see cref="InputSize"/> square — never past <see cref="MaxScale"/>, which is as far as small
/// writing gains from it — and set in the top left corner on white.</para>
///
/// <para>MEASURED on the composite balloons and the evaluation set's blocks of twenty characters or
/// more, cut and read: 384, 512 and 640 square cut as well as each block at its own size (2.5, 2.4
/// and 2.3% of characters wrong on the composites at full size, 1.9 at its own; 1.7, 1.6 and 1.9 on
/// the real blocks, 1.6 at its own). 384 runs in 7ms on an RTX 3070, 512 in 30.</para>
/// </remarks>
internal sealed class MangaColumnDetector : IDisposable
{
    /// <summary>The side of the square every block is set in.</summary>
    internal const int InputSize = 384;

    /// <summary>The most a block is enlarged.</summary>
    internal const double MaxScale = 4;

    // ImageNet's, on 0–255, as CRAFT was trained.
    private static readonly float[] Mean = [123.675f, 116.28f, 103.53f];
    private static readonly float[] Deviation = [58.395f, 57.12f, 57.375f];

    private readonly InferenceSession _session;

    internal MangaColumnDetector(string path, SessionOptions options) =>
        _session = new InferenceSession(path, options);

    public void Dispose() => _session.Dispose();

    /// <param name="rgb">The whole page as packed RGB (see <see cref="PillowImage.Rgb"/>).</param>
    internal MangaColumnCuts.Maps Detect(byte[] rgb, int width, int height, RectangleF block)
    {
        var crop = MangaPageLayout.RecognitionCrop(block, width, height);
        var pixels = new byte[crop.Width * crop.Height * 3];
        for (int y = 0; y < crop.Height; y++)
            Array.Copy(rgb, ((crop.Y + y) * width + crop.X) * 3, pixels, y * crop.Width * 3, crop.Width * 3);

        double scale = Math.Min(MaxScale, (double)InputSize / Math.Max(crop.Width, crop.Height));
        int w = Math.Clamp((int)(crop.Width * scale), 1, InputSize), h = Math.Clamp((int)(crop.Height * scale), 1, InputSize);
        var small = PillowImage.Resize(pixels, crop.Width, crop.Height, 3, w, h);

        int plane = InputSize * InputSize;
        var input = new float[3 * plane];
        for (int c = 0; c < 3; c++)
            Array.Fill(input, (255 - Mean[c]) / Deviation[c], c * plane, plane);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        for (int c = 0; c < 3; c++)
            input[c * plane + y * InputSize + x] = (small[(y * w + x) * 3 + c] - Mean[c]) / Deviation[c];

        using var run = new RunOptions();
        using var image = OrtValue.CreateTensorValueFromMemory(input, [1, 3, InputSize, InputSize]);
        using var outputs = _session.Run(run, ["image"], [image], _session.OutputNames);
        var scores = outputs[0].GetTensorDataAsSpan<float>();

        int side = InputSize / 2, cells = side * side;
        var region = new float[cells];
        var affinity = new float[cells];
        for (int i = 0; i < cells; i++)
        {
            region[i] = scores[2 * i];
            affinity[i] = scores[2 * i + 1];
        }

        return new MangaColumnCuts.Maps(region, affinity, side, side, 2 / scale,
            new Size((w + 1) / 2, (h + 1) / 2));
    }
}
