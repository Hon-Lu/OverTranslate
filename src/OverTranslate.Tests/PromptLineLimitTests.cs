using OverTranslate.Views.Settings;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The prompt card's box caps what can be typed or pasted into it. The cap exists because the prompt is
/// sent once per recognised block, so a pasted document is that cost a dozen times over.
/// </summary>
public class PromptLineLimitTests
{
    [Theory]
    [InlineData("")]
    [InlineData("one line")]
    [InlineData("line\nline")]
    public void ShortEnoughTextIsLeftAlone(string text)
    {
        Assert.Equal(-1, PromptEditOverlay.LineLimitOverflowIndex(text, 200));
    }

    [Fact]
    public void TextOfExactlyTheLimitIsLeftAlone()
    {
        var text = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}"));

        Assert.Equal(-1, PromptEditOverlay.LineLimitOverflowIndex(text, 200));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void OneLineTooManyIsCutAtTheBreakThatStartedIt(string lineBreak)
    {
        var kept = string.Join(lineBreak, Enumerable.Range(0, 200).Select(i => $"line {i}"));
        var text = kept + lineBreak + "line 200";

        var overflow = PromptEditOverlay.LineLimitOverflowIndex(text, 200);

        Assert.Equal(kept.Length, overflow);
        // Never ends on half a CRLF pair, which would leave a stray carriage return in the prompt.
        Assert.Equal(kept, text[..overflow]);
    }

    /// <summary>
    /// A trailing break is the 201st line starting, so it goes — otherwise pressing Enter at the
    /// bottom of a full box would appear to do nothing while quietly growing the stored text.
    /// </summary>
    [Fact]
    public void TrailingLineBreakOnAFullBoxCounts()
    {
        var kept = string.Join("\n", Enumerable.Range(0, 200).Select(i => $"line {i}"));

        Assert.Equal(kept.Length, PromptEditOverlay.LineLimitOverflowIndex(kept + "\n", 200));
    }

    [Fact]
    public void EmptyLinesCountTheSameAsWrittenOnes()
    {
        var text = new string('\n', 500);

        var overflow = PromptEditOverlay.LineLimitOverflowIndex(text, 200);

        Assert.Equal(199, overflow);
        Assert.Equal(199, text[..overflow].Length);
    }
}
