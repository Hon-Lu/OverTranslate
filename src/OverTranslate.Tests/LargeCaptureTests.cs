using System.Drawing;
using OverTranslate.Services;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

/// <summary>
/// The screenshot path over a capture big enough to be downscaled on the way to the detector, which
/// is the regime where its short axis is quantised and lines start coming back garbled.
/// </summary>
public class LargeCaptureTests(ITestOutputHelper output)
{
    [ScreenshotFact("subtitle-over-light-floor-1226x196.png")]
    public async Task ALineInACaptureLargeEnoughToBeDownscaledIsStillRead()
    {
        using var ocr = new OcrService();
        using var subtitle = ExternalScreenshot.Load("subtitle-over-light-floor-1226x196.png");
        using var capture = OnALargeCanvas(subtitle, 2400, 1200);

        var blocks = await ocr.RecognizeAsync(capture, "EN");
        var text = string.Join(" ", blocks.Select(b => b.Text));

        output.WriteLine($"{blocks.Count} block(s): {text}");
        Assert.Contains("okay", text, StringComparison.OrdinalIgnoreCase);
    }

    private static Bitmap OnALargeCanvas(Bitmap content, int width, int height)
    {
        var canvas = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(canvas);
        graphics.Clear(Color.FromArgb(30, 30, 34));
        graphics.DrawImageUnscaled(content, (width - content.Width) / 2, (height - content.Height) / 2);
        return canvas;
    }
}
