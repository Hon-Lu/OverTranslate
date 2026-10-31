using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Views.Capture;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The wheel sizing the tool in hand (#373): how far a notch goes, that it stops at the ends, and that
/// the slider lands where the new width is.
/// </summary>
public class AnnotationWheelSizeTests
{
    public static TheoryData<AnnotationTool> SizedTools => new()
    {
        AnnotationTool.Pen, AnnotationTool.Highlighter, AnnotationTool.Eraser,
        AnnotationTool.Line, AnnotationTool.Arrow, AnnotationTool.Rectangle, AnnotationTool.Ellipse,
    };

    [Theory]
    [MemberData(nameof(SizedTools))]
    public void Notches_WalkTheWholeRange_AndStopAtTheEnds(AnnotationTool tool)
    {
        double thin = AnnotationPanelWindow.NudgedThickness(tool, 1000, -1000);
        double thick = AnnotationPanelWindow.NudgedThickness(tool, 0, 1000);

        double width = thin;
        int notches = 0;
        while (width < thick && notches < 100)
        {
            double next = AnnotationPanelWindow.NudgedThickness(tool, width, 1);
            Assert.True(next > width);
            width = next;
            notches++;
        }

        Assert.Equal(thick, width);
        Assert.InRange(notches, 8, 14);
        Assert.Equal(thick, AnnotationPanelWindow.NudgedThickness(tool, thick, 3));
        Assert.Equal(thin, AnnotationPanelWindow.NudgedThickness(tool, thin, -3));
    }

    [Fact]
    public void PenNotch_IsOnePixel()
    {
        Assert.Equal(7, AnnotationPanelWindow.NudgedThickness(AnnotationTool.Pen, 6, 1));
        Assert.Equal(4, AnnotationPanelWindow.NudgedThickness(AnnotationTool.Pen, 6, -2));
    }

    [Theory]
    [MemberData(nameof(SizedTools))]
    public void SliderPosition_GivesBackTheWidth(AnnotationTool tool)
    {
        for (int notches = 0; notches <= 12; notches++)
        {
            double width = AnnotationPanelWindow.NudgedThickness(tool, 0, notches);
            double fraction = AnnotationPanelWindow.FractionFor(tool, width);

            Assert.InRange(fraction, 0, 1);
            Assert.Equal(width, AnnotationPanelWindow.ThicknessAt(tool, fraction), 6);
        }
    }

    [Theory]
    [InlineData(24, 1, 26)]
    [InlineData(24, -1, 22)]
    [InlineData(12, 1, 13)]
    [InlineData(128, 1, 141)]
    [InlineData(9, -5, 8)]
    [InlineData(495, 3, 500)]
    public void TextNotch_IsATenthOfTheSize(int size, int notches, int expected) =>
        Assert.Equal(expected, AnnotationPanelWindow.NudgedTextSize(size, notches));

    [Fact]
    public void TouchpadPieces_AddUpToWholeNotches()
    {
        int remainder = 0;
        int notches = 0;
        for (int i = 0; i < 12; i++) notches += MouseWheelHook.Accumulate(ref remainder, 30);

        Assert.Equal(3, notches);
        Assert.Equal(0, remainder);

        Assert.Equal(-1, MouseWheelHook.Accumulate(ref remainder, -150));
        Assert.Equal(-30, remainder);
    }
}
