using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class CommandPaletteTests
{
    [Fact]
    public void CatalogIncludesChartMetricAndNavigationDestinationsWithStableTargets()
    {
        var items = CommandPaletteCatalog.Items;

        Assert.Contains(items, item => item.Category == "Navigation" && item.Title == "Daybook Review");
        Assert.Contains(items, item => item.Category == "Chart" && item.Title == "Underwater net P&L");
        Assert.Contains(items, item => item.Category == "Metric" && item.Title == "Sharpe" && item.Route == "/analytics" && item.TargetId == CommandPaletteCatalog.Anchor("risk-metrics", "Sharpe"));
        Assert.True(items.Count(item => item.Category == "Chart") >= 50);
        Assert.All(items.Where(item => item.Category is "Chart" or "Metric"), item => Assert.False(string.IsNullOrWhiteSpace(item.TargetId)));
        Assert.All(items, item => Assert.StartsWith("/", item.Route));
        var duplicateTargets = items.Where(item => item.TargetId is not null)
            .GroupBy(item => $"{item.Route}#{item.TargetId}")
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key} ({string.Join(", ", group.Select(item => item.Title))})");
        Assert.True(!duplicateTargets.Any(), string.Join(Environment.NewLine, duplicateTargets));
        Assert.Equal("tf-command-risk-metrics-sharpe", CommandPaletteCatalog.Anchor("risk-metrics", "Sharpe"));
    }
}
