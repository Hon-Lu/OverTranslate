using System.Text;

namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// The host side of the Hayai graphs: everything whose shape depends on the block, worked out here so
/// the graphs themselves can be fixed in shape.
/// </summary>
/// <remarks>
/// <para>DirectML compiles a graph for the shapes it first sees. The export takes every
/// data-dependent shape out of the graphs — how many patches a block becomes, where the position
/// embedding is sampled, which patches the projector merges, the rotary angles, the attention masks
/// — and pads to the largest case, so one compilation serves every block. MEASURED by the survey
/// (findings-B1): with shapes left dynamic, a prefill of a new shape took 100ms and each step 14ms;
/// fixed, 4ms and 2.5ms.</para>
///
/// <para>Each function is a port of the reference in the survey's scripts (<c>run_hayai_ort2.py</c>,
/// <c>hayai_onnx.py</c>, <c>aa_resize.py</c>), which were checked against the model's own
/// PyTorch preprocessing.</para>
/// </remarks>
internal static class HayaiInputs
{
    internal const int MaxPatches = 384;
    internal const int PatchSize = 16;
    internal const int PatchValues = PatchSize * PatchSize * 3;
    internal const int Width = 768;
    /// <summary>Vision tokens after the projector merges 2×2 patches; the most a block can have.</summary>
    internal const int MaxVisionTokens = 192;
    internal const int RopeDim = 32;
    private const int Grid = 16;

    /// <summary>
    /// The size a block is resized to: both sides multiples of 16, as large as fits in
    /// <see cref="MaxPatches"/> patches, the aspect ratio kept — SigLIP2 NaFlex's rule.
    /// </summary>
    internal static (int Height, int Width) Size(int height, int width)
    {
        static int Side(double scale, int n) =>
            Math.Max(PatchSize, (int)(Math.Ceiling(n * scale / PatchSize) * PatchSize));

        // A bisection on the scale, as the reference does it, step for step.
        double low = 1e-6, high = 100.0;
        while (high - low >= 1e-5)
        {
            double scale = (low + high) / 2;
            if ((Side(scale, height) / PatchSize) * (Side(scale, width) / PatchSize) <= MaxPatches) low = scale;
            else high = scale;
        }

        return (Side(low, height), Side(low, width));
    }

    /// <summary>
    /// Bilinear resampling with antialiasing along one axis (PyTorch's <c>antialias=True</c>, which is
    /// Pillow's triangle filter), as a <paramref name="to"/> × <paramref name="from"/> matrix.
    /// </summary>
    internal static double[,] Resampling(int from, int to)
    {
        double scale = (double)from / to;
        double support = scale >= 1 ? scale : 1.0;
        double inverse = scale >= 1 ? 1.0 / scale : 1.0;
        var matrix = new double[to, from];
        for (int i = 0; i < to; i++)
        {
            double centre = scale * (i + 0.5);
            int low = Math.Max((int)(centre - support + 0.5), 0);
            int high = Math.Min((int)(centre + support + 0.5), from);
            double total = 0;
            var w = new double[high - low];
            for (int j = 0; j < w.Length; j++)
            {
                w[j] = Math.Max(0.0, 1 - Math.Abs((j + low - centre + 0.5) * inverse));
                total += w[j];
            }

            for (int j = 0; j < w.Length; j++)
                matrix[i, low + j] = w[j] / total;
        }

        return matrix;
    }

    /// <summary>
    /// The learned 16×16 grid of position embeddings resized to the block's patch grid, one row of
    /// <see cref="Width"/> per patch; rows past the block's patches repeat the first.
    /// </summary>
    /// <param name="grid">16×16×768, row-major.</param>
    internal static float[] Positions(float[] grid, int rows, int columns)
    {
        var down = Resampling(Grid, rows);
        var across = Resampling(Grid, columns);
        // Across first: [16 rows][columns][768].
        var half = new double[Grid * columns * Width];
        for (int i = 0; i < Grid; i++)
        for (int c = 0; c < columns; c++)
        {
            int o = (i * columns + c) * Width;
            for (int j = 0; j < Grid; j++)
            {
                double w = across[c, j];
                if (w == 0) continue;
                int g = (i * Grid + j) * Width;
                for (int d = 0; d < Width; d++) half[o + d] += w * grid[g + d];
            }
        }

        var positions = new float[MaxPatches * Width];
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < columns; c++)
        {
            int o = (r * columns + c) * Width;
            var sum = new double[Width];
            for (int i = 0; i < Grid; i++)
            {
                double w = down[r, i];
                if (w == 0) continue;
                int h = (i * columns + c) * Width;
                for (int d = 0; d < Width; d++) sum[d] += w * half[h + d];
            }

            for (int d = 0; d < Width; d++) positions[o + d] = (float)sum[d];
        }

        for (int p = rows * columns; p < MaxPatches; p++)
            Array.Copy(positions, 0, positions, p * Width, Width);
        return positions;
    }

    /// <summary>The vision tokens' grid: the patch grid halved, rounded up.</summary>
    internal static (int Rows, int Columns) Merged(int rows, int columns) => ((rows + 1) / 2, (columns + 1) / 2);

    /// <summary>
    /// Which four patches each vision token is made of — the 2×2 block at its place, the last row or
    /// column repeated where the grid is odd — four indices per token, zero past the last token.
    /// </summary>
    internal static long[] Gather(int rows, int columns)
    {
        var (mergedRows, mergedColumns) = Merged(rows, columns);
        var indices = new long[MaxVisionTokens * 4];
        for (int p = 0; p < mergedRows * mergedColumns; p++)
        {
            int y = p / mergedColumns, x = p % mergedColumns;
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int sy = Math.Min(y * 2 + dy, rows - 1), sx = Math.Min(x * 2 + dx, columns - 1);
                indices[p * 4 + dy * 2 + dx] = sy * columns + sx;
            }
        }

        return indices;
    }

    private static readonly double[] Frequencies =
        [.. Enumerable.Range(0, RopeDim / 2).Select(i => 1.0 / Math.Pow(10000.0, 2.0 * i / RopeDim))];

    /// <summary>
    /// Rotary angles for a vision token: the first half from its row, the second from its column.
    /// </summary>
    internal static void VisionAngles(int token, int mergedColumns, Span<double> angles)
    {
        double y = token / mergedColumns, x = token % mergedColumns;
        for (int k = 0; k < RopeDim / 2; k++)
        {
            angles[k] = y * Frequencies[k];
            angles[RopeDim / 2 + k] = x * Frequencies[k];
        }
    }

    /// <summary>Rotary angles for the text token at <paramref name="position"/>: both halves alike.</summary>
    internal static void TextAngles(int position, Span<double> angles)
    {
        for (int k = 0; k < RopeDim / 2; k++)
            angles[k] = angles[RopeDim / 2 + k] = position * Frequencies[k];
    }

    /// <summary>
    /// The block's pixels as the vision graph takes them: <see cref="MaxPatches"/> rows of 16×16×3
    /// values in [-1, 1], each patch's pixels row by row and each pixel R, G, B; rows past the
    /// block's patches zero.
    /// </summary>
    /// <param name="rgb">The block resized to <paramref name="height"/>×<paramref name="width"/>, packed RGB.</param>
    internal static void Patches(byte[] rgb, int height, int width, Span<float> into)
    {
        int columns = width / PatchSize, rows = height / PatchSize;
        into[..(MaxPatches * PatchValues)].Clear();
        for (int py = 0; py < rows; py++)
        for (int px = 0; px < columns; px++)
        {
            int o = (py * columns + px) * PatchValues;
            for (int y = 0; y < PatchSize; y++)
            {
                int source = ((py * PatchSize + y) * width + px * PatchSize) * 3;
                for (int i = 0; i < PatchSize * 3; i++)
                    into[o + y * PatchSize * 3 + i] = (rgb[source + i] / 255f - 0.5f) / 0.5f;
            }
        }
    }
}

/// <summary>Hayai's byte-level BPE, decoding only: a token is a run of bytes spelled in GPT-2's byte alphabet.</summary>
internal sealed class HayaiVocabulary
{
    private readonly byte[][] _bytes;
    private readonly bool[] _special;

    internal HayaiVocabulary(IReadOnlyList<string> tokens, IEnumerable<int> special)
    {
        var fromChar = ByteAlphabet().Select((c, b) => (c, b)).ToDictionary(p => p.c, p => (byte)p.b);
        _bytes = [.. tokens.Select(token => token.Select(c => fromChar.TryGetValue(c, out var b) ? b : (byte)'?').ToArray())];
        _special = new bool[tokens.Count];
        foreach (var id in special) _special[id] = true;
    }

    internal int Count => _bytes.Length;

    /// <summary>The tokens' text, special tokens left out; bytes that are not UTF-8 become U+FFFD.</summary>
    internal string Decode(IEnumerable<long> ids)
    {
        var bytes = new List<byte>();
        foreach (var id in ids)
            if (id >= 0 && id < _bytes.Length && !_special[id]) bytes.AddRange(_bytes[id]);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>GPT-2's <c>bytes_to_unicode</c>: the character each byte is written as.</summary>
    internal static char[] ByteAlphabet()
    {
        var printable = Enumerable.Range('!', '~' - '!' + 1)
            .Concat(Enumerable.Range('¡', '¬' - '¡' + 1))
            .Concat(Enumerable.Range('®', 'ÿ' - '®' + 1))
            .ToHashSet();
        var alphabet = new char[256];
        int next = 0;
        for (int b = 0; b < 256; b++)
            alphabet[b] = printable.Contains(b) ? (char)b : (char)(256 + next++);
        return alphabet;
    }
}
