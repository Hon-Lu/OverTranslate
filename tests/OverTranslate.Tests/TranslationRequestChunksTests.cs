using System.Collections.Generic;
using System.Linq;
using OverTranslate.Engines;
using Xunit;

namespace OverTranslate.Tests;

public class TranslationRequestChunksTests
{
    private const int Safe = TranslationRequestChunks.SafeMaxCharacters;

    private static string Repeat(string body, int count) =>
        string.Concat(Enumerable.Repeat(body, count));

    private static List<string> Texts(IEnumerable<TranslationRequestChunk> chunks) =>
        chunks.Select(chunk => chunk.Text).ToList();

    /// <summary>
    /// The margin under the refusal point, kept as a test so it cannot quietly be spent. What the
    /// endpoints count is not published, so a text measured at 999 here may still be over on the
    /// wire — and going over loses Microsoft and Bing together.
    /// </summary>
    [Fact]
    public void SendsWellShortOfWhatTheEndpointsRefuse()
    {
        Assert.True(
            Safe <= TranslationRequestChunks.HardMaxCharacters * 0.85,
            "the working budget should keep a real margin under the hard limit");
    }

    [Theory]
    [InlineData(Safe - 1, 1)]
    [InlineData(Safe, 1)]
    [InlineData(Safe + 1, 2)]
    public void SplitsOnceThePieceWouldNotFitTheBudget(int length, int expected)
    {
        var text = Repeat("a", length);

        Assert.Equal(expected, TranslationRequestChunks.Split(text).Count);
    }

    /// <summary>
    /// Under the hard limit is not the test. A 950-character text every engine would accept is
    /// still split, because Google accepts far more than it translates correctly.
    /// </summary>
    [Fact]
    public void SplitsATextTheEndpointsWouldHaveAccepted()
    {
        var text = Repeat("This sentence is here to take up room. ", 25);

        Assert.InRange(text.Length, Safe + 1, TranslationRequestChunks.HardMaxCharacters);
        Assert.True(TranslationRequestChunks.Split(text).Count > 1);
    }

    /// <summary>
    /// The ordinary case, which is nearly every block: nothing to split, and the text goes up
    /// exactly as it was.
    /// </summary>
    [Fact]
    public void LeavesATextThatFitsAlone()
    {
        var text = "On June 11, 2026, PaddleOCR released PP-OCRv6.";

        var chunk = Assert.Single(TranslationRequestChunks.Split(text));
        Assert.Equal(text, chunk.Text);
        Assert.Equal(TranslationChunkBoundary.End, chunk.BoundaryAfter);
    }

    /// <summary>
    /// The invariant that matters most here, because the fault this replaced produced text that was
    /// not in the source: the pieces are the original, in order, with nothing added or lost.
    /// </summary>
    [Theory]
    [InlineData("Sentences end here. And here. And here again. ", 40)]
    [InlineData("這是一句話。這是另一句話。", 90)]
    [InlineData("no punctuation at all just words going on ", 40)]
    [InlineData("unbrokenrunofcharacterswithnothingtobreakat", 40)]
    [InlineData("Mixed 中英文 content. 中文句子。 More English. ", 40)]
    public void ThePiecesAreExactlyTheOriginal(string body, int count)
    {
        var text = Repeat(body, count);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.Equal(text, string.Concat(Texts(chunks)));
        Assert.All(chunks, chunk => Assert.InRange(chunk.Text.Length, 1, Safe));
    }

    /// <summary>
    /// Cut at sentence ends, so each piece is whole thoughts. A sentence split down the middle is
    /// translated as two half-thoughts, which is the cost this is trying not to pay.
    /// </summary>
    [Fact]
    public void CutsLatinAtSentenceEnds()
    {
        var text = Repeat("This is sentence one. This is sentence two. ", 40);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.All(chunks.SkipLast(1), chunk =>
        {
            Assert.Equal(TranslationChunkBoundary.Sentence, chunk.BoundaryAfter);
            Assert.EndsWith(". ", chunk.Text);
        });
    }

    /// <summary>
    /// CJK needs no space after its full stop, because nothing else uses that mark. Waiting for one
    /// would find no sentence end in the whole text.
    /// </summary>
    [Fact]
    public void CutsChineseAtItsOwnFullStops()
    {
        var text = Repeat("第一句。第二句。第三句。", 120);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks.SkipLast(1), chunk =>
        {
            Assert.Equal(TranslationChunkBoundary.Sentence, chunk.BoundaryAfter);
            Assert.EndsWith("。", chunk.Text);
        });
    }

    /// <summary>
    /// And the same for scripts that end a sentence with something else again — the marks are
    /// listed, the languages are not.
    /// </summary>
    [Theory]
    [InlineData("यह एक वाक्य है।", "।")]
    [InlineData("یہ ایک جملہ ہے۔", "۔")]
    public void CutsOtherScriptsAtTheirOwnSentenceMarks(string sentence, string mark)
    {
        var text = Repeat(sentence, 200);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks.SkipLast(1), chunk =>
        {
            Assert.Equal(TranslationChunkBoundary.Sentence, chunk.BoundaryAfter);
            Assert.EndsWith(mark, chunk.Text);
        });
    }

    /// <summary>
    /// A full stop inside a number is not a sentence end. "1.40s vs 7.30s" must not be cut in two.
    /// </summary>
    [Fact]
    public void DoesNotCutInsideANumber()
    {
        var text = Repeat("It runs in 1.40s versus 7.30s on the same processor. ", 30);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.All(chunks.SkipLast(1), chunk =>
        {
            Assert.EndsWith(". ", chunk.Text);
            // The full stop it ended on is a sentence's, not a decimal point's.
            Assert.False(char.IsDigit(chunk.Text[^3]), $"cut inside a number: \"{chunk.Text[^8..]}\"");
        });
    }

    /// <summary>
    /// A single line break is where a column ended, not where a sentence did. Treating it as a
    /// sentence end would cut a wrapped paragraph at every visual line and undo the grouping that
    /// put it together.
    /// </summary>
    [Fact]
    public void DoesNotTreatOneLineBreakAsASentenceEnd()
    {
        var text = Repeat("this is a long\nsentence that continues\nonto another line ", 30);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks.SkipLast(1), chunk =>
            Assert.Equal(TranslationChunkBoundary.Whitespace, chunk.BoundaryAfter));
    }

    /// <summary>
    /// A blank line is the one break that does mean something, and it beats a sentence end that
    /// would have cost half the budget to reach.
    /// </summary>
    [Fact]
    public void TakesABlankLineOverAnEarlierSentenceEnd()
    {
        var text = "Short. " + Repeat("word ", 150) + "\n\n" + Repeat("more ", 200);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.Equal(TranslationChunkBoundary.Paragraph, chunks[0].BoundaryAfter);
        Assert.Equal(text, string.Concat(Texts(chunks)));
    }

    /// <summary>
    /// With no sentence to end, a word boundary will do — better a whole word than a whole budget.
    /// </summary>
    [Fact]
    public void FallsBackToWordBoundaries()
    {
        var text = Repeat("word ", 400);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks.SkipLast(1), chunk =>
        {
            Assert.Equal(TranslationChunkBoundary.Whitespace, chunk.BoundaryAfter);
            Assert.EndsWith(" ", chunk.Text);
        });
    }

    /// <summary>
    /// And with neither, the budget itself — but never through the middle of a character.
    /// </summary>
    [Fact]
    public void SplitsUnbrokenTextWithoutBreakingACharacter()
    {
        var text = Repeat("🙂", 700);

        var chunks = TranslationRequestChunks.Split(text);

        Assert.Equal(text, string.Concat(Texts(chunks)));
        Assert.Contains(chunks, chunk => chunk.BoundaryAfter == TranslationChunkBoundary.HardSplit);
        Assert.All(chunks, chunk =>
        {
            Assert.False(char.IsLowSurrogate(chunk.Text[0]));
            Assert.False(char.IsHighSurrogate(chunk.Text[^1]));
        });
    }

    private static string JoinOf(TranslationChunkBoundary boundary, string first, string second) =>
        TranslationRequestChunks.Join(
            [new TranslationRequestChunk("", boundary), new TranslationRequestChunk("", TranslationChunkBoundary.End)],
            [first, second]);

    /// <summary>
    /// CJK sets its sentences without spaces, so putting one in adds a gap the original never had.
    /// </summary>
    [Fact]
    public void JoinsChineseSentencesWithoutAddingASpace()
    {
        Assert.Equal(
            "第一段。第二段。",
            JoinOf(TranslationChunkBoundary.Sentence, "第一段。", "第二段。"));
    }

    [Fact]
    public void JoinsEnglishSentencesWithASpace()
    {
        Assert.Equal(
            "First part. Second part.",
            JoinOf(TranslationChunkBoundary.Sentence, "First part.", "Second part."));
    }

    /// <summary>
    /// A blank line in the source is a blank line in the translation — the structure came from the
    /// text, so dropping it would be losing something the user wrote.
    /// </summary>
    [Fact]
    public void JoinsParagraphsAsParagraphs()
    {
        Assert.Equal(
            "第一段。\n\n第二段。",
            JoinOf(TranslationChunkBoundary.Paragraph, "第一段。", "第二段。"));
    }

    /// <summary>
    /// A cut made with nothing to cut at closes up with nothing, rather than inventing a space in
    /// the middle of what was one run of characters.
    /// </summary>
    [Fact]
    public void ClosesUpAHardSplit()
    {
        Assert.Equal(
            "AAAABBBB",
            JoinOf(TranslationChunkBoundary.HardSplit, "AAAA", "BBBB"));
    }

    [Fact]
    public void JoinIgnoresPiecesThatCameBackEmpty()
    {
        var chunks = new List<TranslationRequestChunk>
        {
            new("", TranslationChunkBoundary.Sentence),
            new("", TranslationChunkBoundary.Sentence),
            new("", TranslationChunkBoundary.End),
        };

        Assert.Equal("only this", TranslationRequestChunks.Join(chunks, ["", "  ", "only this"]));
    }
}
