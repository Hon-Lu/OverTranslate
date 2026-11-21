using OverTranslate.Services.Ocr;
using SkiaSharp;
using Xunit;

namespace OverTranslate.Tests;

public class SmallKanaRereadTests
{
    private static string[] Chars(string text) => [.. text.Select(c => c.ToString())];

    private static float[] Scores(int length, float value) => [.. Enumerable.Repeat(value, length)];

    [Fact]
    public void Merge_TakesTheSmallTwinOfAFullSizeKana()
    {
        var merged = SmallKanaReread.Merge(
            Chars("つていうか"), Scores(5, 0.9f), Chars("っていうか"), Scores(5, 0.8f));

        Assert.NotNull(merged);
        Assert.Equal("っていうか", string.Concat(merged.Value.Chars));
        Assert.Equal(0.8f, merged.Value.Scores[0]);
        Assert.Equal(0.9f, merged.Value.Scores[1]);
    }

    [Fact]
    public void Merge_NeverMakesASmallKanaFullSize()
    {
        // The Stardew Valley line: the reading with room said や where the first said ゃ.
        Assert.Null(SmallKanaReread.Merge(
            Chars("ゃってる"), Scores(4, 0.9f), Chars("やってる"), Scores(4, 0.9f)));
    }

    [Fact]
    public void Merge_KeepsEverythingElseFromTheFirstReading()
    {
        var merged = SmallKanaReread.Merge(
            Chars("チヤンネル登録"), Scores(7, 0.9f), Chars("チャンネル登緑"), Scores(7, 0.9f));

        Assert.Equal("チャンネル登録", string.Concat(merged!.Value.Chars));
    }

    [Fact]
    public void Merge_IgnoresReadingsOfDifferentLength()
    {
        Assert.Null(SmallKanaReread.Merge(
            Chars("でしよ"), Scores(3, 0.9f), Chars("でしょ!"), Scores(4, 0.9f)));
    }

    [Fact]
    public void Merge_OnlyPairsTwins()
    {
        // つ and ゃ are both kana and both differ in size from something, but not from each other.
        Assert.Null(SmallKanaReread.Merge(
            Chars("つて"), Scores(2, 0.9f), Chars("ゃて"), Scores(2, 0.9f)));
    }

    [Fact]
    public void Merge_OnlyJoinsASmallKanaWhereKanaAreWrittenThatWay()
    {
        // ァ after プ: the auto-probe corpus's ポップアップ, read ポップァップ with room around it.
        Assert.Null(SmallKanaReread.Merge(
            Chars("ポップアップ"), Scores(6, 0.9f), Chars("ポップァップ"), Scores(6, 0.9f)));
        Assert.Null(SmallKanaReread.Merge(
            Chars("なやみ"), Scores(3, 0.9f), Chars("なゃみ"), Scores(3, 0.9f)));
        // The ones that are: an i-row kana, a loanword pair, っ after anything, a line's first kana.
        Assert.Equal("ディヴィニティ", string.Concat(SmallKanaReread.Merge(
            Chars("ディヴイニテイ"), Scores(7, 0.9f), Chars("ディヴィニティ"), Scores(7, 0.9f))!.Value.Chars));
        Assert.Equal("うわーっ", string.Concat(SmallKanaReread.Merge(
            Chars("うわーつ"), Scores(4, 0.9f), Chars("うわーっ"), Scores(4, 0.9f))!.Value.Chars));
        Assert.Equal("ゃって", string.Concat(SmallKanaReread.Merge(
            Chars("やって"), Scores(3, 0.9f), Chars("ゃって"), Scores(3, 0.9f))!.Value.Chars));
        Assert.Equal("はぁはぁ", string.Concat(SmallKanaReread.Merge(
            Chars("はあはあ"), Scores(4, 0.9f), Chars("はぁはぁ"), Scores(4, 0.9f))!.Value.Chars));
    }

    [Fact]
    public void WithMargins_AddsRowsOfTheCropsBackground()
    {
        using var part = new SKBitmap(40, 10);
        using (var canvas = new SKCanvas(part))
        {
            canvas.Clear(new SKColor(20, 30, 40));
            using var ink = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(5, 0, 10, 10, ink);
        }

        using var padded = SmallKanaReread.WithMargins(part, 3);

        Assert.Equal(40, padded.Width);
        Assert.Equal(16, padded.Height);
        Assert.Equal(new SKColor(20, 30, 40), padded.GetPixel(8, 0));
        Assert.Equal(new SKColor(20, 30, 40), padded.GetPixel(8, 15));
        Assert.Equal(SKColors.White, padded.GetPixel(8, 3));
    }
}
