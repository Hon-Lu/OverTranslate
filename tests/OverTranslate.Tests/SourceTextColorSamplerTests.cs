using System.Drawing;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

public class SourceTextColorSamplerTests
{
    [Fact]
    public void Sample_ReadsTheRingAroundTheBoxAsBackgroundAndTheMajorityGlyphColourInside()
    {
        using var frame = new Bitmap(80, 40);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(255, 24, 28, 34));
            g.FillRectangle(Brushes.White, 20, 12, 30, 12);
            using var highlight = new SolidBrush(Color.FromArgb(255, 255, 220, 0));
            g.FillRectangle(highlight, 50, 12, 10, 12);
        }

        var sample = SourceTextColorSampler.Sample(frame, new System.Windows.Rect(20, 12, 40, 12));

        Assert.NotNull(sample);
        Assert.Equal(System.Windows.Media.Color.FromRgb(24, 28, 34), sample!.Value.Background);
        Assert.Equal(System.Windows.Media.Color.FromRgb(255, 255, 255), sample.Value.Text);
    }

    [Fact]
    public void Sample_HasNoTextColourWhenNothingInsideStandsOutFromTheBackground()
    {
        using var frame = new Bitmap(80, 40);
        using (var g = Graphics.FromImage(frame))
            g.Clear(Color.FromArgb(255, 120, 120, 120));

        var sample = SourceTextColorSampler.Sample(frame, new System.Windows.Rect(20, 12, 40, 12));

        Assert.NotNull(sample);
        Assert.Null(sample!.Value.Text);
    }

    [Fact]
    public void Sample_IsNullForABoxOutsideTheFrame()
    {
        using var frame = new Bitmap(80, 40);

        Assert.Null(SourceTextColorSampler.Sample(frame, new System.Windows.Rect(100, 50, 20, 10)));
    }

    [Fact]
    public void ForCaptureOverlay_KeepsWhiteCaptionOnAPaleCardLegible()
    {
        using var frame = new Bitmap(120, 60);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.FromArgb(255, 0xC8, 0xC8, 0xC8));
            g.FillRectangle(Brushes.White, 30, 20, 60, 16);
        }

        var (background, text) = SourceTextColorSampler.ForCaptureOverlay(
            frame, new System.Windows.Rect(30, 20, 60, 16));

        Assert.Equal(System.Windows.Media.Color.FromRgb(0xC8, 0xC8, 0xC8), background);
        Assert.True(OverlayTextColor.ContrastRatio(text, background) >= OverlayTextColor.MinimumContrast);
    }

    [Fact]
    public void ForCaptureOverlay_FallsBackToBlackOnWhiteWhenTheBoxIsOutsideTheFrame()
    {
        using var frame = new Bitmap(80, 40);

        var (background, text) = SourceTextColorSampler.ForCaptureOverlay(
            frame, new System.Windows.Rect(100, 50, 20, 10));

        Assert.Equal(System.Windows.Media.Colors.White, background);
        Assert.Equal(System.Windows.Media.Color.FromRgb(0, 0, 0), text);
    }
}
