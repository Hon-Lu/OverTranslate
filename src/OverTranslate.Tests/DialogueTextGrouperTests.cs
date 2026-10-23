using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using OverTranslate.Services.Realtime;
using Xunit;

namespace OverTranslate.Tests;

public class DialogueTextGrouperTests
{
    [Fact]
    public void ImaiSubtitleWideCaptureKeepsBothRowsTogether()
    {
        Assert.Single(DialogueTextGrouper.Group([
            Block("I do want to see how you look", 482, 19, 833, 104),
            Block("in Ako'sclothes, Imai-san.", 526, 108, 759, 71)]));
    }

    [Theory]
    [InlineData(100, 45, 759)] // still too different in height
    [InlineData(200, 71, 759)] // too far below the previous line
    [InlineData(108, 71, 140)] // a short label cannot use the relaxed height check
    public void SubtitleHeightToleranceDoesNotRemoveOtherGuards(double y, double height, double width)
    {
        Assert.Equal(2, DialogueTextGrouper.Group([
            Block("I do want to see how you look", 482, 19, 833, 104),
            Block("Other text", 526, y, width, height)]).Count);
    }

    [Theory]
    [InlineData("想像を超えてく", "る每日", "想像を超えてくる每日")]
    [InlineData("踏んできた修羅場が", "just like a", "踏んできた修羅場がjust like a")]
    [InlineData("I", "am here.", "I am here.")]
    public void InlineTextUsesTheExistingBoundarySpacing(string left, string right, string expected)
    {
        Assert.Equal(expected, Assert.Single(DialogueTextGrouper.Group([Block(left, 0, 0, 100), Block(right, 108, 0, 100)])).Text);
    }

    [Fact]
    public void TwoIsolatedNoiseCharactersCannotBecomeAWord()
    {
        var grouped = OcrService.GroupRealtime([Block("元", 976, 2, 27, 19, 0.96), Block("V", 1015, 0, 23, 22, 0.81)], 223, RealtimeBlockMode.Subtitle);
        Assert.Empty(RealtimeTranslationSession.RejectShortReadings(grouped, -1));
    }

    [Fact]
    public void SmallerSpeakerNameAboveDialogueStaysSeparate()
    {
        Assert.Equal(2, DialogueTextGrouper.Group([Block("しろは", 211, 56, 86, 32), Block("「うん、じゃあ次はね……", 57, 114, 335, 48)]).Count);
    }

    [Fact]
    public void SpeakerNameStickingOutOverIndentedDialogueStaysSeparate()
    {
        // Ren'Py's default name plate (#341), at a framing where the heights pass the 0.75 floor.
        var groups = DialogueTextGrouper.Group([Block("Eileen", 10, 0, 124, 48), Block("What would you like to see?", 41, 56, 389, 37)]);
        Assert.Equal(["Eileen", "What would you like to see?"], groups.Select(g => g.Text));
    }

    [Theory]
    [InlineData("HEY,", 80, 0, 40, 20, "WHAT ARE YOU DOING HERE", 0, 24, 200, 20)] // centred bubble
    [InlineData("その音はどこかおぞましく、", 575, 1158, 594, 58, "不穏なものが接近していることをエマたちに感じさせた。", 566, 1229, 1207, 64)]
    [InlineData("エマもヒロも身を強張らせ、", 559, 1140, 608, 83, "その視線が、ゆるゆると、通路に向けられる。", 663, 1230, 968, 63)]
    [InlineData("「ねえ", 0, 0, 90, 40, "聞いてる？」", 40, 46, 230, 40)] // hanging indent
    [InlineData("stevencastle**", 204, 107, 177, 33, "IF YOU'D SEEN HIM CRAWLING FORWARD AFTER", 202, 133, 493, 36)] // flush name
    public void ShortFirstLinesThatAreNotSpeakerLabelsStayJoined(
        string first, double x1, double y1, double w1, double h1,
        string second, double x2, double y2, double w2, double h2)
    {
        Assert.Single(DialogueTextGrouper.Group([Block(first, x1, y1, w1, h1), Block(second, x2, y2, w2, h2)]));
    }

    [Fact]
    public void ShortMiddleLineOfAParagraphIsNotTakenForASpeakerLabel()
    {
        // The second line alone would pass as a name over the third; it already belongs to a paragraph.
        Assert.Single(DialogueTextGrouper.Group([
            Block("This is the first line of", 10, 0, 200),
            Block("a short", 0, 24, 60),
            Block("paragraph that continues here", 20, 48, 200)]));
    }

    [Fact]
    public void DifferentSizedUiRowsStaySeparate()
    {
        Assert.Equal(3, DialogueTextGrouper.Group([Block("미스틸", 113, 0, 207, 52), Block("×××", 171, 58, 96, 32), Block("RP 8932-680", 59, 103, 369, 53)]).Count);
    }

    [Fact]
    public void GuitarDialogueBenefitSurvives()
    {
        Assert.Single(DialogueTextGrouper.Group([Block("The news did say they were building", 70, 62, 921, 79),
            Block("a facility to study that weird guitar...", 64, 127, 940, 78)]));
    }

    private static OcrTextBlock Block(string text, double x, double y, double w, double h = 20, double confidence = 0.99) =>
        new(text, new Rect(x, y, w, h), Confidence: confidence, LayoutBounds: new Rect(x, y, w, h),
            LayoutScript: OcrLayoutScript.Latin, LayoutGlyphHeight: h * 0.82);

    [Fact]
    public void JoinsSplitRowsBeforeJoiningTheNextSentence()
    {
        var a = Block("I heard a bunch", 0, 0, 140);
        var b = Block("of experts.", 160, 0, 110);
        var c = Block("They are studying it.", 20, 29, 220);
        var group = Assert.Single(DialogueTextGrouper.Group([c, b, a]));
        Assert.Equal("I heard a bunch of experts. They are studying it.", group.Text);
        Assert.Equal(2, group.Lines.Count);
    }

    [Fact]
    public void UnresolvedHorizontalGapCannotBeSkippedVertically()
    {
        var a = Block("left text", 0, 0, 100);
        var b = Block("right text", 240, 0, 100);
        var c = Block("below text", 0, 27, 100);
        Assert.Equal(3, DialogueTextGrouper.Group([a, b, c]).Count);
    }

    [Theory]
    [InlineData(0, 65, 160, 20)] // separate dialogue boxes
    [InlineData(300, 28, 160, 20)] // another column
    [InlineData(0, 28, 100, 8)] // tiny UI/name tag
    public void DoesNotJoinUnrelatedGeometry(double x, double y, double w, double h)
    {
        Assert.Equal(2, DialogueTextGrouper.Group([Block("Main dialogue", 0, 0, 200), Block("Other text", x, y, w, h)]).Count);
    }

    [Fact]
    public void NoiseIsRemovedBeforeItCanContaminateDialogue()
    {
        var noise = Block("EIN", 0, 0, 240, 110, 0.99);
        var shortNoise = Block("x", 60, 10, 10, confidence: 0.5);
        var text = Block("Actual dialogue", 0, 30, 180);
        Assert.Equal(text, Assert.Single(OcrService.GroupRealtime([noise, shortNoise, text], 100, RealtimeBlockMode.Subtitle)));
    }

    [Fact]
    public void PanelRoutingStillUsesTheOriginalGrouper()
    {
        var blocks = new List<OcrTextBlock> { Block("Title", 0, 0, 100), Block("Description", 0, 28, 180) };
        var expected = OcrTextBlockGrouper.Group(OcrService.RejectUnconvincingBlocks(blocks), GroupingProfile.Realtime);
        var actual = OcrService.GroupRealtime(blocks, 100, RealtimeBlockMode.Panel);
        Assert.Equal(expected.Select(b => (b.Text, b.Bounds)), actual.Select(b => (b.Text, b.Bounds)));
    }

    [Fact]
    public void DoesNotSkipAnInterveningSpeakerLabel()
    {
        var top = Block("Previous dialogue", 0, 0, 200);
        var label = Block("Speaker", 0, 21, 60, 5);
        var bottom = Block("Next dialogue", 0, 28, 200);
        Assert.Equal(3, DialogueTextGrouper.Group([top, label, bottom]).Count);
    }

    [Fact]
    public void ConfidentSingleLetterCanJoinItsSentence()
    {
        var result = OcrService.GroupRealtime([Block("I", 0, 0, 8), Block("am here.", 15, 0, 90)], 100, RealtimeBlockMode.Subtitle);
        Assert.Equal("I am here.", Assert.Single(result).Text);
    }
}
