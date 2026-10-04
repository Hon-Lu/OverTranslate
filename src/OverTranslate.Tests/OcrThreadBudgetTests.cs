using OverTranslate.Services.Ocr;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The point of this budget is how it behaves on machines nobody here has, so the table is a test
/// rather than a comment.
/// </summary>
public class OcrThreadBudgetTests
{
    [Theory]
    [InlineData(2, 2, 1)]
    [InlineData(4, 2, 1)]
    [InlineData(6, 2, 1)]
    [InlineData(8, 2, 2)]
    [InlineData(12, 3, 3)]
    [InlineData(16, 4, 3)]
    [InlineData(24, 4, 3)]
    [InlineData(64, 4, 4)]
    public void TheBudgetIsSpentAsTheTableSays(int cores, int threads, int slots)
    {
        var budget = OcrThreadBudget.For(cores);

        Assert.Equal(threads, budget.Threads);
        Assert.Equal(slots, budget.Slots);
    }

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(64)]
    public void AMachineWithRoomNeverMakesARealtimeBlockQueue(int cores)
    {
        // A session is one loop per block, so fewer slots than blocks means the feature contends
        // with itself. Measured before this floor existed: a three-block session on 16 cores turned
        // away 58 polls, 36 of them from the one busy block.
        Assert.True(OcrThreadBudget.For(cores).Slots >= OcrThreadBudget.RealtimeBlocks);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(64)]
    public void NoMachineGivesAwayMoreThanThreeQuartersOfItself(int cores)
    {
        // The invariant that survived. Half was the original rule, and it turned out to budget for a
        // peak that does not happen — three blocks rarely fire together, and a block whose pixels
        // have not changed does not run at all; measured use on 16 cores sat near 25%. Three
        // quarters is what buying the never-queue floor costs, and it is still a ceiling rather
        // than a reading.
        var (threads, slots) = OcrThreadBudget.For(cores);

        Assert.True(
            threads * slots <= Math.Max(2, cores * 3 / 4),
            $"{cores} cores would take {threads}x{slots}={threads * slots}");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void SmallMachinesAreLeftExactlyWhereTheyWere(int cores)
    {
        // The measurements behind the larger thread counts were taken on a 16-core machine, where
        // four threads is a quarter of it. They say nothing about a four-core machine, where four
        // threads is all of it — so a small machine gets the shipped behaviour unchanged: two
        // threads, and the same slot count the old Clamp(cores / 4, 1, 4) produced. It pays for that
        // by letting its third block queue, which is the graceful half of the trade.
        var (threads, slots) = OcrThreadBudget.For(cores);

        Assert.Equal(2, threads);
        Assert.Equal(Math.Clamp(cores / 4, 1, 4), slots);
    }

    [Fact]
    public void ThreadsNeverExceedWhereTheModelStopsScaling()
    {
        // Six threads measured slower than four on a 16-core machine, so nothing is gained by
        // handing a bigger machine more of them.
        foreach (var cores in new[] { 16, 32, 64, 128, 256 })
            Assert.True(OcrThreadBudget.For(cores).Threads <= OcrThreadBudget.MaxThreads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AnImpossibleCoreCountStillProducesAUsableBudget(int cores)
    {
        // Environment.ProcessorCount should never be this, but a budget of zero threads would wedge
        // recognition for good, and that is a bad way to find out it can be.
        var (threads, slots) = OcrThreadBudget.For(cores);

        Assert.True(threads >= 1);
        Assert.True(slots >= 1);
    }
}
