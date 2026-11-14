namespace TradeFoundry.Publishing;

public static class PublicationAssets
{
    public const string Favicon = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\"><rect width=\"64\" height=\"64\" rx=\"14\" fill=\"#17644e\"/><polyline points=\"12,44 25,31 35,38 52,18\" fill=\"none\" stroke=\"#fff\" stroke-width=\"6\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";

    public static string? Script(string name)
    {
        if (name is not ("trade-execution-drawing.js" or "publication-charts.js" or "publication-reader.js" or "lightweight-charts.standalone.production.js")) return null;
        using var stream = typeof(PublicationAssets).Assembly.GetManifestResourceStream($"TradeFoundry.Publishing.Contracts.Assets.{name}")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
