namespace TradeFoundry.Core;

public sealed class McpOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "http://127.0.0.1:5081";
    public int RequestsPerMinute { get; set; } = 60;
    public int MaxConcurrentAnalysis { get; set; } = 4;
}
