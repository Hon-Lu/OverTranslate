using System.Windows;
using OverTranslate.Services;
using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// Items of a bar — buttons, tabs, a nav menu, a row of prices — that the same-line gap test let
/// through stay apart on the live panel path; a line split into two or three pieces stays one line.
/// </summary>
/// <remarks>
/// Every row here is a real row of the realtime corpus, coordinates and glyph estimates as the
/// detector returned them, except the word-boxed sentence, which is built to the word spacing
/// <see cref="SameLineGapThreshold.Fallback"/> was measured on.
/// </remarks>
public class LabelRunGroupingTests
{
    // Key-hint bar of a game's save menu: 「決定」 and 「もどる」 0.39 apart, and 0.39 from the
    // caption before them.
    private static readonly OcrTextBlock[] KeyHintBar =
    [
        Measured("4 ゲームモード変更", 1171, 974, 199, 32, OcrLayoutScript.Cjk, 26.091),
        Measured("セーブデータ削除", 1418, 973, 175, 32, OcrLayoutScript.Cjk, 25.813),
        Measured("決定", 1606, 972, 89, 34, OcrLayoutScript.Cjk, 27.88),
        Measured("もどる", 1708, 974, 102, 32, OcrLayoutScript.Cjk, 26.24),
    ];

    [Fact]
    public void AKeyHintBar_KeepsItsButtonsApart()
    {
        var lines = Lines(KeyHintBar, GroupingProfile.Realtime);

        Assert.Equal(["4 ゲームモード変更", "セーブデータ削除", "決定", "もどる"], lines);
    }

    [Fact]
    public void ASiteNav_KeepsItsEntriesApart()
    {
        var row = new[]
        {
            Measured("HTML", 369, 110, 77, 27, OcrLayoutScript.Latin, 22.14),
            Measured("CSS", 457, 110, 64, 27, OcrLayoutScript.Latin, 13.5),
            Measured("JavaScript", 534, 113, 109, 22, OcrLayoutScript.Latin, 14.17),
            Measured("Web APIs", 656, 113, 104, 22, OcrLayoutScript.Latin, 18.04),
            Measured("All ✓", 771, 107, 50, 28, OcrLayoutScript.Latin, 14),
            Measured("Learn V", 834, 110, 95, 27, OcrLayoutScript.Latin, 20.583),
            Measured("Tools", 940, 104, 95, 36, OcrLayoutScript.Latin, 24.7),
            Measured("About", 1046, 113, 73, 22, OcrLayoutScript.Latin, 18.04),
            Measured("Blog", 1129, 109, 49, 31, OcrLayoutScript.Latin, 15.5),
        };

        var lines = Lines(row, GroupingProfile.Realtime);

        Assert.Equal(row.Select(block => block.Text), lines);
    }

    [Fact]
    public void APriceBesideItsDiscountBadge_StaysApart()
    {
        var row = new[]
        {
            Measured("サイバーパンク2077", 196, 91, 170, 27, OcrLayoutScript.Cjk, 18.236),
            Measured("2633円", 407, 91, 68, 27, OcrLayoutScript.Cjk, 16.048),
            Measured("8778円", 491, 93, 56, 23, OcrLayoutScript.Cjk, 13.216),
            Measured("70%オフ", 559, 90, 76, 34, OcrLayoutScript.Cjk, 17.936),
        };

        var lines = Lines(row, GroupingProfile.Realtime);

        Assert.Contains("8778円", lines);
        Assert.Contains("70%オフ", lines);
    }

    [Fact]
    public void AStatsHeader_KeepsTheEntryAfterTheRunApart()
    {
        var row = new[]
        {
            Measured("Speed", 277, 694, 84, 27, OcrLayoutScript.Latin, 21.84),
            Measured("Stamina", 366, 695, 96, 26, OcrLayoutScript.Latin, 17.829),
            Measured("Power", 470, 695, 77, 26, OcrLayoutScript.Latin, 20.02),
            Measured("Guts", 571, 694, 66, 27, OcrLayoutScript.Latin, 21.45),
        };

        Assert.Contains("Power", Lines(row, GroupingProfile.Realtime));
    }

    /// <summary>
    /// A key glyph is one item with the caption after it, however wide the space: only the gap
    /// between two captions is refused.
    /// </summary>
    [Fact]
    public void AKeyGlyphStaysWithItsCaption_WhileTheCaptionsPart()
    {
        var row = new[]
        {
            Measured("A", 927, 976, 30, 29, OcrLayoutScript.Latin, 14.5),
            Measured("①페이지 넘기기", 970, 973, 163, 35, OcrLayoutScript.Latin, 28.7),
            Measured("가 조건 검색 및 정렬", 1146, 976, 200, 32, OcrLayoutScript.Latin, 26.24),
        };

        var lines = Lines(row, GroupingProfile.Realtime);

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("A", lines[0]);
        Assert.Contains("페이지 넘기기", lines[0]);
        Assert.Equal("가 조건 검색 및 정렬", lines[1]);
    }

    /// <summary>
    /// Something else on the row half a screen away is not a third item of a bar: the level and
    /// the name beside it stay one line.
    /// </summary>
    [Fact]
    public void AnItemAcrossTheScreen_DoesNotMakeABar()
    {
        var row = new[]
        {
            Measured("시스템", 163, 781, 95, 44, OcrLayoutScript.Latin, 22),
            Measured("Lv 100", 762, 802, 97, 31, OcrLayoutScript.Latin, 25.22),
            Measured("ML50나루메아", 872, 803, 163, 31, OcrLayoutScript.Latin, 25.42),
        };

        Assert.Equal(2, Lines(row, GroupingProfile.Realtime).Count);
    }

    /// <summary>
    /// Only the live profile refuses label runs. The screenshot side has nothing measured to move
    /// it on, so the same bar is still one line there.
    /// </summary>
    [Fact]
    public void TheScreenshotModes_StillJoinTheBar()
    {
        Assert.Contains(Lines(KeyHintBar, GroupingProfile.General), line => line.Contains("決定") && line.Contains("もどる"));
        Assert.Contains(Lines(KeyHintBar, GroupingProfile.Interface), line => line.Contains("決定") && line.Contains("もどる"));
    }

    public static TheoryData<OcrTextBlock[]> OneLine => new()
    {
        // A subtitle split once at its pause, 0.39 apart: two pieces alone on the row are not a bar.
        new[]
        {
            Measured("何なんですか？", 528, 85, 364, 66, OcrLayoutScript.Cjk, 54.12),
            Measured("何なんですか？", 919, 82, 371, 72, OcrLayoutScript.Cjk, 59.04),
        },
        // The same pause with no mark on either side: the text alone would pass for two captions,
        // and only the row having nothing else at item spacing keeps it whole.
        new[]
        {
            Measured("ちょっと待って", 528, 85, 364, 66, OcrLayoutScript.Cjk, 54.12),
            Measured("本当に行くの", 919, 82, 320, 72, OcrLayoutScript.Cjk, 59.04),
        },
        // A date and its weekday.
        new[]
        {
            Measured("7/29", 147, 82, 157, 57, OcrLayoutScript.Latin, 46.74),
            Measured("(THU)", 299, 99, 88, 42, OcrLayoutScript.Latin, 22.88),
        },
        // A sentence the detector cut in two, with the count beside it.
        new[]
        {
            Measured("22人がこのレビュ", 119, 122, 144, 29, OcrLayoutScript.Cjk, 18.88),
            Measured("一が参考になったと投票しました", 256, 123, 232, 29, OcrLayoutScript.Cjk, 18.251),
        },
        // A quotation opening on a name.
        new[]
        {
            Measured("「勇者の", 376, 569, 105, 41, OcrLayoutScript.Cjk, 30.975),
            Measured("生まれ変わりなんだよ。", 481, 574, 256, 37, OcrLayoutScript.Cjk, 27.462),
        },
        // Two dates of one event line.
        new[]
        {
            Measured("6月2日、", 1537, 1051, 79, 25, OcrLayoutScript.Cjk, 18.644),
            Measured("西暦1073年", 1608, 1046, 128, 34, OcrLayoutScript.Cjk, 21.577),
        },
        // A sentence boxed word by word at 0.35 of a line apart: a run, but not of labels.
        Words("Send", "this", "session", "to", "the", "background"),
    };

    [Theory]
    [MemberData(nameof(OneLine))]
    public void ALineInPieces_StaysOneLine(OcrTextBlock[] row)
    {
        Assert.Single(Lines(row, GroupingProfile.Realtime));
    }

    private static List<string> Lines(IReadOnlyList<OcrTextBlock> row, GroupingProfile profile) =>
        OcrTextBlockGrouper.GroupLines(row, profile)
            .OrderBy(line => line.LayoutBounds.X)
            .Select(line => line.Text)
            .ToList();

    private static OcrTextBlock[] Words(params string[] words)
    {
        const double height = 24;
        var x = 100.0;
        var blocks = new List<OcrTextBlock>();
        foreach (var word in words)
        {
            var width = word.Length * 12.0;
            blocks.Add(Measured(word, x, 100, width, height, OcrLayoutScript.Latin, 16));
            x += width + height * 0.35;
        }
        return [.. blocks];
    }

    private static OcrTextBlock Measured(
        string text, double x, double y, double width, double height, OcrLayoutScript script, double glyph)
    {
        var box = new Rect(x, y, width, height);
        return new(text, box, LayoutScript: script, LayoutBounds: box, LayoutGlyphHeight: glyph);
    }
}
