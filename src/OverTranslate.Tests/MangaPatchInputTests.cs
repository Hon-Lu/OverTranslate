using System.Runtime.InteropServices;
using System.Security.Cryptography;
using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// CPT-flex was trained on its author's preprocessing, so the encoder input has to be that, to the
/// bit. Every expected hash here is SHA-256 of the float32 arrays the author's <c>preprocess(fit="aspect")</c>
/// (manga-ocr-CPT 1.0.0, Pillow 12.3, numpy 2.4) gives for the same picture, and of the position
/// weights built from torch's bicubic resize the way tools/MangaModelExport/export_flex.py builds them.
/// </summary>
public class MangaPatchInputTests
{
    private const int Cells = MangaPatchInput.Patches;

    [Theory]
    [InlineData(37, 120, 24, 8, "ac54a62b10e531428b90db76cfdf069f429ca99c7083c6e2147ceb1c3436b2fe", "7136cc09d95cee7c5fa49d1d8cfd41db98e252c9a3955f2e2da375f93d5b5b22", "5dd1a484c275c10199ab1040aaf21b351385e8d2e4db0c11b4117232fae22ecd")]
    [InlineData(300, 40, 5, 38, "f328d2259fd444064a5bbc16f84ba89348374f10539039e5e1ef2fdd0ab910fa", "cf1ab3a9208974801ad8a22bab708d82830483e0d9334286d3198040e33b9f44", "32cad08c12a6f86d668e6efb2f97d529c5330d9a8b8e9ea16cbf37042ea025bf")]
    [InlineData(224, 224, 14, 14, "34665338fde846925b6c897387358aefacfe028490e6469abb4a139ad08cdb2b", "fff91e873df93b4fe0f83d2e7a763bf00af8fe4a51f4e5662a433611336db00a", "15a2ef709ae50883daba54007e971147e91cdb866cf557dfa85319b8ad6b9de1")]
    [InlineData(5, 3, 11, 17, "9c09c538e1978b7c881f7e988929d2ed3cfd922642d75773871d2b6da587043f", "f2306e5c58139ac0cce6711fcf09f54056bc607b3faa69800b4bd7fcb4b613ea", "deb52ac84488d4b215a2cf0a9c283c9cfdea9cc9171357c22cb38f8fe683dd37")]
    [InlineData(61, 1100, 55, 3, "6bb32feccbd9656c54eb571ba20ce7da424d9390a192992ed3587cb24e1f5eab", "f37b2e4f311dc5440fcf5112e3cf2296efceed89f538065e7c8196944f25ec26", "4f40189253048394663294f135b10888ca007bb28467add3f4b257f5ce5a8413")]
    public void Fill_IsTheAuthorsInput_BitForBit(
        int width, int height, int rows, int columns, string patchesHash, string maskHash, string positionsHash)
    {
        // A grey ramp that wraps, so every resize lands on different values.
        var grey = new byte[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            grey[y * width + x] = (byte)((x * 7 + y * 13) % 256);

        var patches = new float[Cells * MangaPatchInput.PatchLength];
        var mask = new float[Cells];
        var positions = new float[Cells * Cells];
        MangaPatchInput.Fill(grey, width, height, patches, mask, positions);

        var (_, gotRows, gotColumns) = MangaPatchInput.Grid(height, width);
        Assert.Equal((rows, columns), (gotRows, gotColumns));
        Assert.Equal(patchesHash, Hash(patches));
        Assert.Equal(maskHash, Hash(mask));
        Assert.Equal(positionsHash, Hash(positions));
    }

    [Fact]
    public void Fill_ClearsWhatAPreviousBlockLeft()
    {
        var patches = new float[Cells * MangaPatchInput.PatchLength];
        var mask = new float[Cells];
        var positions = new float[Cells * Cells];
        var square = Enumerable.Repeat((byte)200, 224 * 224).ToArray();
        MangaPatchInput.Fill(square, 224, 224, patches, mask, positions);
        // 1×300: 1 column of patches, the rest padding.
        MangaPatchInput.Fill(new byte[300], 1, 300, patches, mask, positions);

        var (_, rows, columns) = MangaPatchInput.Grid(300, 1);
        int real = rows * columns;
        Assert.Equal(real, mask.Count(m => m == 1f));
        Assert.All(patches.Skip(real * MangaPatchInput.PatchLength), v => Assert.Equal(0f, v));
        Assert.All(positions.Skip(real * Cells), v => Assert.Equal(0f, v));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(14)]
    [InlineData(26)]
    [InlineData(196)]
    public void CubicWeights_SumToOneForEveryCell(int size)
    {
        var weights = MangaPatchInput.CubicWeights(size);
        for (int i = 0; i < size; i++)
        {
            double sum = 0;
            for (int a = 0; a < 14; a++) sum += weights[i, a];
            Assert.Equal(1.0, sum, 12);
        }
    }

    [Fact]
    public void BorderMedian_RoundsTheMiddlePairDown()
    {
        // 2×2: edge 10, 11, 20, 21, 10, 20, 11, 21 → middle pair 11, 20 → 15.
        Assert.Equal(15, MangaPatchInput.BorderMedian([10, 11, 20, 21], 2, 2));
    }

    private static string Hash(float[] values) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan()))).ToLowerInvariant();
}
