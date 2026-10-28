namespace TradeFoundry.Publishing;

public static class PublicationAssets
{
    public static string? Script(string name)
    {
        if (name is not ("trade-execution-drawing.js" or "publication-charts.js" or "lightweight-charts.standalone.production.js")) return null;
        using var stream = typeof(PublicationAssets).Assembly.GetManifestResourceStream($"TradeFoundry.Publishing.Contracts.Assets.{name}")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
