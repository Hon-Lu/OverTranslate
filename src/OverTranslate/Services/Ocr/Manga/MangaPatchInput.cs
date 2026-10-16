namespace OverTranslate.Services.Ocr.Manga;

/// <summary>
/// What CPT-flex's encoder takes for one block: the block in grey with its shape kept, cut into
/// 16px patches, which of the patches are real, and where each one sits.
/// </summary>
/// <remarks>
/// <para>The author's <c>preprocess(fit="aspect")</c>, which the model was trained and evaluated on.
/// The block is shrunk or grown, keeping its shape, to the largest size whose grid of 16px patches is
/// at most <see cref="Patches"/>; it sits at the top left of the grid and the rest of the grid is
/// filled with the middle value of its own edge. The grid's patches come first, row by row, and the
/// rest of the 196 are padding the model is told to ignore.</para>
///
/// <para>The model learned positions for a 14×14 grid and the author resizes that table to the grid
/// bicubically. The encoder holds the table; this gives it the resize as weights over the table's 196
/// cells, one row per patch, which is the same sum. MEASURED: built this way, the encoder input is the
/// one the author's Python builds, bit for bit, and the readings match the author's on all but one
/// of the 2031 blocks of the shared evaluation set.</para>
/// </remarks>
internal static class MangaPatchInput
{
    internal const int Patches = 196;
    internal const int PatchSize = 16;
    internal const int PatchLength = 3 * PatchSize * PatchSize;
    private const int TableSide = 14;

    /// <summary>
    /// The largest scale whose grid holds at most <see cref="Patches"/> patches, and that grid —
    /// found by the author's bisection, so the edge cases land where the model was trained.
    /// </summary>
    internal static (double Scale, int Rows, int Columns) Grid(int height, int width)
    {
        double low = 1e-4, high = 1000.0;
        for (int i = 0; i < 60; i++)
        {
            double middle = (low + high) / 2;
            if (Math.Ceiling(height * middle / PatchSize) * Math.Ceiling(width * middle / PatchSize) <= Patches)
                low = middle;
            else
                high = middle;
        }

        return (low, (int)Math.Ceiling(height * low / PatchSize), (int)Math.Ceiling(width * low / PatchSize));
    }

    /// <summary>
    /// Writes one block's encoder input: <paramref name="patches"/> [196 × 768],
    /// <paramref name="mask"/> [196] and <paramref name="positions"/> [196 × 196], all cleared first.
    /// </summary>
    /// <param name="grey">The block in grey (<see cref="PillowImage.Luma"/>), one byte a pixel.</param>
    internal static void Fill(byte[] grey, int width, int height, Span<float> patches, Span<float> mask, Span<float> positions)
    {
        patches.Clear();
        mask.Clear();
        positions.Clear();

        var (scale, rows, columns) = Grid(height, width);
        // Python's round, which is the banker's rounding Math.Round does.
        int smallWidth = Math.Max(1, Math.Min(columns * PatchSize, (int)Math.Round(width * scale)));
        int smallHeight = Math.Max(1, Math.Min(rows * PatchSize, (int)Math.Round(height * scale)));
        var small = PillowImage.Resize(grey, width, height, 1, smallWidth, smallHeight);
        byte fill = BorderMedian(small, smallWidth, smallHeight);

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                var patch = patches.Slice((row * columns + column) * PatchLength, PatchLength);
                for (int y = 0; y < PatchSize; y++)
                {
                    int sy = row * PatchSize + y;
                    for (int x = 0; x < PatchSize; x++)
                    {
                        int sx = column * PatchSize + x;
                        byte value = sy < smallHeight && sx < smallWidth ? small[sy * smallWidth + sx] : fill;
                        float v = (value / 255f - 0.5f) / 0.5f;
                        // Channel, then row, then column; the three channels are the same grey.
                        int at = y * PatchSize + x;
                        patch[at] = v;
                        patch[PatchSize * PatchSize + at] = v;
                        patch[2 * PatchSize * PatchSize + at] = v;
                    }
                }
            }
        }

        int real = rows * columns;
        mask[..real].Fill(1f);
        PositionWeights(rows, columns, positions);
    }

    /// <summary>
    /// numpy's median of the resized block's edge — top row, bottom row, left column, right column,
    /// corners counted twice — cut to a byte as the author's <c>astype(uint8)</c> does.
    /// </summary>
    internal static byte BorderMedian(byte[] small, int width, int height)
    {
        var edge = new byte[2 * width + 2 * height];
        int n = 0;
        for (int x = 0; x < width; x++) edge[n++] = small[x];
        for (int x = 0; x < width; x++) edge[n++] = small[(height - 1) * width + x];
        for (int y = 0; y < height; y++) edge[n++] = small[y * width];
        for (int y = 0; y < height; y++) edge[n++] = small[y * width + width - 1];
        Array.Sort(edge);
        // Always an even count: the mean of the two middle values, rounded down.
        return (byte)((edge[edge.Length / 2 - 1] + edge[edge.Length / 2]) / 2);
    }

    /// <summary>
    /// [196 × 196]: row r is patch r of a <paramref name="rows"/> × <paramref name="columns"/> grid as
    /// weights over the 14×14 table; rows past the grid stay zero.
    /// </summary>
    internal static void PositionWeights(int rows, int columns, Span<float> weights)
    {
        if (rows == TableSide && columns == TableSide)
        {
            // The author uses the table as it is.
            for (int i = 0; i < Patches; i++) weights[i * Patches + i] = 1f;
            return;
        }

        var down = CubicWeights(rows);
        var across = CubicWeights(columns);
        for (int i = 0; i < rows; i++)
        for (int j = 0; j < columns; j++)
        {
            var row = weights.Slice((i * columns + j) * Patches, Patches);
            for (int a = 0; a < TableSide; a++)
            for (int b = 0; b < TableSide; b++)
            {
                // numpy writes +0 where the product is −0: the same to the encoder, but it keeps the
                // input the author's bit for bit.
                double weight = down[i, a] * across[j, b];
                row[a * TableSide + b] = weight == 0 ? 0f : (float)weight;
            }
        }
    }

    /// <summary>
    /// torch's bicubic resize of 14 cells to <paramref name="size"/> along one axis as a matrix
    /// [size × 14]: <c>interpolate(mode="bicubic", align_corners=False)</c>, A = −0.75, edges clamped.
    /// </summary>
    internal static double[,] CubicWeights(int size)
    {
        const double A = -0.75;
        var weights = new double[size, TableSide];
        double scale = (double)TableSide / size;
        for (int i = 0; i < size; i++)
        {
            double x = scale * (i + 0.5) - 0.5;
            double floor = Math.Floor(x);
            double t = x - floor;
            Span<double> k =
            [
                ((A * (t + 1) - 5 * A) * (t + 1) + 8 * A) * (t + 1) - 4 * A,
                ((A + 2) * t - (A + 3)) * t * t + 1,
                ((A + 2) * (1 - t) - (A + 3)) * (1 - t) * (1 - t) + 1,
                ((A * (2 - t) - 5 * A) * (2 - t) + 8 * A) * (2 - t) - 4 * A,
            ];
            for (int tap = 0; tap < 4; tap++)
                weights[i, Math.Clamp((int)floor - 1 + tap, 0, TableSide - 1)] += k[tap];
        }

        return weights;
    }
}
