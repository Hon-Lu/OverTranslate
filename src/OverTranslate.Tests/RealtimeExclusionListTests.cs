using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// What a monitor capture is told to leave out once 取詞翻譯 and 替換翻譯 may open over a running
/// session (#326).
/// </summary>
public class RealtimeExclusionListTests
{
    private static readonly IReadOnlyList<IntPtr> Layers = [10, 20, 30];

    [Fact]
    public void NothingElseOnScreenLeavesTheLayersAsTheyAre()
    {
        var list = RealtimeSessionController.ExclusionList(Layers, [30, 10, 20]);

        Assert.Same(Layers, list);
    }

    [Fact]
    public void AnotherWindowOfOursIsAddedAfterTheLayers()
    {
        var list = RealtimeSessionController.ExclusionList(Layers, [20, 99, 10, 30]);

        Assert.Equal([10, 20, 30, 99], list);
    }

    [Fact]
    public void TheSameWindowsInAnotherZOrderGiveTheSameList()
    {
        var before = RealtimeSessionController.ExclusionList(Layers, [77, 10, 55, 20]);
        var after = RealtimeSessionController.ExclusionList(Layers, [55, 20, 10, 77]);

        Assert.Equal(before, after);
        Assert.Equal([10, 20, 30, 55, 77], after);
    }
}
