using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Rejecting a real line here means a subtitle never appears at all, which is exactly what the
/// earlier learned-height version of this did over a game with a chat log in it.
/// </summary>
public class CollapsedDetectionTests
{
    // The measured block: 196 tall, real lines 88-117, collapses at 194, 253, 303 and 343.
    private const double Block = 196;

    [Theory]
    [InlineData(194)]
    [InlineData(253)]
    [InlineData(303)]
    [InlineData(343)]
    public void ABoxThrownAcrossTheWholeBlockIsACollapse(double height)
    {
        // "A" and "M" are what the recogniser actually returned out of those boxes.
        Assert.True(CollapsedDetection.IsCollapsed(height, Block, "A"));
    }

    [Theory]
    [InlineData(88)]
    [InlineData(95)]
    [InlineData(117)]
    [InlineData(150)]
    public void RealLinesLeaveRoomAboveAndBelowThem(double height)
    {
        Assert.False(CollapsedDetection.IsCollapsed(height, Block, "It's so relaxing."));
    }

    [Fact]
    public void ALooseBoxAroundARealSentenceIsNotACollapse()
    {
        // Measured under PP-OCRv6_det_tiny: a 171px box holding "Let's pay CiRcLE visit on the way
        // home." in a 190px block, thrown away by the old 0.9 threshold. The new detector draws
        // looser boxes than the one this rule was calibrated on, and a sentence lost this way never
        // reaches the screen at all.
        Assert.False(
            CollapsedDetection.IsCollapsed(171, 190, "Let's pay CiRcLE visit on the way home."));
    }

    [Fact]
    public void ABoxOverrunningTheBlockIsStillACollapse()
    {
        // From the same session: 214px in a 191px block, out of which the recogniser read "Yay!".
        Assert.True(CollapsedDetection.IsCollapsed(214, 191, "Yay!"));
    }

    [Fact]
    public void ASentenceFillingTheBlockTheUserDrewSurvives()
    {
        // The case issue #35 opens with: a block drawn tight around one line, so the line is 100%
        // of it. Under the height test alone this was a collapse, and 22 of 45 measured frames read
        // as nothing because of it. What the box holds is a complete sentence, and that is the half
        // of the question the block's height cannot answer.
        Assert.False(
            CollapsedDetection.IsCollapsed(190, 190, "The news did say they were building"));
    }

    [Fact]
    public void ABlockSpanningBoxThatReadNothingIsStillACollapse()
    {
        // The same event arriving without any text to judge: the box is the evidence, and there is
        // nothing here worth putting on screen either way.
        Assert.True(CollapsedDetection.IsCollapsed(200, 196, null));
        Assert.True(CollapsedDetection.IsCollapsed(200, 196, "   "));
    }

    [Theory]
    // The case that broke the version this replaced: one block holding a game's 13px chat log and
    // its 55-78px subtitles. Every size in it has to survive, because the block is what decides now
    // and the block has not changed.
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(55)]
    [InlineData(78)]
    public void TextOfEverySizeInAMixedBlockSurvives(double height)
    {
        Assert.False(CollapsedDetection.IsCollapsed(height, 400, "M"));
    }

    [Fact]
    public void ABlockOfNoHeightIsNotJudged()
    {
        Assert.False(CollapsedDetection.IsCollapsed(200, 0, "A"));
    }
}
