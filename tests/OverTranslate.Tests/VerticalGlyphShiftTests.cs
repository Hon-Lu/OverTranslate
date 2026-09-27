using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OverTranslate.Layout;
using OverTranslate.Services;
using Xunit;
using static OverTranslate.Layout.VerticalTextGrid;

namespace OverTranslate.Tests;

/// <summary>
/// Which glyphs vertical writing moves inside their cells, for which targets, and where they land.
/// </summary>
public class VerticalGlyphShiftTests
{
    private const double Cell = 40;

    [Theory]
    [InlineData('。')]
    [InlineData('、')]
    [InlineData('，')]
    [InlineData('．')]
    [InlineData('｡')]
    [InlineData('､')]
    public void SentencePunctuation_MovesForJapaneseAndSimplifiedOnly(char glyph)
    {
        Assert.Equal(GlyphShift.Punctuation, ShiftFor(glyph, "JA"));
        Assert.Equal(GlyphShift.Punctuation, ShiftFor(glyph, "ZH"));
        Assert.Equal(GlyphShift.Punctuation, ShiftFor(glyph, "zh-hans"));
        // Traditional Chinese sets it centred in vertical writing, and JhengHei already draws it so.
        Assert.Equal(GlyphShift.None, ShiftFor(glyph, "ZH-HANT"));
        Assert.Equal(GlyphShift.None, ShiftFor(glyph, "KO"));
        Assert.Equal(GlyphShift.None, ShiftFor(glyph, "EN"));
        Assert.Equal(GlyphShift.None, ShiftFor(glyph, null));
    }

    [Theory]
    [InlineData('っ')]
    [InlineData('ゃ')]
    [InlineData('ゖ')]
    [InlineData('ッ')]
    [InlineData('ョ')]
    [InlineData('ヶ')]
    [InlineData('ㇰ')]
    [InlineData('ㇿ')]
    public void SmallKana_MovesForJapaneseOnly(char glyph)
    {
        Assert.Equal(GlyphShift.SmallKana, ShiftFor(glyph, "ja"));
        Assert.Equal(GlyphShift.None, ShiftFor(glyph, "ZH"));
        Assert.Equal(GlyphShift.None, ShiftFor(glyph, "ZH-HANS"));
        Assert.Equal(GlyphShift.None, ShiftFor(glyph, "ZH-HANT"));
        Assert.Equal(GlyphShift.None, ShiftFor(glyph, "EN"));
    }

    [Theory]
    [InlineData('つ')]
    [InlineData('あ')]
    [InlineData('ツ')]
    [InlineData('漢')]
    [InlineData('！')]
    [InlineData('？')]
    [InlineData('A')]
    public void OtherGlyphs_StayPut(char glyph)
    {
        foreach (var target in new[] { "JA", "ZH", "ZH-HANS", "ZH-HANT", "KO", "EN" })
            Assert.Equal(GlyphShift.None, ShiftFor(glyph, target));
    }

    /// <remarks>A glyph both turned and moved would be moved by the wrong amount: the turn is about its middle.</remarks>
    [Fact]
    public void NoGlyph_IsBothTurnedAndMoved()
    {
        for (char c = '　'; c < '㄀'; c++)
            Assert.False(RotatesGlyph(c) && ShiftFor(c, "JA") != GlyphShift.None, $"U+{(int)c:X4}");
        for (char c = '＀'; c < '￰'; c++)
            Assert.False(RotatesGlyph(c) && ShiftFor(c, "JA") != GlyphShift.None, $"U+{(int)c:X4}");
    }

    [Fact]
    public void Punctuation_InkIsCentredInTheUpperRight()
    {
        // A 。 drawn bottom left, as Yu Gothic UI and YaHei draw it for horizontal text.
        var ink = new Rect(5, 26, 10, 10);

        var shift = InkShift(GlyphShift.Punctuation, ink, Rect.Empty, Cell);

        Assert.True(shift.X > 0);
        Assert.True(shift.Y < 0);
        Assert.Equal(Cell * 0.75, ink.Left + ink.Width / 2 + shift.X, 3);
        Assert.Equal(Cell * 0.25, ink.Top + ink.Height / 2 + shift.Y, 3);
    }

    [Fact]
    public void Punctuation_IsKeptInsideTheCell()
    {
        // Too wide to be centred on the three-quarter mark without crossing the right edge.
        var ink = new Rect(2, 30, 24, 8);

        var shift = InkShift(GlyphShift.Punctuation, ink, Rect.Empty, Cell);

        Assert.Equal(Cell, ink.Right + shift.X, 3);
        Assert.True(ink.Top + shift.Y >= 0);
    }

    [Fact]
    public void Shift_NeverGoesLeftOrDown()
    {
        // Already past where it would be aimed.
        var ink = new Rect(32, 1, 6, 6);

        var shift = InkShift(GlyphShift.Punctuation, ink, Rect.Empty, Cell);

        Assert.Equal(0, shift.X);
        Assert.Equal(0, shift.Y);
    }

    [Fact]
    public void SmallKana_MovesPartWayTowardsTheFullSizeKana()
    {
        var reference = new Rect(6, 2, 28, 32);   // あ
        var ink = new Rect(10, 14, 20, 20);       // っ, bottom-aligned and centred

        var shift = InkShift(GlyphShift.SmallKana, ink, reference, Cell);

        Assert.True(shift.X > 0);
        Assert.True(shift.Y < 0);
        // Flush right with the full-size kana, and only part of the way up — not into the corner.
        Assert.Equal(reference.Right, ink.Right + shift.X, 3);
        Assert.True(ink.Top + shift.Y > reference.Top);
        Assert.True(-shift.Y < ink.Top - reference.Top);
    }

    [Fact]
    public void NothingToShift_GivesZero()
    {
        Assert.Equal(default, InkShift(GlyphShift.None, new Rect(0, 30, 10, 10), Rect.Empty, Cell));
        Assert.Equal(default, InkShift(GlyphShift.SmallKana, new Rect(0, 30, 10, 10), Rect.Empty, Cell));
    }

    /// <remarks>Measured in the real fonts, through the same path the overlays take.</remarks>
    [Theory]
    [InlineData("JA", '。')]
    [InlineData("JA", '、')]
    [InlineData("ZH", '。')]
    [InlineData("ZH-HANS", '，')]
    public void ShiftGlyph_PutsTheInkInTheUpperRight(string target, char glyph) => OnStaThread(() =>
    {
        var cell = Glyph(target, glyph);
        var before = MeasureInk(cell, glyph, Cell);

        ShiftGlyph(cell, glyph, target, Cell);

        var move = Assert.IsType<TranslateTransform>(cell.RenderTransform);
        double x = before.Left + before.Width / 2 + move.X;
        double y = before.Top + before.Height / 2 + move.Y;
        Assert.InRange(x / Cell, 0.6, 0.9);
        Assert.InRange(y / Cell, 0.1, 0.4);
    });

    [Theory]
    [InlineData("ZH-HANT", '。')]
    [InlineData("EN", '。')]
    [InlineData("JA", '漢')]
    public void ShiftGlyph_LeavesOthersAlone(string target, char glyph) => OnStaThread(() =>
    {
        var cell = Glyph(target, glyph);

        ShiftGlyph(cell, glyph, target, Cell);

        Assert.True(cell.RenderTransform is null || cell.RenderTransform.Value.IsIdentity);
    });

    private static TextBlock Glyph(string target, char glyph) => new()
    {
        Text = glyph.ToString(),
        FontFamily = TranslatedTextFont.For(target, LocalizationService.TraditionalChinese),
        FontSize = Cell * 0.92,
        FontWeight = FontWeights.SemiBold,
        TextAlignment = TextAlignment.Center,
    };

    /// <summary>Runs the test body where WPF elements can be made, and brings its failure back out.</summary>
    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { failure = e; }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
