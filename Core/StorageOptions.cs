namespace TradeFoundry.Core;

public sealed class StorageOptions
{
    public string DataDirectory { get; set; } = ".tradefoundry-data";
    public string DatabaseFileName { get; set; } = "journal.db";
}
