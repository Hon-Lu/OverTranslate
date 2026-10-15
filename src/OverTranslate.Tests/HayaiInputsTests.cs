using System.Text;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The host side of the Hayai graphs. The sizes were produced by the survey's reference
/// (<c>pil_preprocess</c> in <c>run_hayai_ort2.py</c>), which matches the model's own processor.
/// </summary>
public class HayaiInputsTests
{
    [Theory]
    [InlineData(1157, 200, 752, 128)]
    [InlineData(232, 94, 480, 192)]
    [InlineData(31, 134, 144, 624)]
    [InlineData(64, 64, 304, 304)]
    [InlineData(25, 16, 384, 256)]
    [InlineData(900, 900, 304, 304)]
    public void Size_MatchesReference(int height, int width, int expectedHeight, int expectedWidth)
    {
        var (h, w) = HayaiInputs.Size(height, width);

        Assert.Equal((expectedHeight, expectedWidth), (h, w));
        Assert.True(h / 16 * (w / 16) <= HayaiInputs.MaxPatches);
    }

    [Fact]
    public void Resampling_SameSize_IsIdentity()
    {
        var m = HayaiInputs.Resampling(16, 16);

        for (int i = 0; i < 16; i++)
        for (int j = 0; j < 16; j++)
            Assert.Equal(i == j ? 1.0 : 0.0, m[i, j], 12);
    }

    [Theory]
    [InlineData(16, 5)]
    [InlineData(16, 47)]
    public void Resampling_RowsSumToOne(int from, int to)
    {
        var m = HayaiInputs.Resampling(from, to);

        for (int i = 0; i < to; i++)
            Assert.Equal(1.0, Enumerable.Range(0, from).Sum(j => m[i, j]), 9);
    }

    [Fact]
    public void Positions_AtGridSize_AreTheGrid_AndPaddingRepeatsTheFirst()
    {
        var grid = Enumerable.Range(0, 16 * 16 * HayaiInputs.Width).Select(i => (float)(i % 997)).ToArray();

        var positions = HayaiInputs.Positions(grid, 16, 16);

        Assert.Equal(grid, positions.AsSpan(0, grid.Length).ToArray());
        Assert.Equal(
            positions.AsSpan(0, HayaiInputs.Width).ToArray(),
            positions.AsSpan((HayaiInputs.MaxPatches - 1) * HayaiInputs.Width, HayaiInputs.Width).ToArray());
    }

    [Fact]
    public void Gather_MergesTwoByTwo_AndRepeatsTheEdgeOfAnOddGrid()
    {
        var indices = HayaiInputs.Gather(3, 3);

        Assert.Equal(HayaiInputs.MaxVisionTokens * 4, indices.Length);
        Assert.Equal(new long[] { 0, 1, 3, 4 }, indices[..4]);
        Assert.Equal(new long[] { 2, 2, 5, 5 }, indices[4..8]);
        Assert.Equal(new long[] { 8, 8, 8, 8 }, indices[12..16]);
        Assert.All(indices[16..], i => Assert.Equal(0L, i));
    }

    [Fact]
    public void Patches_AreRowMajor_PixelsRowByRow_ChannelsLast()
    {
        // 16×32: two patches side by side; every byte different enough to tell apart.
        var rgb = new byte[16 * 32 * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)(i % 251);
        var into = new float[HayaiInputs.MaxPatches * HayaiInputs.PatchValues];

        HayaiInputs.Patches(rgb, 16, 32, into);

        static float At(byte b) => (b / 255f - 0.5f) / 0.5f;
        // Second patch, its first pixel: image (y 0, x 16), green.
        Assert.Equal(At(rgb[16 * 3 + 1]), into[HayaiInputs.PatchValues + 1]);
        // First patch, second pixel row, first pixel: image (y 1, x 0), red.
        Assert.Equal(At(rgb[32 * 3]), into[16 * 3]);
        Assert.Equal(0f, into[2 * HayaiInputs.PatchValues]);
    }

    [Fact]
    public void TextAngles_UseThePositionInBothHalves()
    {
        Span<double> angles = stackalloc double[HayaiInputs.RopeDim];

        HayaiInputs.TextAngles(3, angles);

        Assert.Equal(3.0, angles[0], 12);
        Assert.Equal(angles[1], angles[HayaiInputs.RopeDim / 2 + 1], 12);
    }

    [Fact]
    public void Vocabulary_DecodesByteLevelTokens_AndSkipsSpecials()
    {
        var alphabet = HayaiVocabulary.ByteAlphabet();
        string Spell(string text) => string.Concat(Encoding.UTF8.GetBytes(text).Select(b => alphabet[b]));
        // あ is three bytes; split it across two tokens as BPE may.
        var whole = Spell("あ");
        string[] tokens = ["<pad>", whole[..1], whole[1..], Spell("A b"), "<eos>"];

        var vocabulary = new HayaiVocabulary(tokens, [0, 4]);

        Assert.Equal("あA b", vocabulary.Decode([0, 1, 2, 3, 4]));
        Assert.Equal('Ġ', alphabet[' ']);
        Assert.Equal('A', alphabet['A']);
    }

    [Fact]
    public void Vocabulary_InvalidUtf8_BecomesReplacementCharacter()
    {
        var alphabet = HayaiVocabulary.ByteAlphabet();
        var vocabulary = new HayaiVocabulary([alphabet[0xE3].ToString()], []);

        Assert.Equal("�", vocabulary.Decode([0]));
    }

    [Theory]
    [InlineData("hayai", true)]
    [InlineData(" Hayai ", true)]
    [InlineData("manga-ocr", false)]
    [InlineData(null, false)]
    public void Choice_FollowsTheEnvironmentVariable(string? value, bool expected)
    {
        Assert.Equal(expected, MangaRecognizerChoice.AsksForHayai(name =>
            name == MangaRecognizerChoice.Variable ? value : null));
    }
}
