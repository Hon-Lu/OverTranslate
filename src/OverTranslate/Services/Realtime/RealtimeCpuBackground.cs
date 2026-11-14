using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OverTranslate.Imaging;

namespace OverTranslate.Services.Realtime;

/// <summary>Repairs one captured frame, shared by every overlay patch in that refresh only.</summary>
internal static class RealtimeCpuBackground
{
    public static Bitmap Repair(Bitmap frame, IReadOnlyList<TranslatedBlock> blocks, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var regions = blocks.Where(b => !string.IsNullOrWhiteSpace(b.TranslatedText))
            .SelectMany(Regions).ToArray();
        if (regions.Length == 0) return (Bitmap)frame.Clone();
        using var bitmap = frame.Clone(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format24bppRgb);
        using var source = ImageBuffer.Uninitialized(frame.Width, frame.Height, PixelType.U8C3);
        Transfer(bitmap, source, toImage: true);
        using var mask = CpuTextMask.Build(source, regions);
        using var repaired = CpuHoleRepair.Repair(source, mask, token);
        Transfer(bitmap, repaired.Image, toImage: false);
        token.ThrowIfCancellationRequested();
        return (Bitmap)bitmap.Clone();
    }

    /// <summary>
    /// How far past its boxes the erase would take each block's ink, in <paramref name="frame"/>'s
    /// pixels — see <see cref="CpuTextMask.Overhang"/>: one entry per block, null for a block with
    /// nothing followed past its boxes, a tilted one, or one with no text.
    /// </summary>
    /// <remarks>
    /// <para>All of a window's blocks in one pass, as the erase takes them: the frame is converted
    /// once, and each line's room to its neighbours is measured against every other line the window
    /// draws, not only its own block's.</para>
    ///
    /// <para>Only the part of the frame the lines and their reach take up is converted. Converting it
    /// is not what this costs — a 1337 by 168 crop is half a millisecond to lock, copy and turn
    /// grey, against five to thirteen for the mask over it — the hats are; but there is no reason to convert picture no line is near.</para>
    /// </remarks>
    public static Rectangle?[] Overhang(Bitmap frame, IReadOnlyList<TranslatedBlock> blocks)
    {
        var reach = new Rectangle?[blocks.Count];
        var lines = new List<(int Block, System.Windows.Rect Row)>();
        for (int index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            if (block.Tilt is not null || string.IsNullOrWhiteSpace(block.TranslatedText)) continue;
            foreach (var row in block.SourceLineBounds is { Count: > 0 } rows ? rows : [block.Bounds])
                if (!row.IsEmpty && double.IsFinite(row.X + row.Y + row.Width + row.Height))
                    lines.Add((index, row));
        }
        if (lines.Count == 0) return reach;

        var union = lines.Select(line => line.Row).Aggregate(System.Windows.Rect.Union);
        int margin = CpuTextMask.FurthestReach + 2;
        var crop = Rectangle.Intersect(new Rectangle(0, 0, frame.Width, frame.Height), Rectangle.FromLTRB(
            (int)Math.Floor(union.Left) - margin, (int)Math.Floor(union.Top) - margin,
            (int)Math.Ceiling(union.Right) + margin, (int)Math.Ceiling(union.Bottom) + margin));
        if (crop.Width <= 0 || crop.Height <= 0) return reach;

        using var source = ImageBuffer.Uninitialized(crop.Width, crop.Height, PixelType.U8C3);
        Transfer(frame, crop, source);
        var regions = lines.Select(line => new CpuTextRegion(line.Row.X - crop.X, line.Row.Y - crop.Y,
            line.Row.Width, line.Row.Height, blocks[line.Block].RenderGlyphHeight)).ToArray();
        var followed = CpuTextMask.Overhang(source, regions);
        for (int index = 0; index < lines.Count; index++)
        {
            if (followed[index] is not { } line) continue;
            var placed = new Rectangle(line.X + crop.X, line.Y + crop.Y, line.Width, line.Height);
            int block = lines[index].Block;
            reach[block] = reach[block] is { } soFar ? Rectangle.Union(soFar, placed) : placed;
        }
        return reach;
    }

    // A group off a tilted card is erased inside its lines' own quadrilaterals only: its upright
    // boxes reach off the card and into the comments either side — see Ocr.TiltedText.
    private static IEnumerable<CpuTextRegion> Regions(TranslatedBlock block)
    {
        var within = block.Tilt?.EraseQuads;
        return (block.SourceLineBounds is { Count: > 0 } lines ? lines : [block.Bounds])
            .Select(r => new CpuTextRegion(r.X, r.Y, r.Width, r.Height, block.RenderGlyphHeight, within));
    }

    private static unsafe void Transfer(Bitmap frame, Rectangle crop, ImageBuffer image)
    {
        var data = frame.LockBits(crop, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int bytes = crop.Width * 3;
            for (int y = 0; y < crop.Height; y++)
                Buffer.MemoryCopy((byte*)IntPtr.Add(data.Scan0, y * data.Stride), image.Row(y), bytes, bytes);
        }
        finally { frame.UnlockBits(data); }
    }

    private static unsafe void Transfer(Bitmap bitmap, ImageBuffer image, bool toImage)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, toImage ? ImageLockMode.ReadOnly : ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            int bytes = bitmap.Width * 3;
            for (int y = 0; y < bounds.Height; y++)
            {
                byte* pixels = (byte*)IntPtr.Add(data.Scan0, y * data.Stride);
                if (toImage) Buffer.MemoryCopy(pixels, image.Row(y), bytes, bytes);
                else Buffer.MemoryCopy(image.Row(y), pixels, bytes, bytes);
            }
        }
        finally { bitmap.UnlockBits(data); }
    }
}
