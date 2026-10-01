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
            .SelectMany(b => (b.SourceLineBounds is { Count: > 0 } lines ? lines : [b.Bounds])
                .Select(r => new CpuTextRegion(r.X, r.Y, r.Width, r.Height, b.RenderGlyphHeight))).ToArray();
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
