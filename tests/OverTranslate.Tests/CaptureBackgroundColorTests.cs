using System.Drawing;
using OverTranslate.Services;
using Xunit;
using MediaColor = System.Windows.Media.Color;
using WpfRect = System.Windows.Rect;

namespace OverTranslate.Tests;

public class CaptureBackgroundColorTests
{
    private static readonly Color Panel = Color.FromArgb(255, 0x2A, 0x3C, 0x55);
    private static readonly Color Page = Color.FromArgb(255, 0xF2, 0xF2, 0xF2);

    /// <summary>A paragraph of three lines printed on a panel that does not cover the whole frame.</summary>
    private static Bitmap ParagraphOnPanel()
    {
        var frame = new Bitmap(400, 300);
        using var g = Graphics.FromImage(frame);
        g.Clear(Page);
        g.FillRectangle(new SolidBrush(Panel), 40, 40, 320, 120);
        for (int i = 0; i < 3; i++)
            g.FillRectangle(Brushes.White, 60, 55 + i * 40, 280, 20);
        return frame;
    }

    private static WpfRect[] ParagraphLines() =>
    [
        new(60, 55, 280, 20),
        new(60, 95, 280, 20),
        new(60, 135, 280, 20),
    ];

    [Fact]
    public void BetweenLines_ReadsThePanelTheParagraphIsPrintedOn()
    {
        using var frame = ParagraphOnPanel();

        var paper = CaptureBackgroundColor.BetweenLines(frame, ParagraphLines(), vertical: false);

        Assert.Equal(MediaColor.FromRgb(Panel.R, Panel.G, Panel.B), paper);
    }

    [Fact]
    public void BetweenLines_IsNullForASingleLineBecauseThereIsNoInteriorToRead()
    {
        using var frame = ParagraphOnPanel();

        Assert.Null(CaptureBackgroundColor.BetweenLines(frame, [ParagraphLines()[0]], vertical: false));
    }

    [Fact]
    public void BetweenLines_IsNullWhenTheGapsHoldNoSingleColour()
    {
        using var frame = new Bitmap(400, 300);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Page);
            for (int x = 0; x < 400; x += 6)
                g.FillRectangle(new SolidBrush(Color.FromArgb(255, x % 256, 40, 200 - x % 200)), x, 0, 6, 300);
            for (int i = 0; i < 3; i++)
                g.FillRectangle(Brushes.White, 60, 55 + i * 40, 280, 20);
        }

        Assert.Null(CaptureBackgroundColor.BetweenLines(frame, ParagraphLines(), vertical: false));
    }

    [Fact]
    public void BetweenLines_ReadsTheGapsBesideTheColumnsWhenTheTextIsVertical()
    {
        using var frame = new Bitmap(300, 400);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Page);
            g.FillRectangle(new SolidBrush(Panel), 40, 40, 120, 320);
            for (int i = 0; i < 3; i++)
                g.FillRectangle(Brushes.White, 55 + i * 40, 60, 20, 280);
        }

        var columns = new WpfRect[] { new(55, 60, 20, 280), new(95, 60, 20, 280), new(135, 60, 20, 280) };

        Assert.Equal(MediaColor.FromRgb(Panel.R, Panel.G, Panel.B),
            CaptureBackgroundColor.BetweenLines(frame, columns, vertical: true));
    }

    /// <summary>
    /// The defect this exists for: the ring is sized from the box, and around a whole paragraph
    /// that reaches off the panel and onto the page behind it.
    /// </summary>
    [Fact]
    public void ForCaptureOverlay_TakesThePanelRatherThanThePageBehindItForAParagraph()
    {
        using var frame = ParagraphOnPanel();
        var paragraph = new WpfRect(60, 55, 280, 100);

        var withoutLines = SourceTextColorSampler.ForCaptureOverlay(frame, paragraph);
        var withLines = SourceTextColorSampler.ForCaptureOverlay(frame, paragraph, ParagraphLines());

        Assert.Equal(MediaColor.FromRgb(Page.R, Page.G, Page.B), withoutLines.Background);
        Assert.Equal(MediaColor.FromRgb(Panel.R, Panel.G, Panel.B), withLines.Background);
    }

    [Fact]
    public void ForCaptureOverlay_LeavesASingleLineExactlyAsItWas()
    {
        using var frame = ParagraphOnPanel();
        var line = ParagraphLines()[0];

        Assert.Equal(
            SourceTextColorSampler.ForCaptureOverlay(frame, line),
            SourceTextColorSampler.ForCaptureOverlay(frame, line, [line]));
    }
}
