using System.Windows;
using System.Windows.Media;
using OverTranslate.Layout;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// design.md §8.6 — a colour may be re-used only where the block at that index is the same block.
/// </summary>
public class SampledColorReuseTests
{
    private static TranslatedBlock Coloured(string text, Rect bounds) =>
        new(text, "譯文", bounds, BackgroundColor: Colors.White, TextColor: Colors.Black);

    private static readonly Rect Box = new(10, 20, 100, 30);

    [Fact]
    public void TheSameBlockInTheSamePlace_KeepsItsColours()
    {
        List<TranslatedBlock> previous = [Coloured("HELLO", Box)];

        Assert.True(SampledColorReuse.CanReuse(
            previous, 0, Coloured("HELLO", Box), verticalText: false, previousVerticalText: false));
    }

    /// <summary>
    /// The detector does not hand back identical rectangles for identical text, and re-sampling
    /// every bubble over a pixel of jitter would throw the re-use away in the case it exists for.
    /// </summary>
    [Fact]
    public void ABoxThatMovedByADetectorsWorthOfJitter_StillCounts()
    {
        List<TranslatedBlock> previous = [Coloured("HELLO", Box)];
        var jittered = Coloured("HELLO", new Rect(11, 21, 101, 29));

        Assert.True(SampledColorReuse.CanReuse(
            previous, 0, jittered, verticalText: false, previousVerticalText: false));
    }

    /// <summary>
    /// The quiet failure §8.6 exists for: same count, same index, different block. Comparing the
    /// mode or the number of blocks would let this through.
    /// </summary>
    [Fact]
    public void ADifferentReadingAtTheSameIndex_IsResampled()
    {
        List<TranslatedBlock> previous = [Coloured("HELLO", Box)];

        Assert.False(SampledColorReuse.CanReuse(
            previous, 0, Coloured("GOODBYE", Box), verticalText: false, previousVerticalText: false));
    }

    [Fact]
    public void ABoxThatMovedFurtherThanJitter_IsResampled()
    {
        List<TranslatedBlock> previous = [Coloured("HELLO", Box)];
        var moved = Coloured("HELLO", new Rect(10, 90, 100, 30));

        Assert.False(SampledColorReuse.CanReuse(
            previous, 0, moved, verticalText: false, previousVerticalText: false));
    }

    [Fact]
    public void SwitchingOrientation_ResamplesEvenWhereTheBlockLooksTheSame()
    {
        List<TranslatedBlock> previous = [Coloured("HELLO", Box)];

        Assert.False(SampledColorReuse.CanReuse(
            previous, 0, Coloured("HELLO", Box), verticalText: true, previousVerticalText: false));
    }

    /// <summary>
    /// The previous list is shorter whenever the last attempt failed before it had colours.
    /// </summary>
    [Fact]
    public void AnIndexPastTheEndOfTheLastResult_IsResampled()
    {
        List<TranslatedBlock> previous = [Coloured("HELLO", Box)];

        Assert.False(SampledColorReuse.CanReuse(
            previous, 1, Coloured("HELLO", Box), verticalText: false, previousVerticalText: false));
    }
}
