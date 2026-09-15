using System.Drawing;
using OverTranslate.Services;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class RealtimeCpuBackgroundTests
{
    [Fact]
    public void Repair_RemovesStrokesWithoutChangingSourceOrUntranslatedRegion()
    {
        // Odd width exercises 24-bit bitmap row padding and BGR channel order.
        using var frame = new Bitmap(213, 143);
        var background = Color.FromArgb(20, 40, 60);
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(background);
            graphics.FillRectangle(Brushes.White, 42, 35, 3, 18);
            graphics.FillRectangle(Brushes.White, 152, 95, 3, 18);
        }
        using var result = RealtimeCpuBackground.Repair(frame,
        [
            new TranslatedBlock("I", "字", new(38, 32, 14, 25)),
            new TranslatedBlock("I", " ", new(148, 92, 14, 25)),
        ]);
        Assert.Equal(frame.Size, result.Size);
        Assert.Equal(Color.White.ToArgb(), frame.GetPixel(43, 40).ToArgb());
        Assert.InRange(result.GetPixel(43, 40).R, 15, 30);
        Assert.InRange(result.GetPixel(43, 40).B, 55, 70);
        Assert.Equal(background.ToArgb(), result.GetPixel(212, 142).ToArgb());
        Assert.Equal(Color.White.ToArgb(), result.GetPixel(153, 100).ToArgb());
    }

    [Fact]
    public void Repair_EmptyTranslationReturnsIndependentUnchangedFrame()
    {
        using var frame = new Bitmap(7, 5);
        frame.SetPixel(6, 4, Color.Red);
        using var result = RealtimeCpuBackground.Repair(frame, []);
        Assert.NotSame(frame, result);
        Assert.Equal(frame.GetPixel(6, 4), result.GetPixel(6, 4));
    }

    [Fact]
    public void Repair_HonorsCancellationBeforeBitmapConversion()
    {
        using var frame = new Bitmap(7, 5);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RealtimeCpuBackground.Repair(frame, [], cancellation.Token));
    }
}
