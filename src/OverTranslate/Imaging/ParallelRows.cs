namespace OverTranslate.Imaging;

/// <summary>
/// Splits a pass over rows across threads once the work is worth the hop, the way the library
/// this replaced splits its stripes. Every pass here writes each output row from inputs nobody
/// writes during the pass, so how the rows are split never changes a result.
/// </summary>
internal static class ParallelRows
{
    /// <summary>How many threads a pass may use. Tests set it to one to compare against a serial run.</summary>
    internal static int Threads = Environment.ProcessorCount;

    /// <summary>Elements of work below which a pass stays on the calling thread.</summary>
    private const long Threshold = 1 << 17;

    /// <summary>Each row on its own, in parallel when there is enough of them.</summary>
    public static void For(int rows, long workPerRow, Action<int> body)
    {
        int threads = Threads;
        if (rows < 2 || threads <= 1 || rows * workPerRow < Threshold)
        {
            for (int y = 0; y < rows; y++) body(y);
            return;
        }
        int blocks = Math.Min(rows, threads * 2);
        Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = threads }, block =>
        {
            int start = (int)((long)rows * block / blocks), end = (int)((long)rows * (block + 1) / blocks);
            for (int y = start; y < end; y++) body(y);
        });
    }

    /// <summary>Contiguous runs of rows, so a pass can carry state from one row to the next within a run.</summary>
    public static void Blocks(int rows, long workPerRow, Action<int, int> body)
    {
        int threads = Threads;
        if (rows < 4 || threads <= 1 || rows * workPerRow < Threshold)
        {
            body(0, rows);
            return;
        }
        int blocks = Math.Min(rows / 2, threads);
        Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = threads }, block =>
            body((int)((long)rows * block / blocks), (int)((long)rows * (block + 1) / blocks)));
    }
}
