using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

public class MangaRubyMaskTests
{
    private const int Width = 80;
    private const int Height = 120;

    /// <summary>A white block with black squares standing in for glyphs.</summary>
    private static byte[] Block(params (int X, int Y, int Size)[] glyphs)
    {
        var rgb = Enumerable.Repeat((byte)255, Width * Height * 3).ToArray();
        foreach (var (gx, gy, size) in glyphs)
            for (int y = gy; y < gy + size; y++)
                for (int x = gx; x < gx + size; x++)
                    rgb[(y * Width + x) * 3] = rgb[(y * Width + x) * 3 + 1] = rgb[(y * Width + x) * 3 + 2] = 0;
        return rgb;
    }

    private static (int X, int Y, int Size)[] Column(int x, int size, int count, int top = 4) =>
        Enumerable.Range(0, count).Select(i => (x, top + i * (size + 3), size)).ToArray();

    private static bool IsInk(byte[] rgb, int x, int y) => rgb[(y * Width + x) * 3] < 128;

    [Fact]
    public void Apply_RubyBesideAColumn_IsCleared()
    {
        // Two body columns of 16px glyphs, and 7px ruby glyphs right of the second.
        var rgb = Block([.. Column(20, 16, 6), .. Column(46, 16, 6), .. Column(64, 7, 4, top: 10)]);

        Assert.True(MangaRubyMask.Apply(rgb, Width, Height));
        Assert.False(IsInk(rgb, 66, 12));
        Assert.True(IsInk(rgb, 50, 10));
        Assert.True(IsInk(rgb, 24, 10));
    }

    [Fact]
    public void Apply_BodyColumnsOnly_AreLeftAlone()
    {
        var rgb = Block([.. Column(14, 16, 6), .. Column(36, 16, 4), .. Column(58, 16, 6)]);
        var before = (byte[])rgb.Clone();

        Assert.False(MangaRubyMask.Apply(rgb, Width, Height));
        Assert.Equal(before, rgb);
    }

    [Fact]
    public void Apply_NarrowLineAtTheEdge_IsNotRuby()
    {
        // A balloon's outline or a neighbour's text cut by the block's edge.
        var rgb = Block([.. Column(20, 16, 6), .. Column(46, 16, 6), .. Column(Width - 7, 7, 4, top: 10)]);

        Assert.False(MangaRubyMask.Apply(rgb, Width, Height));
    }
}
