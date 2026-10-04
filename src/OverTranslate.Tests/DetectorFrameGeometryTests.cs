using System;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The invariant #69 spent three rounds failing to name: the detector must see the aspect ratio it
/// was handed, whatever pixel size the user's box happens to be.
/// </summary>
/// <remarks>
/// It used not to. RapidOcrNet's ScaleParam quantises each axis on its own with (n / 32 - 1) * 32,
/// and the old AlignForDetector-only approach avoided that solely when the library's scale came out
/// exactly 1.0 — which it never did once alignment had grown the bitmap past the ImgResize computed
/// from the size before alignment. A 465x76 dialogue box reached the detector 0.89x across and 0.67x
/// down, and the leading "「うん、" was never detected.
///
/// These run no inference: geometry is decided before the model is loaded, so it can be asserted
/// without one, and a test that needed a model would not run in CI.
/// </remarks>
public class DetectorFrameGeometryTests
{
    // Real captures and the awkward sizes around them: the two ja-game dialogue boxes that opened
    // this, the subtitle strips the corpora are made of, and sizes either side of a stride boundary.
    [Theory]
    [InlineData(465, 76)]
    [InlineData(450, 150)]
    [InlineData(1150, 240)]
    [InlineData(1825, 223)]
    [InlineData(1824, 129)]
    [InlineData(264, 56)]
    [InlineData(270, 60)]
    [InlineData(2473, 892)]
    [InlineData(3840, 2160)]
    public void TheDetectorSeesTheAspectRatioItWasHanded(int width, int height)
    {
        using var frame = FrameFor(width, height, RealtimeBlockMode.Subtitle);

        // One rounded pixel on each axis is all the isotropic resize can introduce, and on the
        // narrowest dimension here that is under half a percent.
        Assert.InRange(frame.RatioX / frame.RatioY, 0.995, 1.005);
    }

    [Theory]
    [InlineData(465, 76)]
    [InlineData(1150, 240)]
    [InlineData(1825, 223)]
    [InlineData(2473, 892)]
    public void TheLibraryIsGivenNothingLeftToResize(int width, int height)
    {
        using var frame = FrameFor(width, height, RealtimeBlockMode.Subtitle);

        // Two conditions together are what skips the quantisation: the resize target cannot bind
        // (so the ratio is exactly 1), and both padded dimensions are already on the stride (so the
        // per-axis rounding has nothing to round). Either one alone leaves it running.
        var padded = (
            Width: frame.Bitmap.Width + 2 * frame.Options.Padding,
            Height: frame.Bitmap.Height + 2 * frame.Options.Padding);

        Assert.True(frame.Options.ImgResize >= Math.Max(padded.Width, padded.Height));
        Assert.Equal(0, padded.Width % 32);
        Assert.Equal(0, padded.Height % 32);
    }

    [Theory]
    [InlineData(1150, 240, RealtimeBlockMode.Subtitle)]
    [InlineData(1825, 223, RealtimeBlockMode.Subtitle)]
    [InlineData(2473, 892, RealtimeBlockMode.Panel)]
    public void TheDetectorSizeAskedForIsTheOneItGets(int width, int height, RealtimeBlockMode mode)
    {
        var requested = RealtimeDetectorSize.For(width, height, mode).Primary;
        using var frame = FrameFor(width, height, mode);

        // Alignment can add up to a stride on top, and nothing more: the request used to be rounded
        // DOWN twice instead, by a different amount on each axis.
        var longest = Math.Max(frame.Bitmap.Width, frame.Bitmap.Height);
        Assert.InRange(longest, requested, requested + 31);
    }

    [Fact]
    public void ASmallRegionIsNotResizedAtAll()
    {
        // Below RealtimeDetectorSize.DownscaleMinSide the region is read at native size, so the only
        // thing the frame may do is align it — the case the 465x76 dialogue box falls into.
        using var frame = FrameFor(465, 76, RealtimeBlockMode.Subtitle);

        Assert.Equal(1.0, frame.RatioX);
        Assert.Equal(1.0, frame.RatioY);
    }

    private static OnnxOcrEngine.DetectorFrame FrameFor(int width, int height, RealtimeBlockMode mode)
    {
        using var source = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        return OnnxOcrEngine.CreateDetectorFrame(
            source, RealtimeDetectorSize.For(width, height, mode).Primary);
    }
}
