using System.Text.RegularExpressions;

namespace TradeFoundry.Core;

/// <summary>
/// Small, deliberately conservative catalog used for deterministic futures math.
/// Unknown instruments remain usable and fall back to one currency unit per point;
/// the UI can still show raw points and the journal can be updated later.
/// </summary>
public static class InstrumentCatalog
{
    private static readonly InstrumentSpec[] Specs =
    [
        new() { Root = "MES", TickSize = 0.25m, PointValue = 5m },
        new() { Root = "MNQ", TickSize = 0.25m, PointValue = 2m },
        new() { Root = "MYM", TickSize = 1m, PointValue = 0.5m },
        new() { Root = "M2K", TickSize = 0.1m, PointValue = 5m },
        new() { Root = "MCL", TickSize = 0.01m, PointValue = 100m },
        new() { Root = "MGC", TickSize = 0.1m, PointValue = 10m },
        new() { Root = "ES", TickSize = 0.25m, PointValue = 50m },
        new() { Root = "NQ", TickSize = 0.25m, PointValue = 20m },
        new() { Root = "YM", TickSize = 1m, PointValue = 5m },
        new() { Root = "RTY", TickSize = 0.1m, PointValue = 50m },
        new() { Root = "CL", TickSize = 0.01m, PointValue = 1_000m },
        new() { Root = "GC", TickSize = 0.1m, PointValue = 100m },
        new() { Root = "6E", TickSize = 0.00005m, PointValue = 125_000m }
    ];

    public static IReadOnlyList<InstrumentSpec> Defaults => Specs;

    public static InstrumentSpec Resolve(string symbol)
    {
        var root = ExtractRoot(symbol);
        return Specs.FirstOrDefault(x => string.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase))
            ?? new InstrumentSpec { Root = root, TickSize = 0m, PointValue = 1m };
    }

    public static string ExtractRoot(string symbol)
    {
        var value = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        value = value.Replace("[SIM]", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("_FUT", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("FUT_", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim('_', ' ', '-');

        // Sierra symbols commonly end in a month code and two-digit year.
        var match = Regex.Match(value, @"^(?<root>[A-Z0-9]+?)(?:[FGHJKMNQUVXZ]\d{1,4})(?:_|$)", RegexOptions.CultureInvariant);
        if (match.Success)
            return match.Groups["root"].Value;

        var separator = value.IndexOf('_');
        if (separator > 0)
            value = value[..separator];

        return value;
    }
}
