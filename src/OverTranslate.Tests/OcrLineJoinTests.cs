using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The lines of a group are joined with a space only where the script sets one, or where a line
/// ended before the group's right edge (#398).
/// </summary>
public class OcrLineJoinTests
{
    [Theory]
    // #398: a Japanese line broken in the middle of でも.
    [InlineData("まだ男は苦手……っていうか、嫌い。で", "もあんたは――いや、いまのなしっ",
        "まだ男は苦手……っていうか、嫌い。でもあんたは――いや、いまのなしっ")]
    [InlineData("我们今天", "去公园吧。", "我们今天去公园吧。")]
    [InlineData("「そうか", "」", "「そうか」")]
    // Dashes and dots take the side of what is next to them.
    [InlineData("でも――", "いや", "でも――いや")]
    [InlineData("……", "でも", "……でも")]
    [InlineData("Wait…", "what?", "Wait… what?")]
    // Everything else keeps the space it always had.
    [InlineData("This sentence is", "complete.", "This sentence is complete.")]
    [InlineData("안녕하세요", "반갑습니다", "안녕하세요 반갑습니다")]
    [InlineData("2026", "年", "2026 年")]
    [InlineData("Steamの", "設定", "Steamの設定")]
    [InlineData("設定を開く", "Settings", "設定を開く Settings")]
    public void JoinsALineThatRunsOn(string first, string second, string expected) =>
        Assert.Equal(expected, OcrTextBlockGrouper.JoinLines([(first, true), (second, false)]));

    [Fact]
    public void ALineThatEndedKeepsItsSpace() =>
        Assert.Equal("剣聖 オリヴァー・カーディフ",
            OcrTextBlockGrouper.JoinLines([("剣聖", false), ("オリヴァー・カーディフ", false)]));

    [Fact]
    public void BlankLinesAreSkipped() =>
        Assert.Equal("でもあんたは",
            OcrTextBlockGrouper.JoinLines([(" で ", true), ("  ", true), ("もあんたは", false)]));

    [Fact]
    public void AJapaneseLineThatReachesTheRightEdgeRunsOn()
    {
        // The capture in #398: the first line is the wider one, the second stops short.
        var first = Line("まだ男は苦手……っていうか、嫌い。で", 46, 16, 365, 25);
        var second = Line("もあんたは――いや、いまのなしっ", 43, 33, 328, 30);

        Assert.Equal("まだ男は苦手……っていうか、嫌い。でもあんたは――いや、いまのなしっ",
            OcrTextBlockGrouper.BuildGroup([first, second]).Text);
    }

    [Fact]
    public void AJapaneseLineThatStopsShortKeepsItsSpace()
    {
        // A title over a name: the first line ends well before the second does.
        var first = Line("剣聖", 0, 0, 60, 26);
        var second = Line("オリヴァー・カーディフ", 0, 30, 260, 26);

        Assert.Equal("剣聖 オリヴァー・カーディフ", OcrTextBlockGrouper.BuildGroup([first, second]).Text);
    }

    private static OcrTextBlock Line(string text, double x, double y, double width, double height) =>
        new(text, new Rect(x, y, width, height), LayoutScript: OcrLayoutScript.Cjk,
            LayoutBounds: new Rect(x, y, width, height), LayoutGlyphHeight: height * 0.75);
}
