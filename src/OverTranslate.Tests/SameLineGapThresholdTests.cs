using System.Collections.Generic;
using System.Linq;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

public class SameLineGapThresholdTests
{
    /// <summary>
    /// The case it exists for: a capture holding both kinds of space. The line is drawn between
    /// them rather than at the fixed number, which would be right for neither if this capture's
    /// spacing happened to sit high or low.
    /// </summary>
    [Fact]
    public void DrawsTheLineBetweenTwoKindsOfSpace()
    {
        double[] gaps = [0.10, 0.14, 0.18, 0.22, 0.60, 0.64, 0.68];

        var threshold = SameLineGapThreshold.Estimate(gaps);

        Assert.True(threshold.Adaptive, threshold.Reason);
        Assert.Equal(0.41, threshold.Value, 3);
    }

    /// <summary>
    /// The range is bounded above as well, and this is the trade it makes. A capture whose halves
    /// split cleanly at 0.89 may really be one whose words are set that far apart — or it may be
    /// two columns of a table, which is the same shape and the expensive mistake. Nothing measured
    /// puts word spacing anywhere near here, so the fixed threshold wins and the cost is a wide
    /// line left in pieces rather than two columns read as a sentence.
    /// </summary>
    [Fact]
    public void FallsBackRatherThanOpenTheGateWide()
    {
        double[] gaps = [0.40, 0.44, 0.46, 0.48, 1.30, 1.42, 1.55];

        var threshold = SameLineGapThreshold.Estimate(gaps);

        Assert.False(threshold.Adaptive);
        Assert.Equal(SameLineGapThreshold.Fallback, threshold.Value);
    }

    /// <summary>
    /// One kind of space, unevenly measured, is not two kinds. Prose alone must not produce a split
    /// somewhere in the middle of its own word gaps.
    /// </summary>
    [Fact]
    public void RefusesToSplitOnePopulation()
    {
        double[] gaps = [0.10, 0.13, 0.16, 0.19, 0.22, 0.26, 0.30];

        var threshold = SameLineGapThreshold.Estimate(gaps);

        Assert.False(threshold.Adaptive);
        Assert.Equal(SameLineGapThreshold.Fallback, threshold.Value);
    }

    /// <summary>
    /// And a menu alone, which is the same argument from the other end: every gap here is an item
    /// boundary, so the answer is to split all of them, not to pick one as a word gap.
    /// </summary>
    [Fact]
    public void RefusesToSplitAMenuIntoWordsAndItems()
    {
        double[] gaps = [0.56, 0.63, 0.67, 0.70, 0.71, 0.72, 0.76];

        var threshold = SameLineGapThreshold.Estimate(gaps);

        Assert.False(threshold.Adaptive);
        Assert.Equal(SameLineGapThreshold.Fallback, threshold.Value);
    }

    /// <summary>
    /// Two boxes on a row say nothing about which kind of space sits between them, and neither do
    /// five. The fixed threshold is the answer until a capture has enough to argue otherwise.
    /// </summary>
    [Fact]
    public void FallsBackWhenThereIsTooLittleToGoOn()
    {
        var threshold = SameLineGapThreshold.Estimate([0.12, 0.70]);

        Assert.False(threshold.Adaptive);
        Assert.Equal(SameLineGapThreshold.Fallback, threshold.Value);
    }

    [Fact]
    public void FallsBackOnACaptureWithNoGapsAtAll()
    {
        var threshold = SameLineGapThreshold.Estimate([]);

        Assert.False(threshold.Adaptive);
        Assert.Equal(SameLineGapThreshold.Fallback, threshold.Value);
    }

    /// <summary>
    /// A split can be clean and still be the wrong two things — text against the distance across a
    /// column, say. An answer no real word spacing occupies is evidence of that, not a threshold.
    /// </summary>
    [Fact]
    public void FallsBackWhenTheAnswerIsOutsideAnyRealSpacing()
    {
        double[] gaps = [0.02, 0.03, 0.04, 0.05, 0.06, 0.07, 0.90];

        var threshold = SameLineGapThreshold.Estimate(gaps);

        Assert.False(threshold.Adaptive);
        Assert.Equal(SameLineGapThreshold.Fallback, threshold.Value);
    }

    /// <summary>
    /// Distances across a layout are not spacing decisions and are left out, so a handful of them
    /// cannot drag the loose half far enough to move the line between the two.
    /// </summary>
    [Fact]
    public void IgnoresDistancesThatAreNotSpacing()
    {
        double[] near = [0.10, 0.14, 0.18, 0.22, 0.60, 0.64, 0.68];
        double[] withFarApart = [.. near, 8.0, 12.0, 20.0];

        Assert.Equal(
            SameLineGapThreshold.Estimate(near).Value,
            SameLineGapThreshold.Estimate(withFarApart).Value,
            3);
    }

    /// <summary>
    /// Same gaps, same answer, whatever order they arrive in — the split is searched exhaustively
    /// rather than iterated from a seed, so there is nothing for the input order to influence.
    /// </summary>
    [Fact]
    public void GivesTheSameAnswerWhateverOrderTheGapsArriveIn()
    {
        double[] gaps = [0.10, 0.14, 0.18, 0.22, 0.60, 0.64, 0.68];

        Assert.Equal(
            SameLineGapThreshold.Estimate(gaps).Value,
            SameLineGapThreshold.Estimate([.. gaps.Reverse()]).Value,
            6);
    }
}
