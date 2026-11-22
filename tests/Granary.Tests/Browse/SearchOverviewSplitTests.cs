using Granary.Core.ViewModels;

namespace Granary.Tests.Browse;

/// <summary>The overview's cut of the first page, as the web's splitOverviewResults (pages/searchResultsView.test.ts).</summary>
public sealed class SearchOverviewSplitTests
{
    private static readonly int[] Sixty = [.. Enumerable.Range(1, 60)];

    [Fact]
    public void TwoRows_ThenThreeMore_AndTheRestIsMoreThanShown()
    {
        var (best, more, hasMore) = SearchOverview.Split(Sixty, columns: 6, serverHasMore: false);

        Assert.Equal(Enumerable.Range(1, 12), best);
        Assert.Equal(Enumerable.Range(13, 18), more);
        Assert.True(hasMore);
    }

    [Fact]
    public void APageThatFits_IsAllShown()
    {
        var (best, more, hasMore) = SearchOverview.Split([1, 2, 3, 4, 5], columns: 2, serverHasMore: false);

        Assert.Equal([1, 2, 3, 4], best);
        Assert.Equal([5], more);
        Assert.False(hasMore);
    }

    [Fact]
    public void AnotherServerPage_IsMoreThanShown_EvenWhenThePageFits()
    {
        Assert.True(SearchOverview.Split([1, 2], columns: 6, serverHasMore: true).HasMoreThanShown);
    }

    [Fact]
    public void ColumnsNotMeasuredYet_CountAsOne()
    {
        var (best, more, _) = SearchOverview.Split(Sixty, columns: 0, serverHasMore: false);

        Assert.Equal([1, 2], best);
        Assert.Equal([3, 4, 5], more);
    }
}
