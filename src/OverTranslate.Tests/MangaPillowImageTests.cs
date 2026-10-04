using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The manga models were measured behind Pillow, so the preprocessing has to be Pillow's to the
/// byte. Every expected array here was produced by Pillow 11 itself (Image.resize with BILINEAR,
/// convert("L")) from the same source pixels.
/// </summary>
public class MangaPillowImageTests
{
    // 7×5 RGB, random.seed(7).
    private static readonly byte[] Source =
    [
        165, 77, 202, 24, 37, 48, 187, 29, 109, 19, 44, 222, 214, 35, 123, 46, 217, 30, 63, 114, 31,
        203, 25, 113, 23, 68, 148, 214, 73, 60, 157, 92, 52, 96, 190, 49, 32, 30, 105, 254, 218, 160,
        238, 232, 185, 153, 127, 92, 124, 41, 153, 253, 175, 229, 147, 37, 60, 214, 84, 175, 77, 250,
        215, 20, 39, 160, 174, 179, 254, 233, 35, 47, 138, 242, 33, 31, 158, 228, 145, 197, 177, 11,
        236, 181, 86, 59, 252, 30, 111, 147, 66, 126, 203, 200, 254, 41, 85, 229, 205, 142, 70, 220,
        142, 212, 183,
    ];

    [Fact]
    public void ShrinkingBothWays_MatchesPillow()
    {
        byte[] pillow =
        [
            114, 51, 116, 115, 63, 129, 97, 139, 66, 148, 92, 126, 161, 105, 103, 136, 132, 135, 151, 118,
            160, 160, 140, 127, 106, 174, 179, 77, 97, 192, 131, 191, 127, 116, 167, 200,
        ];

        Assert.Equal(pillow, PillowImage.Resize(Source, 7, 5, 3, 3, 4));
    }

    [Fact]
    public void ShrinkingOneAxisOnly_MatchesPillow()
    {
        byte[] pillow =
        [
            109, 50, 116, 106, 48, 151, 88, 141, 50, 128, 54, 117, 141, 106, 62, 125, 131, 113, 177, 146,
            138, 190, 103, 160, 151, 133, 165, 132, 98, 176, 138, 167, 104, 73, 203, 189, 58, 96, 197, 128,
            199, 134, 130, 155, 204,
        ];

        Assert.Equal(pillow, PillowImage.Resize(Source, 7, 5, 3, 3, 5));
    }

    [Fact]
    public void Enlarging_MatchesPillow()
    {
        var result = PillowImage.Resize(Source, 7, 5, 3, 11, 9);

        // First row and last pixel of Pillow's 11×9 answer; the corners are the source's own.
        byte[] firstRow =
        [
            165, 77, 202, 101, 59, 132, 39, 36, 54, 143, 31, 92, 126, 34, 150, 19, 44, 222, 143, 38, 159,
            168, 85, 98, 61, 200, 38, 55, 161, 31, 63, 114, 31,
        ];
        Assert.Equal(firstRow, result[..33]);
        Assert.Equal(new byte[] { 142, 212, 183 }, result[^3..]);
    }

    [Fact]
    public void Luma_MatchesPillowsConvertL()
    {
        byte[] pillow =
        [
            118, 34, 85, 57, 99, 145, 89, 88, 64, 114, 107, 146, 39, 222, 228, 131, 79, 204, 73, 133, 194,
            47, 186, 96, 187, 128, 179, 162, 89, 91, 117, 214, 183, 109, 188,
        ];

        Assert.Equal(pillow, PillowImage.Luma(Source));
    }

    [Fact]
    public void GreyThenResize_MatchesPillow()
    {
        Assert.Equal(
            new byte[] { 82, 111, 131, 139, 127, 160 },
            PillowImage.Resize(PillowImage.Luma(Source), 7, 5, 1, 2, 3));
    }

    [Theory]
    [InlineData("お前 は", "お前は")]
    [InlineData("えっと…", "えっと...")]
    [InlineData("そう・・・", "そう...")]
    [InlineData("ＡＢ１", "AB1")]
    [InlineData("中・国", "中・国")]
    public void PostProcess_IsMangaOcrs(string raw, string expected) =>
        Assert.Equal(expected, MangaTextRecognizer.PostProcess(raw));
}
