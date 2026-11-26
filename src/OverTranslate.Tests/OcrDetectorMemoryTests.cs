using System.Diagnostics;
using System.Drawing;
using OverTranslate.Services.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

[CollectionDefinition(nameof(OcrDetectorMemoryCollection), DisableParallelization = true)]
public sealed class OcrDetectorMemoryCollection;

/// <summary>
/// #437, end to end: what reading a large screenshot leaves behind once the pass is over. Alone in
/// its collection, because it reads the memory of the whole test process.
/// </summary>
[Collection(nameof(OcrDetectorMemoryCollection))]
public class OcrDetectorMemoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LargeCapture_DoesNotLeaveItsDetectorBuffersBehind()
    {
        using var engine = new OnnxOcrEngine();
        using (var small = new Bitmap(320, 80)) await engine.RecognizeAsync(small, "EN");
        long before = PrivateBytes();

        // Blank on purpose: the detector's buffers follow the size of the image, not what is on it.
        using (var page = new Bitmap(1600, 1200))
            for (int i = 0; i < 3; i++)
                await engine.RecognizeAsync(page, "EN");

        long grown = PrivateBytes() - before;
        output.WriteLine($"private bytes grew {grown >> 20} MB");
        Assert.True(grown < 150L << 20, $"{grown >> 20} MB stayed behind after the passes ended");
    }

    private static long PrivateBytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var self = Process.GetCurrentProcess();
        return self.PrivateMemorySize64;
    }
}
