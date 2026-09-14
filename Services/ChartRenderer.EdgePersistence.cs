using System.Globalization;

namespace TradeFoundry.Services;

public static partial class ChartRenderer
{
    public static string TearSheetEdgePersistenceRollingExpectancy(EdgePersistenceMetrics metrics)
    {
        var windows = metrics.Rolling.Where(window => window.Points.Count > 0).ToArray();
        if (windows.Length == 0)
            return Empty("Rolling expectancy needs at least 25 completed trades.");

        var colors = new[] { Green, Blue, Gold };
        var traces = windows.Select((window, index) => new
        {
            type = "scatter",
            x = window.Points.Select(point => $"T{point.EndTradeNumber}").ToArray(),
            y = window.Points.Select(point => point.Expectancy).ToArray(),
            mode = "lines+markers",
            name = $"{window.Window}-trade",
            connectgaps = false,
            line = new { color = colors[Math.Min(index, colors.Length - 1)], width = 1.8 },
            marker = new { color = colors[Math.Min(index, colors.Length - 1)], size = 5 },
            customdata = window.Points.Select(point => new object?[] { point.TradeCount, point.NetPnl }).ToArray(),
            hovertemplate = $"%{{x}}<br>{window.Window}-trade expectancy: %{{y:,.2f}}<br>%{{customdata[0]}} trades · net %{{customdata[1]:,.2f}}<extra></extra>"
        }).Cast<object>().ToArray();
        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        SetAxis(layout, "xaxis", "trade sequence");
        SetAxis(layout, "yaxis", "gross expectancy");
        layout["shapes"] = new object[] { HorizontalMarker(0m, Zero, "dash") };
        return Plotly("Rolling expectancy by trade window", traces, layout);
    }

    public static string TearSheetEdgePersistenceRollingQuality(EdgePersistenceMetrics metrics)
    {
        var windows = metrics.Rolling.Where(window => window.Points.Count > 0).ToArray();
        if (windows.Length == 0)
            return Empty("Rolling profit factor and win rate need at least 25 completed trades.");

        var colors = new[] { Blue, Green, Gold };
        var traces = new List<object>();
        foreach (var (window, index) in windows.Select((value, index) => (value, index)))
        {
            var color = colors[Math.Min(index, colors.Length - 1)];
            traces.Add(new
            {
                type = "scatter",
                x = window.Points.Select(point => $"T{point.EndTradeNumber}").ToArray(),
                y = window.Points.Select(point => point.ProfitFactor).ToArray(),
                mode = "lines+markers",
                name = $"{window.Window}-trade PF",
                connectgaps = false,
                line = new { color, width = 1.7 },
                marker = new { color, size = 4 },
                hovertemplate = $"%{{x}}<br>{window.Window}-trade profit factor: %{{y:.2f}}<extra></extra>"
            });
            traces.Add(new
            {
                type = "scatter",
                x = window.Points.Select(point => $"T{point.EndTradeNumber}").ToArray(),
                y = window.Points.Select(point => point.WinRate.HasValue ? point.WinRate.Value * 100m : (decimal?)null).ToArray(),
                yaxis = "y2",
                mode = "lines",
                name = $"{window.Window}-trade win rate",
                connectgaps = false,
                line = new { color, width = 1.3, dash = "dot" },
                hovertemplate = $"%{{x}}<br>{window.Window}-trade win rate: %{{y:.1f}}%<extra></extra>"
            });
        }

        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        layout["yaxis2"] = new Dictionary<string, object?>
        {
            ["title"] = new { text = "win rate (%)", font = new { color = Muted, size = 11 } },
            ["overlaying"] = "y",
            ["side"] = "right",
            ["range"] = new[] { 0, 100 },
            ["showgrid"] = false,
            ["automargin"] = true,
            ["tickfont"] = new { color = Muted, size = 11 }
        };
        SetAxis(layout, "xaxis", "trade sequence");
        SetAxis(layout, "yaxis", "profit factor");
        layout["shapes"] = new object[] { HorizontalMarker(1m, Gold, "dash") };
        return Plotly("Rolling profit factor and win rate", traces, layout);
    }

    public static string TearSheetEdgePersistenceRollingMae(EdgePersistenceMetrics metrics)
    {
        var windows = metrics.Rolling.Where(window => window.Points.Any(point => point.MedianMae.HasValue)).ToArray();
        if (windows.Length == 0)
            return Empty("Rolling MAE persistence needs imported MAE data.");

        var colors = new[] { Red, Blue, Gold };
        var traces = windows.SelectMany((window, index) =>
        {
            var color = colors[Math.Min(index, colors.Length - 1)];
            return new object[]
            {
                new
                {
                    type = "scatter",
                    x = window.Points.Select(point => $"T{point.EndTradeNumber}").ToArray(),
                    y = window.Points.Select(point => point.MedianMae).ToArray(),
                    mode = "lines+markers",
                    name = $"{window.Window}-trade MAE P50",
                    connectgaps = false,
                    line = new { color, width = 1.8 },
                    marker = new { color, size = 4 },
                    hovertemplate = $"%{{x}}<br>{window.Window}-trade median MAE: %{{y:,.2f}}<extra></extra>"
                },
                new
                {
                    type = "scatter",
                    x = window.Points.Select(point => $"T{point.EndTradeNumber}").ToArray(),
                    y = window.Points.Select(point => point.MaeP90).ToArray(),
                    mode = "lines",
                    name = $"{window.Window}-trade MAE P90",
                    connectgaps = false,
                    line = new { color, width = 1.2, dash = "dot" },
                    hovertemplate = $"%{{x}}<br>{window.Window}-trade MAE P90: %{{y:,.2f}}<extra></extra>"
                }
            };
        }).ToArray();
        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        SetAxis(layout, "xaxis", "trade sequence");
        SetAxis(layout, "yaxis", "absolute MAE");
        return Plotly("Rolling MAE persistence", traces, layout);
    }

    public static string TearSheetEdgePersistenceRollingLoss(EdgePersistenceMetrics metrics)
    {
        var windows = metrics.Rolling.Where(window => window.Points.Any(point => point.AverageLoss.HasValue || point.MedianLoss.HasValue)).ToArray();
        if (windows.Length == 0)
            return Empty("Rolling loss stability needs at least one losing trade.");

        var colors = new[] { Red, Blue, Gold };
        var traces = windows.SelectMany((window, index) =>
        {
            var color = colors[Math.Min(index, colors.Length - 1)];
            return new object[]
            {
                new
                {
                    type = "scatter",
                    x = window.Points.Select(point => $"T{point.EndTradeNumber}").ToArray(),
                    y = window.Points.Select(point => point.AverageLoss).ToArray(),
                    mode = "lines+markers",
                    name = $"{window.Window}-trade average loss",
                    connectgaps = false,
                    line = new { color, width = 1.8 },
                    marker = new { color, size = 4 },
                    hovertemplate = $"%{{x}}<br>{window.Window}-trade average loss: %{{y:,.2f}}<extra></extra>"
                },
                new
                {
                    type = "scatter",
                    x = window.Points.Select(point => $"T{point.EndTradeNumber}").ToArray(),
                    y = window.Points.Select(point => point.MedianLoss).ToArray(),
                    mode = "lines",
                    name = $"{window.Window}-trade median loss",
                    connectgaps = false,
                    line = new { color, width = 1.2, dash = "dot" },
                    hovertemplate = $"%{{x}}<br>{window.Window}-trade median loss: %{{y:,.2f}}<extra></extra>"
                }
            };
        }).ToArray();
        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        SetAxis(layout, "xaxis", "trade sequence");
        SetAxis(layout, "yaxis", "gross loss");
        layout["shapes"] = new object[] { HorizontalMarker(0m, Zero, "dash") };
        return Plotly("Rolling average and median loss", traces, layout);
    }

    public static string TearSheetEdgePersistenceBlocks(EdgePersistenceMetrics metrics)
    {
        var sets = metrics.Blocks.Where(set => set.Blocks.Count > 0).ToArray();
        if (sets.Length == 0)
            return Empty("Non-overlapping blocks need at least 25 completed trades.");

        var blocks = sets.SelectMany(set => set.Blocks.Select(block => new
        {
            Label = $"{set.Window} · B{block.BlockNumber}",
            block.NetPnl,
            block.Expectancy,
            block.ProfitFactor,
            block.WinRate
        })).ToArray();
        var layout = CartesianLayout("x unified");
        layout["showlegend"] = false;
        SetAxis(layout, "xaxis", "non-overlapping block");
        if (layout["xaxis"] is Dictionary<string, object?> axis)
            axis["type"] = "category";
        SetAxis(layout, "yaxis", "net P&L");
        layout["shapes"] = new object[] { HorizontalMarker(0m, Zero, "dash") };
        return Plotly("Non-overlapping block results", new object[]
        {
            new
            {
                type = "bar",
                x = blocks.Select(block => block.Label).ToArray(),
                y = blocks.Select(block => block.NetPnl).ToArray(),
                marker = new { color = blocks.Select(block => block.NetPnl >= 0m ? Green : Red).ToArray() },
                customdata = blocks.Select(block => new object?[] { block.Expectancy, block.ProfitFactor, block.WinRate.HasValue ? block.WinRate.Value * 100m : (decimal?)null }).ToArray(),
                hovertemplate = "%{x}<br>Net P&L: %{y:,.2f}<br>Expectancy: %{customdata[0]:,.2f}<br>Profit factor: %{customdata[1]:.2f}<br>Win rate: %{customdata[2]:.1f}%<extra></extra>"
            }
        }, layout);
    }

}
