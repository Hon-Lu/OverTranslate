using OverTranslate.Services.Ocr.Manga;
using Xunit;

namespace OverTranslate.Tests;

public class MangaDecodeGuardTests
{
    [Fact]
    public void EndsInLoop_OneTokenRunToTheLimit_IsLoop()
    {
        var tokens = new List<long> { 5, 6 };
        tokens.AddRange(Enumerable.Repeat(7L, MangaDecodeGuard.LoopTokens));

        Assert.True(MangaDecodeGuard.EndsInLoop(tokens));
    }

    [Fact]
    public void EndsInLoop_ShortRuns_AreNotLoops()
    {
        // A manga-ocr ellipsis is twelve dot tokens; どどどど and うんうん are shorter still.
        Assert.False(MangaDecodeGuard.EndsInLoop([.. Enumerable.Repeat(9L, 12), 3]));
        Assert.False(MangaDecodeGuard.EndsInLoop([.. Enumerable.Repeat(9L, 12)]));
        Assert.False(MangaDecodeGuard.EndsInLoop([1, 2, 1, 2, 1, 2]));
    }

    [Fact]
    public void EndsInLoop_LongUnitThreeTimes_IsLoop()
    {
        long[] unit = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        var tokens = new List<long> { 42 };
        for (int i = 0; i < 3; i++) tokens.AddRange(unit);

        Assert.True(MangaDecodeGuard.EndsInLoop(tokens));
        Assert.False(MangaDecodeGuard.EndsInLoop(tokens.Take(tokens.Count - 1).ToList()));
    }
}
