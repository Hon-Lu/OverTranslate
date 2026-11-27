using OverTranslate.Views.Realtime;
using Xunit;

namespace OverTranslate.Tests;

/// <summary>
/// The folded lists on the 即時翻譯 page: which rows show before 顯示其餘, and what the line counts.
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
    public void Unfolded_ShowsEveryRow()
    {
        var (shown, hidden) = CollapsibleRows.Shown(7, [5], expanded: true);

        Assert.Equal(Enumerable.Range(0, 7), shown);
        Assert.Equal(0, hidden);
    }

    [Fact]
    public void ASelectedRowPastTheThird_ShowsInItsOwnPlace_AndIsNotCountedInTheRest()
    {
        var (shown, hidden) = CollapsibleRows.Shown(7, [4], expanded: false);

        Assert.Equal([0, 1, 2, 4], shown);
        Assert.Equal(3, hidden);
    }

    [Fact]
    public void SeveralKeptRows_AllShow_InOrder_AndOnesAlreadyShownChangeNothing()
    {
        var (shown, hidden) = CollapsibleRows.Shown(8, [6, 1, 3, 42, -1], expanded: false);

        Assert.Equal([0, 1, 2, 3, 6], shown);
        Assert.Equal(3, hidden);
    }

    [Fact]
    public void WhenTheKeptRowIsTheOnlyOneLeft_NothingIsHidden()
    {
        var (shown, hidden) = CollapsibleRows.Shown(4, [3], expanded: false);

        Assert.Equal([0, 1, 2, 3], shown);
        Assert.Equal(0, hidden);
    }

    [Theory]
    [InlineData(3, 0, false, false)]
    [InlineData(7, 4, false, true)]
    [InlineData(7, 0, true, true)]
    [InlineData(4, 0, false, false)]
    public void TheMoreRow_IsThereWhenRowsAreFoldedAwayOrTheListIsUnfolded(int count, int hidden, bool expanded, bool expected) =>
        Assert.Equal(expected, CollapsibleRows.HasMoreRow(count, hidden, expanded));

    [Fact]
    public void TheCountFollowsTheList_AsRowsAreAddedAndRemoved()
    {
        Assert.Equal(1, CollapsibleRows.Shown(4, null, false).Hidden);
        Assert.Equal(2, CollapsibleRows.Shown(5, null, false).Hidden);
        Assert.Equal(0, CollapsibleRows.Shown(3, null, false).Hidden);
        Assert.False(CollapsibleRows.Folds(3));
    }
}
