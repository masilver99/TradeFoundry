using TradeFoundry.Pages;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class PagerTests
{
    private static Pager Create(int page, int pageCount, int groupSize = 10) => new(page, pageCount, number => $"?page={number}", groupSize);

    [Fact]
    public void FirstGroupHasNoPreviousGroupAndSkipsToTheNextGroup()
    {
        var pager = Create(3, 47);

        Assert.Equal(Enumerable.Range(1, 10), pager.GroupPages);
        Assert.False(pager.HasPreviousGroup);
        Assert.True(pager.HasNextGroup);
        Assert.Equal(11, pager.NextGroupPage);
        Assert.True(pager.HasPrevious);
        Assert.True(pager.HasNext);
    }

    [Fact]
    public void MiddleGroupSkipsBothWays()
    {
        var pager = Create(20, 47);

        Assert.Equal(Enumerable.Range(11, 10), pager.GroupPages);
        Assert.Equal(10, pager.PreviousGroupPage);
        Assert.Equal(21, pager.NextGroupPage);
    }

    [Fact]
    public void LastGroupIsPartialAndPagesAreClamped()
    {
        var pager = Create(99, 47);

        Assert.Equal(47, pager.Page);
        Assert.Equal(Enumerable.Range(41, 7), pager.GroupPages);
        Assert.False(pager.HasNextGroup);
        Assert.False(pager.HasNext);
        Assert.Equal("?page=47", pager.Url(48));
        Assert.Equal("?page=1", pager.Url(0));
    }

    [Fact]
    public void SinglePageIsHidden()
    {
        Assert.False(Create(1, 1).IsVisible);
        Assert.False(Create(1, 0).IsVisible);
        Assert.True(Create(1, 2).IsVisible);
    }
}
