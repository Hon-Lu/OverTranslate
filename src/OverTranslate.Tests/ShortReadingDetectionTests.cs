using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Rejecting a real line here costs a subtitle; letting a false one through puts a single glyph on
/// screen where a correct translation was, so both directions are held down by measured readings.
/// </summary>
public class ShortReadingDetectionTests
{
    [Theory]
    // The three false readings from the 2026-08-09 session, each off a frame with no subtitle on it
    // at all, and the single characters the collapse work recorded before that.
    [InlineData("A")]
    [InlineData("M")]
    [InlineData("?")]
    [InlineData("2")]
    public void OneCharacterIsSceneryRatherThanText(string text)
    {
        Assert.True(ShortReadingDetection.IsTooShort(text));
    }

    [Theory]
    // The two real rescues from that same session, and the shortest subtitles in the fixtures.
    [InlineData("It's a bit too vague.")]
    [InlineData("It might just...")]
    [InlineData("Yes.")]
    [InlineData("Ok")]
    public void RealLinesAreKept(string text)
    {
        Assert.False(ShortReadingDetection.IsTooShort(text));
    }

    [Theory]
    // A box holding nothing but space is the same nothing as an empty one; padding must not buy a
    // reading its way past the floor.
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  A  ")]
    [InlineData("\t")]
    [InlineData(null)]
    public void WhitespaceDoesNotCountTowardsLength(string? text)
    {
        Assert.True(ShortReadingDetection.IsTooShort(text));
    }

    [Fact]
    public void TwoCharactersIsTheFloorAndSurvives()
    {
        // CJK earns its place here: two characters is a whole sentence in the languages this reads,
        // so the floor cannot be raised without cost even though every measured false reading was
        // one character.
        Assert.False(ShortReadingDetection.IsTooShort("はい"));
        Assert.False(ShortReadingDetection.IsTooShort("好的"));
    }

    [Theory]
    // Scenery read as text, with the scores PP-OCRv6 actually gave them over one subtitle session.
    // "DM" is the one the user saw on screen; it slipped past every test of the box's shape.
    [InlineData("DM", 0.71)]
    [InlineData("'N", 0.64)]
    [InlineData("605G0", 0.60)]
    [InlineData("NA", 0.61)]
    [InlineData("{02", 0.79)]
    [InlineData("米", 0.79)]
    public void ShortAndUnsureIsScenery(string text, double confidence)
    {
        Assert.True(ShortReadingDetection.IsUnconvincingShortText(text, confidence));
    }

    [Theory]
    // Real short subtitles from that same session. These are why the floor is 0.80 and not higher.
    [InlineData("Yay!", 1.00)]
    [InlineData("What?!", 1.00)]
    [InlineData("Why me?", 0.97)]
    [InlineData("0-0h, no!", 0.98)]
    [InlineData("月島まりな", 1.00)]
    public void ShortAndConfidentIsKept(string text, double confidence)
    {
        Assert.False(ShortReadingDetection.IsUnconvincingShortText(text, confidence));
    }

    [Fact]
    public void LongReadingsAreNeverJudgedOnConfidence()
    {
        // The same session had real lines down at 0.68. Length is what separates them from the
        // scenery above, so the confidence floor must not reach them.
        Assert.False(ShortReadingDetection.IsUnconvincingShortText(
            "You seem rather dispirited, Minato-san.", 0.68));
        Assert.False(ShortReadingDetection.IsUnconvincingShortText("Kasumi'd better", 0.61));
    }

    [Fact]
    public void NoConfidenceMeansNoJudgement()
    {
        // The engine reports no score for some readings, and a missing score is not a low one.
        Assert.False(ShortReadingDetection.IsUnconvincingShortText("DM", null));
    }
}
