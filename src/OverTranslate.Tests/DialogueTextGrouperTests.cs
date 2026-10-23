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

    // A status screen's slot labels over their percentages, as read at two scales (OCR coordinates
    // from the report screenshot). main joined 乳口内 and 腔内肛門 at one scale, 口内クリ腔内肛門 at
    // the other.
    public static TheoryData<OcrTextBlock[]> SlotLabelRows => new()
    {
        new[]
        {
            Block("口内", 387, 20, 62, 40), Block("乳", 280, 22, 52, 38), Block("肛門", 723, 22, 61, 37),
            Block("腔内", 610, 23, 59, 34), Block("耳", 858, 27, 30, 27), Block("96%", 117, 95, 97, 44),
            Block("100%100%100%100%100%100%", 219, 95, 683, 44), Block("開発度は下げることも可能", 214, 127, 472, 47),
        },
        new[]
        {
            Block("腔内", 290, 5, 35, 22), Block("尻尾", 70, 6, 31, 20), Block("口内", 182, 6, 31, 18),
            Block("クリ", 240, 7, 29, 17), Block("肛門", 351, 8, 29, 16), Block("乳", 138, 9, 16, 13),
            Block("96%", 47, 42, 49, 24), Block("100%100%100%", 270, 42, 173, 25),
            Block("100%100%", 103, 43, 116, 21), Block("100%", 212, 43, 60, 21),
        },
    };

    [Theory]
    [MemberData(nameof(SlotLabelRows))]
    public void SlotLabelsOverARowOfValuesStaySeparate(OcrTextBlock[] blocks)
    {
        var texts = DialogueTextGrouper.Group(blocks).Select(b => b.Text).ToList();
        foreach (var label in blocks.Where(b => !b.Text.Any(char.IsDigit) && b.Text.Length <= 2))
            Assert.Contains(label.Text, texts);
    }

    // A subtitle written with doubled full-width spaces, cut by the detector into four boxes
    // spaced like slot labels (measured on a rendered はい　　はい　　はい　　はい). Nothing itemised
    // sits beside it, so it is left as the row rules already join it.
    [Fact]
    public void SpacedSubtitleWordsAreNotTakenForSlotLabels()
    {
        OcrTextBlock[] words =
        [
            Block("はい", 326, 39, 101, 55), Block("はい", 854, 39, 101, 55),
            Block("はい", 502, 41, 99, 52), Block("はい", 682, 41, 97, 52),
        ];
        Assert.Equal(["はいはい", "はいはい"], DialogueTextGrouper.Group(words).Select(b => b.Text));
        Assert.Equal(["はいはい", "はいはい", "そうだね、わかってるよ"],
            DialogueTextGrouper.Group([.. words, Block("そうだね、わかってるよ", 330, 110, 620, 52)]).Select(b => b.Text));
    }

    // A visual novel's quick menu under its two icon letters (region-subtitle-ja-game/2.png): short
    // buttons with a three-box row above, but that row covers too little of the menu to be its
    // values. Grouped as before.
    [Fact]
    public void QuickMenuUnderIconLettersIsGroupedAsBefore()
    {
        OcrTextBlock[] frame =
        [
            Block("Summer Pockets REFLECTION BLUE羽未8月2日", 2, 0, 295, 24), Block("ろは", 460, 603, 62, 32, 1.0),
            Block("「うん、じゃあ次はね……」", 285, 662, 346, 42, 1.0), Block("X CLOSE", 1336, 667, 91, 36, 0.92),
            Block("VOICE", 1335, 709, 90, 31, 1.0), Block("LOG", 1334, 747, 78, 40, 0.81),
            Block("G", 805, 792, 36, 32), Block("C", 881, 794, 26, 27, 0.91), Block("LOCK", 1366, 795, 53, 23, 1.0),
            Block("TITLE", 800, 815, 45, 24), Block("QSAVE", 294, 816, 53, 23, 1.0), Block("AUTO", 656, 817, 45, 21, 1.0),
            Block("SKIP", 732, 817, 38, 22, 1.0), Block("QUIT", 875, 817, 39, 21), Block("LOAD", 515, 818, 41, 19, 1.0),
            Block("CONFIG", 939, 818, 58, 21, 1.0), Block("RECORD", 1008, 818, 62, 21, 1.0),
            Block("TWITTER", 1077, 818, 67, 21, 1.0), Block("QLOAD", 366, 819, 53, 18, 0.94),
            Block("SAVE", 446, 819, 34, 18, 1.0), Block("BACK", 589, 819, 38, 18, 1.0), Block("LOCK", 1162, 819, 42, 19, 1.0),
        ];
        Assert.Equal(
        [
            "Summer Pockets REFLECTION BLUE羽未8月2日", "ろは", "「うん、じゃあ次はね……」", "X CLOSE", "VOICE LOG",
            "G", "C", "LOCK", "BACK AUTO SKIP TITLE QUIT CONFIG RECORD TWITTER LOCK", "QSAVE QLOAD SAVE", "LOAD",
        ], OcrService.GroupRealtime([.. frame], 839, RealtimeBlockMode.Subtitle).Select(b => b.Text));
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
