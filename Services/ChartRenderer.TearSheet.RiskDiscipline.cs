using System.Globalization;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

public static partial class ChartRenderer
{
    public static string TearSheetRiskDisciplineRollingRescue(IEnumerable<Trade> source)
    {
        var history = RiskDisciplineTrend.Build(source);
        if (history.Rolling.Count == 0)
            return Empty($"Rolling rescue tracking needs at least {RiskDisciplineTrend.RollingWindow} completed trades.");

        var points = history.Rolling
            .Where(point => point.HasCompleteWinnerRiskData && (point.RescueRate.HasValue || point.RescueProfitShare.HasValue))
            .ToArray();
        if (points.Length == 0)
            return Empty("Rolling rescue tracking needs MAE and initial-risk data for gross winners.");

        var labels = points.Select(point => DateTimeLabel(point.ExitUtc)).ToArray();
        var customdata = points
            .Select(point => new object?[] { point.WindowTradeCount, point.RiskQualifiedTradeCount, point.WinnerCount })
            .ToArray();
        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        SetAxis(layout, "xaxis", "exit date", date: true);
        SetAxis(layout, "yaxis", "share (%)", percent: true);
        SetRange(layout, "yaxis", new[] { 0, 100 });
        return Plotly("Rolling rescue dependency", new object[]
        {
            new
            {
                type = "scatter",
                x = labels,
                y = points.Select(point => PercentValue(point.RescueRate)).ToArray(),
                mode = "lines+markers",
                name = "Rescue Rate",
                connectgaps = false,
                line = new { color = Green, width = 1.8 },
                marker = new { color = Green, size = 5 },
                customdata,
                hovertemplate = "%{x|%b %-d, %Y}<br>Rescue rate: %{y:.1f}%<br>%{customdata[0]}-trade window · %{customdata[2]} winners<extra></extra>"
            },
            new
            {
                type = "scatter",
                x = labels,
                y = points.Select(point => PercentValue(point.RescueProfitShare)).ToArray(),
                mode = "lines+markers",
                name = "Rescue Profit Share",
                connectgaps = false,
                line = new { color = Blue, width = 1.6, dash = "dot" },
                marker = new { color = Blue, size = 5 },
                customdata,
                hovertemplate = "%{x|%b %-d, %Y}<br>Rescue profit share: %{y:.1f}%<br>%{customdata[0]}-trade window · %{customdata[2]} winners<extra></extra>"
            }
        }, layout);
    }

    public static string TearSheetRiskDisciplineRollingHeat(IEnumerable<Trade> source)
    {
        var history = RiskDisciplineTrend.Build(source);
        if (history.Rolling.Count == 0)
            return Empty($"Rolling heat tracking needs at least {RiskDisciplineTrend.RollingWindow} completed trades.");

        var points = history.Rolling
            .Where(point => point.HasCompleteWinnerRiskData && (point.WinnerMaeP90R.HasValue || point.WinnerHeatRatio.HasValue))
            .ToArray();
        if (points.Length == 0)
            return Empty("Rolling heat tracking needs MAE, initial-risk, and gross-winner data.");

        var labels = points.Select(point => DateTimeLabel(point.ExitUtc)).ToArray();
        var customdata = points
            .Select(point => new object?[] { point.WindowTradeCount, point.WinnerCount })
            .ToArray();
        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        layout["yaxis2"] = new Dictionary<string, object?>
        {
            ["title"] = new { text = "winner heat ratio", font = new { color = Muted, size = 11 } },
            ["overlaying"] = "y",
            ["side"] = "right",
            ["showgrid"] = false,
            ["automargin"] = true,
            ["tickfont"] = new { color = Muted, size = 11 }
        };
        SetAxis(layout, "xaxis", "exit date", date: true);
        SetAxis(layout, "yaxis", "winner MAE P90 (R)");
        return Plotly("Rolling winner heat and MAE", new object[]
        {
            new
            {
                type = "scatter",
                x = labels,
                y = points.Select(point => point.WinnerMaeP90R).ToArray(),
                mode = "lines+markers",
                name = "Winner MAE P90",
                connectgaps = false,
                line = new { color = Blue, width = 1.8 },
                marker = new { color = Blue, size = 5 },
                customdata,
                hovertemplate = "%{x|%b %-d, %Y}<br>Winner MAE P90: %{y:.2f}R<br>%{customdata[0]}-trade window · %{customdata[1]} winners<extra></extra>"
            },
            new
            {
                type = "scatter",
                x = labels,
                y = points.Select(point => point.WinnerHeatRatio).ToArray(),
                yaxis = "y2",
                mode = "lines+markers",
                name = "Winner Heat Ratio",
                connectgaps = false,
                line = new { color = Gold, width = 1.6, dash = "dot" },
                marker = new { color = Gold, size = 5 },
                customdata,
                hovertemplate = "%{x|%b %-d, %Y}<br>Winner heat ratio: %{y:.2f}<br>%{customdata[0]}-trade window · %{customdata[1]} winners<extra></extra>"
            }
        }, layout);
    }

    public static string TearSheetRiskDisciplineRollingViolations(IEnumerable<Trade> source)
    {
        var history = RiskDisciplineTrend.Build(source);
        if (history.Rolling.Count == 0)
            return Empty($"Rolling MAE violation tracking needs at least {RiskDisciplineTrend.RollingWindow} completed trades.");

        var points = history.Rolling
            .Where(point => point.HasCompleteRiskData && point.MaeViolationRate.HasValue)
            .ToArray();
        if (points.Length == 0)
            return Empty("Rolling MAE violation tracking needs MAE and initial-risk data for every trade in the window.");

        var labels = points.Select(point => DateTimeLabel(point.ExitUtc)).ToArray();
        var customdata = points
            .Select(point => new object?[] { point.WindowTradeCount, point.RiskQualifiedTradeCount })
            .ToArray();
        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        layout["yaxis2"] = new Dictionary<string, object?>
        {
            ["title"] = new { text = "violations (count)", font = new { color = Muted, size = 11 } },
            ["overlaying"] = "y",
            ["side"] = "right",
            ["rangemode"] = "tozero",
            ["showgrid"] = false,
            ["automargin"] = true,
            ["tickfont"] = new { color = Muted, size = 11 }
        };
        SetAxis(layout, "xaxis", "exit date", date: true);
        SetAxis(layout, "yaxis", "MAE violation rate (%)", percent: true);
        SetRange(layout, "yaxis", new[] { 0, 100 });
        return Plotly("Rolling MAE violations", new object[]
        {
            new
            {
                type = "bar",
                x = labels,
                y = points.Select(point => PercentValue(point.MaeViolationRate)).ToArray(),
                name = "Violation rate",
                marker = new { color = Red, opacity = .78 },
                customdata,
                hovertemplate = "%{x|%b %-d, %Y}<br>Violation rate: %{y:.1f}%<br>%{customdata[0]}-trade window · %{customdata[1]} qualified trades<extra></extra>"
            },
            new
            {
                type = "scatter",
                x = labels,
                y = points.Select(point => point.MaeViolationCount).ToArray(),
                yaxis = "y2",
                mode = "lines+markers",
                name = "Violation count",
                line = new { color = Gold, width = 1.8 },
                marker = new { color = Gold, size = 5 },
                customdata,
                hovertemplate = "%{x|%b %-d, %Y}<br>Violations: %{y}<br>%{customdata[0]}-trade window · %{customdata[1]} qualified trades<extra></extra>"
            }
        }, layout);
    }

    public static string TearSheetRiskDisciplineMonthly(IEnumerable<Trade> source, string? timeZoneId = null)
    {
        var history = RiskDisciplineTrend.Build(source, timeZoneId);
        if (history.Monthly.Count == 0)
            return Empty("Monthly Risk Discipline tracking appears after the first completed trade.");

        var labels = history.Monthly.Select(point => point.Month.ToString("yyyy-MM", CultureInfo.InvariantCulture)).ToArray();
        var customdata = history.Monthly
            .Select(point => new object?[]
            {
                point.TradeCount,
                point.WinnerCount,
                point.RiskQualifiedTradeCount,
                point.RiskQualifiedWinnerCount,
                PercentValue(point.RescueRate),
                point.WinnerMaeP90R,
                point.MaeViolationCount
            })
            .ToArray();
        var layout = CartesianLayout("x unified");
        layout["showlegend"] = true;
        layout["legend"] = new { x = .02, y = .98 };
        layout["yaxis2"] = new Dictionary<string, object?>
        {
            ["title"] = new { text = "completed trades", font = new { color = Muted, size = 11 } },
            ["overlaying"] = "y",
            ["side"] = "right",
            ["rangemode"] = "tozero",
            ["showgrid"] = false,
            ["automargin"] = true,
            ["tickfont"] = new { color = Muted, size = 11 }
        };
        SetAxis(layout, "xaxis", "month");
        if (layout["xaxis"] is Dictionary<string, object?> monthAxis)
            monthAxis["type"] = "category";
        SetAxis(layout, "yaxis", "Risk Discipline score");
        SetRange(layout, "yaxis", new[] { 0, 100 });
        return Plotly("Monthly Risk Discipline score", new object[]
        {
            new
            {
                type = "bar",
                x = labels,
                y = history.Monthly.Select(point => point.TradeCount).ToArray(),
                yaxis = "y2",
                name = "Completed trades",
                marker = new { color = Purple, opacity = .55 },
                customdata,
                hovertemplate = "%{x}<br>Completed trades: %{y}<br>Winners: %{customdata[1]}<br>Risk data: %{customdata[2]}/%{customdata[0]} trades · %{customdata[3]}/%{customdata[1]} winners<br>Rescue rate: %{customdata[4]:.1f}%<br>Winner MAE P90: %{customdata[5]:.2f}R<br>MAE violations: %{customdata[6]}<extra></extra>"
            },
            new
            {
                type = "scatter",
                x = labels,
                y = history.Monthly.Select(point => point.Score).ToArray(),
                mode = "lines+markers",
                name = "Risk Discipline score",
                connectgaps = false,
                line = new { color = Blue, width = 1.8 },
                marker = new { color = Blue, size = 6 },
                customdata,
                hovertemplate = "%{x}<br>Risk Discipline score: %{y:.0f}/100<br>%{customdata[0]} completed trades · %{customdata[1]} winners<extra></extra>"
            }
        }, layout);
    }

    private static decimal? PercentValue(decimal? value) => value.HasValue ? value.Value * 100m : null;

    private static string DateTimeLabel(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
