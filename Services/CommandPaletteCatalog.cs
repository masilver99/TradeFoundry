using System.Globalization;
using System.Text;
using System.Text.Json;
using TradeFoundry.Core;

namespace TradeFoundry.Services;

public sealed record CommandPaletteItem(
    string Type,
    string Title,
    string Category,
    string Route,
    string? TargetId,
    string[] Aliases,
    string[] Keywords);

public static class CommandPaletteCatalog
{
    private sealed record ChartEntry(string Section, string Title, string[] Aliases, string[] Keywords);

    private static readonly Lazy<IReadOnlyList<CommandPaletteItem>> Catalog = new(BuildCatalog);

    public static IReadOnlyList<CommandPaletteItem> Items => Catalog.Value;

    public static string ToJson() => JsonSerializer.Serialize(Items, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static string Anchor(string section, string title) => $"tf-command-{Slug(section)}-{Slug(title)}";

    private static IReadOnlyList<CommandPaletteItem> BuildCatalog()
    {
        var items = new List<CommandPaletteItem>();
        var targetCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Page(string title, string route, params string[] aliases) =>
            items.Add(new("navigate", title, "Navigation", route, null, aliases, Array.Empty<string>()));
        void Section(string title, string route, string section, params string[] aliases) =>
            items.Add(new("navigate", title, "Section", route, section, aliases, Array.Empty<string>()));
        void Card(string title, string category, string route, string section, string[] aliases, string[] keywords)
        {
            var normalizedRoute = route.StartsWith('/') ? route : $"/{route}";
            var targetId = Anchor(section, title);
            var targetKey = $"{normalizedRoute}#{targetId}";
            var count = targetCounts.GetValueOrDefault(targetKey) + 1;
            targetCounts[targetKey] = count;
            if (count > 1) targetId = $"{targetId}-{count}";
            items.Add(new("navigate", title, category, normalizedRoute, targetId, aliases, keywords));
        }

        Page("Overview", "/overview", "Home", "Dashboard");
        Page("Trade Ledger", "/trades", "Trades", "Trade search", "Ledger");
        Page("Account", "/account", "Balance", "Transactions");
        Page("Analysis", "/analytics", "Analytics");
        Page("Indicators", "/indicators", "Metrics", "Statistics");
        Page("Broker Costs", "/broker-comparison", "Commissions", "Fees", "Broker comparison");
        Page("Daybook Review", "/review", "Daybook", "Daily review", "Journal notes", "Review");
        Page("Daily Journal", "/daily-journal", "Journal entries", "Journal feed", "Daily notes");
        Page("Imports", "/imports", "Import data", "Import history");
        Page("Settings", "/settings", "Configuration", "Preferences");

        Section("SC Trade Statistics", "/analytics", "sc-trade-statistics", "Sierra Chart", "Trade statistics");
        Section("History", "/analytics", "history", "Calendar", "Period summary");
        Section("Period Breakdown", "/analytics", "period-breakdown", "Day of week", "Entry hour");
        Section("Performance", "/analytics", "performance", "Equity", "Returns");
        Section("Risk & Stability", "/analytics", "risk", "Risk", "Drawdown");
        Section("Edge Persistence", "/analytics", "edge-persistence", "Edge", "Consistency");
        Section("Trade Quality", "/analytics", "trade-quality", "MFE", "MAE", "Excursion");
        Section("Timing & Sizing", "/analytics", "timing-sizing", "Session", "Direction");
        Section("Execution", "/analytics", "execution", "Orders", "Exit types");
        Section("Indicator Key", "/indicators", "indicator-key", "Key metrics");

        var charts = new[]
        {
            new ChartEntry("overview-charts", "Equity Curve", ["Equity", "Cumulative P&L", "Balance"], ["overview", "account growth"]),
            new ChartEntry("overview-charts", "Daily result", ["Daily P&L", "Daily results"], ["overview", "day", "profit loss"]),
            new ChartEntry("history", "Monthly net P&L by day", ["P&L Calendar", "Calendar", "Heatmap"], ["history", "daily", "monthly", "profit loss"]),
            new ChartEntry("history", "Worst trade excursion by day", ["MAE Calendar", "Daily MAE", "Worst MAE"], ["history", "adverse excursion", "risk"]),
            new ChartEntry("period-breakdown", "By Day of Week", ["Day of week"], ["period breakdown", "weekday", "performance"]),
            new ChartEntry("period-breakdown", "By Entry Hour", ["Entry time"], ["period breakdown", "hour", "timing"]),
            new ChartEntry("period-breakdown", "Weekly P&L", ["Weekly results"], ["period breakdown", "profit loss"]),
            new ChartEntry("period-breakdown", "Monthly P&L", ["Monthly results"], ["period breakdown", "profit loss"]),
            new ChartEntry("performance-equity", "Adjusted and raw balance", ["Equity Curve", "Account balance"], ["performance", "equity", "cumulative pnl"]),
            new ChartEntry("performance-equity", "Trade P&L waterfall", ["P&L waterfall", "Trade waterfall"], ["performance", "profit loss", "completed trade"]),
            new ChartEntry("performance-costs", "Gross, net, and cumulative fees", ["Fee drag", "Commissions"], ["costs", "exchange", "NFA", "clearing"]),
            new ChartEntry("performance-periodic", "Daily gross P&L", ["Daily P&L"], ["performance", "periodic", "profit loss"]),
            new ChartEntry("performance-periodic", "Monthly net P&L", ["Monthly returns"], ["performance", "periodic", "profit loss"]),
            new ChartEntry("performance-periodic", "Yearly strategy vs benchmark", ["Annual returns"], ["performance", "yearly", "comparison"]),
            new ChartEntry("performance-periodic", "Distribution of monthly returns", ["Monthly return distribution", "Histogram"], ["performance", "risk", "distribution"]),
            new ChartEntry("performance-periodic", "Daily active returns", ["Active returns"], ["performance", "benchmark", "daily"]),
            new ChartEntry("performance-periodic", "Monthly return heatmap", ["Return heatmap", "Calendar returns"], ["performance", "monthly", "calendar"]),
            new ChartEntry("performance-benchmark", "Strategy vs benchmark returns", ["Benchmark comparison"], ["performance", "relative returns", "comparison"]),
            new ChartEntry("performance-benchmark", "Monte Carlo simulated paths", ["Monte Carlo", "Simulated equity paths"], ["performance", "simulation", "risk"]),
            new ChartEntry("risk-discipline", "Rolling rescue dependency", ["Rescue rate over time"], ["risk", "discipline", "rolling"]),
            new ChartEntry("risk-discipline", "Rolling winner heat and MAE", ["Winner heat"], ["risk", "discipline", "adverse excursion"]),
            new ChartEntry("risk-discipline", "Rolling MAE violations", ["MAE breaches"], ["risk", "discipline", "risk target"]),
            new ChartEntry("risk-discipline", "Monthly Risk Discipline score", ["Risk Discipline by month"], ["risk", "discipline", "monthly"]),
            new ChartEntry("risk-drawdown", "Underwater net P&L", ["Drawdown Underwater", "Drawdown"], ["risk", "drawdown", "equity decline"]),
            new ChartEntry("risk-drawdown", "Worst five drawdown periods", ["Largest drawdowns"], ["risk", "drawdown", "recovery"]),
            new ChartEntry("risk-drawdown", "Drawdown duration and recovery", ["Recovery time"], ["risk", "underwater", "recovery"]),
            new ChartEntry("risk-rolling", "Expectancy, win rate, and Sharpe", ["Rolling analytics", "Rolling Sharpe"], ["risk", "rolling", "edge"]),
            new ChartEntry("risk-rolling", "Rolling six-month volatility", ["Rolling volatility"], ["risk", "standard deviation"]),
            new ChartEntry("risk-rolling", "Rolling six-month Sharpe", ["Sharpe ratio over time"], ["risk adjusted return", "volatility"]),
            new ChartEntry("risk-rolling", "Rolling six-month Sortino", ["Sortino ratio over time"], ["downside risk", "returns"]),
            new ChartEntry("risk-rolling", "Win rate over time", ["Monthly win rate"], ["risk", "rolling", "performance"]),
            new ChartEntry("risk-distributions", "Daily P&L distribution", ["Daily return distribution"], ["risk", "histogram", "outcomes"]),
            new ChartEntry("risk-distributions", "Trade P&L distribution", ["Trade return distribution"], ["risk", "histogram", "outcomes"]),
            new ChartEntry("risk-distributions", "Trade P&L by dollar range", ["Trade P&L range", "Dollar range"], ["risk", "wins", "losses"]),
            new ChartEntry("risk-distributions", "Winner profit concentration", ["Profit concentration", "Largest winners"], ["risk", "outliers", "concentration"]),
            new ChartEntry("risk-distributions", "R-multiple distribution", ["R distribution"], ["risk", "initial risk", "position sizing"]),
            new ChartEntry("edge-persistence-rolling", "Rolling expectancy", ["Expectancy over time"], ["edge persistence", "gross pnl"]),
            new ChartEntry("edge-persistence-rolling", "Profit factor and win rate", ["Rolling profit factor"], ["edge persistence", "quality"]),
            new ChartEntry("edge-persistence-rolling", "Median and tail MAE", ["MAE percentile"], ["edge persistence", "adverse excursion"]),
            new ChartEntry("edge-persistence-rolling", "Average and median loss", ["Loss size"], ["edge persistence", "loss risk"]),
            new ChartEntry("edge-persistence-blocks", "P&L by non-overlapping block", ["Independent trade blocks"], ["edge persistence", "consistency", "samples"]),
            new ChartEntry("edge-persistence-risk", "MAE by rolling window", ["MAE trend"], ["edge persistence", "risk stability"]),
            new ChartEntry("edge-persistence-risk", "Loss size by rolling window", ["Rolling losses"], ["edge persistence", "risk stability"]),
            new ChartEntry("quality-excursion", "MAE vs MFE", ["MFE / MAE Scatter", "Excursion scatter"], ["trade quality", "favorable adverse excursion", "heat"]),
            new ChartEntry("quality-excursion", "Heat taken by winning trades", ["Winner MAE"], ["trade quality", "adverse excursion", "winners"]),
            new ChartEntry("quality-excursion", "MAE and MFE percentile profile", ["Excursion percentiles"], ["trade quality", "distribution", "favorable adverse"]),
            new ChartEntry("quality-holding", "Time in trade vs gross P&L", ["Holding time"], ["trade quality", "duration", "performance"]),
            new ChartEntry("quality-holding", "Expectancy, win rate, and MFE capture", ["Holding efficiency"], ["trade quality", "exit efficiency"]),
            new ChartEntry("quality-holding", "MFE capture and profit left on table", ["Exit efficiency"], ["trade quality", "favorable excursion"]),
            new ChartEntry("quality-process", "Next-trade expectancy and win rate", ["Streak state"], ["trade quality", "wins", "losses"]),
            new ChartEntry("quality-process", "Outcome mix", ["Win loss mix"], ["trade quality", "distribution"]),
            new ChartEntry("timing-context", "Direction mix", ["Long short mix"], ["timing", "sizing", "position direction"]),
            new ChartEntry("timing-context", "Session mix", ["Trading session"], ["timing", "market hours"]),
            new ChartEntry("execution-exits", "Average P&L and win rate by exit type", ["Exit classification"], ["execution", "target", "stop", "exit"])
        };
        foreach (var chart in charts)
            Card(chart.Title, "Chart", chart.Section == "overview-charts" ? "overview" : "analytics", chart.Section, chart.Aliases, chart.Keywords);

        var metricSet = TearSheetMetrics.Build(
            Array.Empty<Trade>(),
            Array.Empty<OrderEvent>(),
            Array.Empty<AccountBalanceEvent>(),
            Array.Empty<BenchmarkPoint>(),
            null,
            "UTC",
            "USD");

        foreach (var metric in metricSet.Key)
            Card(metric.Label, "Metric", "indicators", "indicator-key", MetricAliases(metric.Label), ["key indicators", "statistics"]);
        foreach (var group in metricSet.Groups)
        foreach (var metric in group.Indicators)
            Card(metric.Label, "Metric", "indicators", group.Id, MetricAliases(metric.Label), [group.Title, group.Kicker, "indicator", "statistics"]);
        Card("R-squared", "Metric", "indicators", "indicators-benchmark", ["R²", "Equity R-squared", "Fit"], ["benchmark", "equity fit", "correlation"]);
        foreach (var metric in metricSet.RiskStability)
            Card(metric.Label, "Metric", "analytics", "risk-metrics", MetricAliases(metric.Label), ["risk stability", "drawdown", "risk-adjusted"]);
        foreach (var metric in metricSet.RiskDiscipline.Indicators)
            Card(metric.Label, "Metric", "analytics", "risk-discipline", MetricAliases(metric.Label), ["risk discipline", "MAE", "initial risk"]);
        Card("Risk Discipline Score", "Metric", "analytics", "risk-discipline", ["Risk score", "Risk discipline"], ["MAE", "risk data coverage"]);
        Card("Edge Persistence Score", "Metric", "analytics", "edge-persistence-summary", ["Edge score", "Consistency score"], ["edge persistence", "evidence strength"]);

        foreach (var metric in new[] { "Account balance", "Net P&L", "Points", "Trading days", "Closed trades", "Win rate", "Profit factor", "Fees" })
            Card(metric, "Metric", "overview", "overview-metrics", MetricAliases(metric), ["overview", "dashboard"]);

        return items;
    }

    private static string[] MetricAliases(string label) => label switch
    {
        "Sharpe" => ["Sharpe ratio", "risk adjusted return"],
        "Sortino" => ["Sortino ratio", "downside risk"],
        "Ulcer Index" => ["drawdown stress", "underwater risk"],
        "Profit Factor" => ["gross profit loss ratio", "PF"],
        "Expectancy" => ["average trade", "expected value"],
        "Max Drawdown" => ["largest equity decline", "MDD"],
        "CVaR 95%" => ["conditional value at risk", "tail loss"],
        "MAE Percentiles" => ["maximum adverse excursion", "heat taken"],
        "MFE Percentiles" => ["maximum favorable excursion", "opportunity"],
        _ => Array.Empty<string>()
    };

    private static string Slug(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        var separator = false;
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            var lower = char.ToLowerInvariant(character);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                if (separator && builder.Length > 0) builder.Append('-');
                builder.Append(lower);
                separator = false;
            }
            else
            {
                separator = true;
            }
        }
        return builder.ToString().Trim('-');
    }
}
