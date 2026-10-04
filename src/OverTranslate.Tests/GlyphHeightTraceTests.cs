using System.Globalization;
using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;
using Xunit.Abstractions;

namespace OverTranslate.Tests;

/// <summary>
/// What the glyph height estimate reports about its own path, and the shape of the discontinuity
/// that report was written to expose.
/// </summary>
/// <remarks>
/// Nothing here changes a threshold or a verdict. It exists because a report written backwards from
/// the final number got two of three diagnoses wrong on this branch: two lines of one paragraph
/// with detection boxes two pixels apart came back 57% apart in estimated glyph height, which reads
/// as a box problem and is not one.
/// </remarks>
public class GlyphHeightTraceTests(ITestOutputHelper output)
{
    // "cost." as the detector drew it on web-v3/3.png: five glyphs in a box 62 wide and 31 tall.
    private const string ShortLine = "cost.";
    private const double ShortLineBoxHeight = 31;

    [Fact]
    public void TheWidthConditionIsAStepAndTheLineItselfFallsOnTheRefusingSide()
    {
        var ladder = new List<(double Width, double Height)>();

        foreach (var width in Widths())
        {
            var box = new Rect(0, 0, width, ShortLineBoxHeight);
            var height = OnnxOcrEngine.LayoutGlyphHeightFor(OcrLayoutScript.Latin, box, ShortLine, out var trace);

            ladder.Add((width, height!.Value));
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"width={width:0.0000}  W-2H={trace.WidthMinusTwiceHeight:+0.0000;-0.0000;0.0000}  " +
                $"W>2H={trace.IsWideEnough,-5}  branch={trace.PitchBranchEntered,-5}  " +
                $"pitch={trace.PitchCandidate:0.0000}  glyph={height:0.0000}  src={trace.Source}"));
        }

        // Every width up to and including twice the height gives the same answer, because the pitch
        // is not consulted at all. The estimate does not vary with the width there, so it cannot be
        // said to be measuring the text. It used to be 0.82 of the box; the short-line correction
        // now takes over exactly where the clamp refuses, and it is half the box.
        var refused = ladder.Where(step => step.Width <= ShortLineBoxHeight * 2).ToList();
        Assert.Equal(ShortLineBoxHeight * 0.5, Assert.Single(refused.Select(step => step.Height).Distinct()));

        // One ten-thousandth of a pixel past it, the answer used to fall by more than a third: two
        // lines of one paragraph either side were compared on quantities that were not the same
        // measurement. Both sides now estimate the glyph itself, and agree to within a tenth.
        var lastRefused = refused[^1].Height;
        var firstAccepted = ladder.First(step => step.Width > ShortLineBoxHeight * 2).Height;
        Assert.True(
            Math.Abs(lastRefused - firstAccepted) < lastRefused / 10,
            $"expected no step at twice the box height, got {lastRefused:0.0000} -> {firstAccepted:0.0000}");

        static IEnumerable<double> Widths() =>
            [58, 59, 60, 61, 61.5, 61.9, 61.99, 62, 62.0001, 62.01, 62.1, 62.5, 63, 64, 65, 66];
    }

    [Fact]
    public void TheLineIsRefusedByTheWidthAndNotByTheGlyphCount()
    {
        var box = new Rect(145, 648, 62, ShortLineBoxHeight);
        OnnxOcrEngine.LayoutGlyphHeightFor(OcrLayoutScript.Latin, box, ShortLine, out var trace);

        // The two halves of the condition, separately, because they have different fixes and the
        // final number cannot tell them apart.
        Assert.True(trace.HasEnoughGlyphs);
        Assert.False(trace.IsWideEnough);
        Assert.Equal(0, trace.WidthMinusTwiceHeight);

        // Entering the branch and the min then choosing the pitch are two different events, and
        // this line is stopped at the first of them: the pitch it would have used is lower than the
        // box estimate. That estimate no longer stands — the short-line correction takes the
        // line wherever the clamp refuses it.
        Assert.False(trace.PitchBranchEntered);
        Assert.False(trace.PitchSelected);
        Assert.True(trace.PitchCandidate < trace.BoxEstimate);
        Assert.True(trace.ShortTextApplied);
        Assert.True(trace.ShortTextSelected);
        Assert.Equal(GlyphHeightSource.ShortText, trace.Source);
    }

    [Fact]
    public void TheTraceReportsTheValueThatWasActuallyReturned()
    {
        // A trace that can disagree with the call it describes is worse than none, so every row of
        // the estimate grid is asked both ways.
        foreach (var (text, box) in GlyphHeightEstimateGridTests.Grid)
        {
            var script = LayoutScriptDetection.For(text);
            var plain = OnnxOcrEngine.LayoutGlyphHeightFor(script, box, text);
            var traced = OnnxOcrEngine.LayoutGlyphHeightFor(script, box, text, out var trace);

            Assert.Equal(plain, traced);
            Assert.Equal(plain, trace.Result);
            Assert.Equal(script, trace.Script);
        }
    }

    [Fact]
    public void AScriptWithNoEstimateStillReportsItsInputs()
    {
        var box = new Rect(0, 0, 300, 30);
        var height = OnnxOcrEngine.LayoutGlyphHeightFor(OcrLayoutScript.Mixed, box, "BanG Dream! アニメ", out var trace);

        Assert.Null(height);
        Assert.Null(trace.Result);
        Assert.Equal(GlyphHeightSource.None, trace.Source);
        Assert.Equal(OcrLayoutScript.Mixed, trace.Script);
        Assert.Equal(13, trace.GlyphCount);
    }
}

/// <summary>
/// Which quantity the size test compared, and which of the two reasons put it on the detection
/// boxes — the distinction one round of this branch got wrong.
/// </summary>
public class TextSizeBasisTests
{
    [Fact]
    public void TwoLinesOfOneScriptAreComparedOnTheirEstimates()
    {
        var previous = Line("BanG Dream! English Site", new Rect(30, 72, 326, 30));
        var current = Line("some more english text here", new Rect(31, 108, 803, 27));

        OcrTextBlockGrouper.TextSizeRatio(previous, current, out var basis, out var previousValue, out var currentValue);

        Assert.Equal(TextSizeBasis.Glyph, basis);
        Assert.Equal(previous.LayoutGlyphHeight, previousValue);
        Assert.Equal(current.LayoutGlyphHeight, currentValue);
    }

    [Fact]
    public void DifferingScriptsFallBackToTheBoxesEvenWithBothEstimatesInHand()
    {
        var previous = Line("BanG Dream! English Site", new Rect(30, 72, 326, 30));
        var current = Line("アニメやゲームなどを展開する次世代プロジェクト", new Rect(31, 108, 803, 27));

        // Both sides carry an estimate here — the CJK one included — and the pair still goes to the
        // boxes, because matching scripts is the first thing the test asks about. Supplying an
        // estimate for the side that lacks one would therefore change nothing about a pair like
        // web-v3/1.png's, whose two lines are Latin and Mixed.
        Assert.NotNull(previous.LayoutGlyphHeight);
        Assert.NotNull(current.LayoutGlyphHeight);

        OcrTextBlockGrouper.TextSizeRatio(previous, current, out var basis, out var previousValue, out var currentValue);

        Assert.Equal(TextSizeBasis.BoxDifferentScript, basis);
        Assert.Equal(previous.LayoutBounds.Height, previousValue);
        Assert.Equal(current.LayoutBounds.Height, currentValue);
    }

    [Fact]
    public void OneScriptWithAMissingEstimateIsTheOtherReasonEntirely()
    {
        var previous = Line("BanG Dream! English Site", new Rect(30, 72, 326, 30));
        var current = Line("english again", new Rect(31, 108, 803, 27)) with { LayoutGlyphHeight = null };

        OcrTextBlockGrouper.TextSizeRatio(current, previous, out var basis, out _, out _);

        Assert.Equal(TextSizeBasis.BoxNoGlyphHeight, basis);
    }

    private static OcrTextBlock Line(string text, Rect box) => new OcrTextBlock(text, box).AsDetected();
}
