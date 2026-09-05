namespace TradeFoundry.Core;

public sealed class BenchmarkOptions
{
    public bool AutomaticRefresh { get; set; } = true;
    public string DefaultSymbol { get; set; } = "^GSPC";
    public int RefreshIntervalMinutes { get; set; } = 1_440;
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int StartPaddingDays { get; set; } = 7;
    public int EndPaddingDays { get; set; } = 2;
    public bool UseAdjustedClose { get; set; } = true;
    public string YahooChartBaseUrl { get; set; } = "https://query2.finance.yahoo.com/";
}
