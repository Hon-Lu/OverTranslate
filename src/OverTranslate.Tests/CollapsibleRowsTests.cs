using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The folded custom list on the 即時翻譯 page's 模式 card: which rows show before 顯示其餘, in what
/// order, and what the row counts.
/// </summary>
public class CollapsibleRowsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ThreeRowsOrFewer_AllShow_AndNothingFolds(int count)
    {
        var (shown, hidden) = CollapsibleRows.Shown(count, null, expanded: false);

        Assert.Equal(Enumerable.Range(0, count), shown);
        Assert.Equal(0, hidden);
        Assert.False(CollapsibleRows.Folds(count));
    }

    [Fact]
    public void Folded_ShowsTheFirstThree_AndCountsTheRest()
    {
        var (shown, hidden) = CollapsibleRows.Shown(7, null, expanded: false);

        Assert.Equal([0, 1, 2], shown);
        Assert.Equal(4, hidden);
        Assert.True(CollapsibleRows.Folds(7));
    }

    [Fact]
    public void APinnedRowPastTheThird_GoesFirst_AndTheListStaysThreeRows()
    {
        var (shown, hidden) = CollapsibleRows.Shown(7, 4, expanded: false);

        Assert.Equal([4, 0, 1], shown);
        Assert.Equal(4, hidden);
    }

    [Fact]
    public void APinnedRowAmongTheFirstThree_GoesFirst_TheOthersKeepTheirOrder()
    {
        var (shown, hidden) = CollapsibleRows.Shown(5, 2, expanded: false);

        Assert.Equal([2, 0, 1], shown);
        Assert.Equal(2, hidden);
    }

    [Fact]
    public void Unfolded_ShowsEveryRow_InTheSameOrderAsFolded()
    {
        var (shown, hidden) = CollapsibleRows.Shown(7, 5, expanded: true);

        Assert.Equal([5, 0, 1, 2, 3, 4, 6], shown);
        Assert.Equal(0, hidden);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(7)]
    public void APinOutOfRange_IsIgnored(int pinned)
    {
        Assert.Equal([0, 1, 2], CollapsibleRows.Shown(7, pinned, expanded: false).Shown);
    }

    [Theory]
    [InlineData(3, 0, false, false)]
    [InlineData(7, 4, false, true)]
    [InlineData(7, 0, true, true)]
    public void TheMoreRow_IsThereWhenRowsAreFoldedAwayOrTheListIsUnfolded(int count, int hidden, bool expanded, bool expected) =>
        Assert.Equal(expected, CollapsibleRows.HasMoreRow(count, hidden, expanded));

    [Fact]
    public void TheCountFollowsTheList_AsRowsAreAddedAndRemoved()
    {
        Assert.Equal(1, CollapsibleRows.Shown(4, null, false).Hidden);
        Assert.Equal(2, CollapsibleRows.Shown(5, 0, false).Hidden);
        Assert.Equal(0, CollapsibleRows.Shown(3, 2, false).Hidden);
        Assert.False(CollapsibleRows.Folds(3));
    }
}
