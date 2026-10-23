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

    // A party status row (web-row-items/ja-dq11-2d-status-row-region.png): names over HP and MP,
    // each label short and set apart from its number. The labels are not cut from their values.
    [Fact]
    public void StatLabelsStayWithTheirValues()
    {
        OcrTextBlock[] frame =
        [
            Block("ベロ二カ", 627, 3, 135, 45, 0.82), Block("カミュ", 401, 6, 106, 43), Block("イレブ", 151, 8, 104, 37),
            Block("セーニヤ", 861, 9, 137, 39, 0.94), Block("HP", 593, 52, 88, 42, 0.98), Block("379", 217, 55, 92, 41, 1.0),
            Block(":315", 425, 56, 125, 38), Block("253", 693, 56, 97, 39, 0.84), Block("HP", 835, 57, 67, 36, 1.0),
            Block("HP", 118, 58, 68, 36, 1.0), Block("33", 935, 58, 65, 36, 0.97), Block("HP", 358, 59, 65, 33, 1.0),
            Block("31", 986, 60, 44, 31, 0.93), Block("279", 692, 103, 95, 39, 1.0), Block("MP", 348, 105, 75, 37, 1.0),
            Block("25", 933, 105, 67, 36, 1.0), Block("：145", 175, 106, 137, 33, 0.95), Block(") 6", 506, 106, 42, 31, 0.92),
            Block("lMP :", 575, 106, 117, 33, 0.82), Block("MP", 836, 106, 65, 32, 1.0), Block("MP", 114, 107, 72, 31, 1.0),
        ];
        Assert.Equal(
            ["ベロ二カ", "カミュ", "イレブ", "セーニヤ", "HP 379 HP:315 HP 253 HP 33 31", ") 6 lMP :279 MP 25", "MP：145 MP"],
            OcrService.GroupRealtime([.. frame], 144, RealtimeBlockMode.Subtitle).Select(b => b.Text));
    }

    // The same status screen at a larger scale: the slot labels already part, but the one-box row
    // of percentages was stacked with the note under it.
    [Fact]
    public void ValuesUnderSlotLabelsAreNotStackedWithTheNoteBelow()
    {
        var texts = DialogueTextGrouper.Group(
        [
            Block("腔内", 920, 33, 88, 51), Block("尻尾", 250, 34, 85, 52), Block("口内", 588, 34, 81, 50),
            Block("肛門", 1091, 35, 82, 48), Block("乳", 424, 36, 72, 51), Block("クリ", 761, 36, 77, 47),
            Block("96%100%100%100%100%100%100%", 171, 140, 1180, 68), Block("開発度は下げることも可能", 325, 196, 705, 62),
        ]).Select(b => b.Text).ToList();
        Assert.Contains("96%100%100%100%100%100%100%", texts);
        Assert.Contains("開発度は下げることも可能", texts);
    }

    // The top of the same status screen: a two-column list whose right column the other rows
    // reach across a wide gap. main read 巫女のお勤め4回絶頂142回 as one item.
    [Fact]
    public void RightColumnOfAListStartsItsOwnItem()
    {
        var texts = DialogueTextGrouper.Group(
        [
            Block("性防率", 65, 75, 96, 28), Block("97%", 170, 77, 53, 28), Block("ピストン数24759回", 272, 79, 211, 25),
            Block("射精された量4637ml", 269, 99, 224, 29), Block("発情回数 9回", 58, 120, 137, 30),
            Block("最大チン長285mm", 271, 122, 194, 27), Block("巫女のお勤め", 58, 142, 135, 30), Block("4回", 192, 144, 40, 28),
            Block("絶頂142回", 269, 144, 135, 27), Block("売春 2回", 60, 168, 96, 26), Block("膣内出し123回", 271, 168, 172, 25),
        ]).Select(b => b.Text).ToList();
        Assert.Contains("巫女のお勤め4回", texts);
        Assert.Contains("絶頂142回", texts);
    }

    // A scoreboard's letterless 26/3/19 over TK / K / A (screen-subtitle-ko), with no slot labels
    // anywhere: grouped as before.
    [Fact]
    public void NumbersAwayFromSlotLabelsAreGroupedAsBefore()
    {
        OcrTextBlock[] frame =
        [
            Block("RTX", 1812, 20, 38, 22, 1.0), Block("VSR", 1809, 34, 44, 27), Block("대상지정 불가", 665, 148, 180, 46, 0.83),
            Block("257s", 597, 232, 57, 31, 0.96), Block("#2", 249, 400, 71, 55, 1.0), Block("26/3/19", 1004, 426, 206, 51, 1.0),
            Block("27,003 841695", 1247, 461, 402, 56, 0.96), Block("랭크", 250, 467, 83, 53, 0.98),
            Block("TK / K / A", 1003, 491, 180, 49, 0.86), Block("20", 689, 522, 70, 50, 0.98), Block("21:54", 249, 529, 114, 50, 1.0),
            Block("딜량", 1246, 530, 84, 52, 0.95), Block("RP", 1441, 533, 61, 46, 1.0), Block("T3", 1016, 557, 74, 47, 1.0),
            Block("1일전", 250, 581, 112, 54, 1.0), Block("아이작", 598, 586, 110, 47, 1.0),
            Block("지금처럼 도파민은 없을 거야 울랄랄라", 284, 835, 1296, 117, 0.95),
        ];
        Assert.Equal(
        [
            "RTX", "VSR", "대상지정 불가", "257s", "#2", "26/3/19 TK / K / A T3", "27,003 841695", "랭크", "20", "21:54",
            "딜량", "RP", "1일전", "아이작", "지금처럼 도파민은 없을 거야 울랄랄라",
        ], OcrService.GroupRealtime([.. frame], 1035, RealtimeBlockMode.Subtitle).Select(b => b.Text));
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
