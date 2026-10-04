using System.Windows;
using OverTranslate.Layout;
using OverTranslate.Services;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// design.md §8.1 / §10.3 — the group's translation put back onto the lines it was read from.
/// </summary>
/// <remarks>
/// Counting the pieces proves almost nothing here, so these ask the two questions that matter:
/// whether every character survives the trip, and whether the cuts land where a reader would put
/// them.
/// </remarks>
public class GroupedTranslationLinesTests
{
    private static List<string> Split(string translation, params double[] lineWidths)
    {
        var lines = lineWidths
            .Select((width, index) => new Rect(0, index * 30, width, 28))
            .ToArray();
        var block = new TranslatedBlock("source", translation, new Rect(0, 0, 400, 90), lines);

        return [.. GroupedTranslationLines.SplitOntoSourceLines([block]).Select(b => b.TranslatedText)];
    }

    /// <summary>Whitespace aside — the cuts eat the spaces they land on — nothing is lost or repeated.</summary>
    private static void AssertRecomposes(string translation, IEnumerable<string> segments)
    {
        static string Bare(string text) => new([.. text.Where(character => !char.IsWhiteSpace(character))]);

        Assert.Equal(Bare(translation), Bare(string.Concat(segments)));
    }

    [Fact]
    public void OneSegmentPerSourceLine()
    {
        var segments = Split("これは段落として翻訳された一続きの文章です。", 200, 180, 160);

        Assert.Equal(3, segments.Count);
    }

    [Fact]
    public void ASingleLineGroupIsLeftAlone()
    {
        var line = new Rect(0, 0, 200, 28);
        var block = new TranslatedBlock("source", "譯文", new Rect(0, 0, 200, 28), [line]);

        var placed = Assert.Single(GroupedTranslationLines.SplitOntoSourceLines([block]));
        Assert.Equal("譯文", placed.TranslatedText);
        Assert.Same(block.SourceLineBounds, placed.SourceLineBounds);
    }

    [Fact]
    public void EachLineIsDrawnAsTheSingleLineItNowIs()
    {
        var lines = new[] { new Rect(0, 0, 200, 28), new Rect(0, 30, 200, 28) };
        var block = new TranslatedBlock("source", "一二三四五六七八", new Rect(0, 0, 200, 58), lines);

        var placed = GroupedTranslationLines.SplitOntoSourceLines([block]);

        Assert.All(placed, line => Assert.Null(line.SourceLineBounds));
        Assert.Equal(lines, placed.Select(line => line.Bounds));
    }

    /// <summary>
    /// The whole point of the exercise: the reader gets every character the engine returned.
    /// </summary>
    [Theory]
    [InlineData("これは段落として翻訳された一続きの文章です、途中で切れてはいけません。")]
    [InlineData("The quick brown fox jumps over the lazy dog and keeps on running until it stops.")]
    [InlineData("短")]
    [InlineData("")]
    [InlineData("Supercalifragilisticexpialidociousandthensomemoreletterswithnobreakatall")]
    public void NothingIsLostAndNothingIsRepeated(string translation)
    {
        var segments = Split(translation, 200, 180, 160, 90);

        Assert.Equal(4, segments.Count);
        AssertRecomposes(translation, segments);
    }

    [Fact]
    public void AnEmptyTranslationGivesEveryLineAnEmptyString()
    {
        var segments = Split("", 200, 180);

        Assert.All(segments, segment => Assert.Equal("", segment));
    }

    /// <summary>
    /// A target language that writes word boundaries is cut at them, not through a word.
    /// </summary>
    [Fact]
    public void ALatinTargetIsCutBetweenWords()
    {
        var translation = "The quick brown fox jumps over the lazy dog";

        var segments = Split(translation, 200, 200);

        Assert.All(segments, segment => Assert.DoesNotContain("  ", segment));
        foreach (var segment in segments)
            Assert.All(segment.Split(' '), word => Assert.Contains(word, translation.Split(' ')));
        AssertRecomposes(translation, segments);
    }

    /// <summary>
    /// A script without spaces has no word boundary to find, and cutting between any two
    /// characters is ordinary there — but not between a character and the punctuation that closes
    /// it.
    /// </summary>
    [Fact]
    public void ClosingPunctuationIsNotStrandedAtTheStartOfALine()
    {
        // Sized so the proportional cut lands immediately before the closing bracket.
        var translation = "彼は「行こう」と言った、それから歩き出した。";

        for (var width = 60; width <= 260; width += 10)
        {
            var segments = Split(translation, width, 300 - width);

            Assert.DoesNotContain(segments[1][..1], new[] { "」", "、", "。" });
            AssertRecomposes(translation, segments);
        }
    }

    [Fact]
    public void OpeningPunctuationIsNotStrandedAtTheEndOfALine()
    {
        var translation = "彼は「行こう」と言った、それから歩き出した。";

        for (var width = 60; width <= 260; width += 10)
        {
            var segments = Split(translation, width, 300 - width);

            Assert.NotEqual("「", segments[0][^1..]);
            AssertRecomposes(translation, segments);
        }
    }

    /// <summary>
    /// A source line can come back with no width at all — a detection collapsed to a sliver, a
    /// box the layout pass emptied. It still gets a line, and the text still all arrives.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    public void ADegenerateSourceLineWidthStillProducesALine(double narrowWidth)
    {
        var translation = "これは段落として翻訳された一続きの文章です。";

        var segments = Split(translation, narrowWidth, 200, narrowWidth);

        Assert.Equal(3, segments.Count);
        AssertRecomposes(translation, segments);
    }

    /// <summary>
    /// An emoji is two chars and one character, and a cut between its halves would show as two
    /// replacement marks with the picture gone.
    /// </summary>
    [Theory]
    // Enough emoji that the search window always has somewhere clean to land...
    [InlineData("🎉🎉🎉🎉🎉🎉🎉🎉", 2)]
    // ...and too few, which is what puts the cut on the proportional position with nothing better
    // inside the window: two emoji shared out over four lines leaves the halves of one of them on
    // either side of a cut.
    [InlineData("🎉🎉", 4)]
    public void ASurrogatePairIsNotCutInHalf(string translation, int lineCount)
    {
        for (var width = 20; width <= 280; width += 10)
        {
            var widths = Enumerable.Range(0, lineCount)
                .Select(index => index == 0 ? width : (300.0 - width) / (lineCount - 1))
                .ToArray();

            var segments = Split(translation, widths);

            Assert.All(segments, segment => Assert.False(HasLoneSurrogate(segment)));
            AssertRecomposes(translation, segments);
        }
    }

    private static bool HasLoneSurrogate(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1])) return true;
                index++;
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                return true;
            }
        }

        return false;
    }
}
