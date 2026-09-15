using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;

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
        using var source = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        Transfer(bitmap, source, toMat: true);
        using var mask = CpuTextMask.Build(source, regions);
        using var repaired = CpuHoleRepair.Repair(source, mask.Combined, token);
        Transfer(bitmap, repaired.Image, toMat: false);
        token.ThrowIfCancellationRequested();
        return (Bitmap)bitmap.Clone();
    }

    private static void Transfer(Bitmap bitmap, Mat mat, bool toMat)
    {
        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(bounds, toMat ? ImageLockMode.ReadOnly : ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[bitmap.Width * 3];
            for (int y = 0; y < bounds.Height; y++)
            {
                var pixels = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(toMat ? pixels : mat.Ptr(y), row, 0, row.Length);
                Marshal.Copy(row, 0, toMat ? mat.Ptr(y) : pixels, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
    }
}
