using System.Globalization;
using System.Text.RegularExpressions;

namespace TradeFoundry.Core;

/// <summary>
/// Sierra Chart can export a fill price as a fixed-point integer-like value
/// (for example, 764725 for 7647.25) while the order source text contains the
/// display price. Keep the raw row unchanged and normalize only the values
/// used for calculations.
/// </summary>
public static class SierraPriceNormalizer
{
    private static readonly decimal[] CandidateScales = [1m, 10m, 100m, 1_000m, 10_000m, 100_000m, 1_000_000m];
    private static readonly Regex SourcePricePattern = new(
        @"(?:Bid|Ask|Last|Requested\s+Price)\s*:\s*(?<value>[+-]?(?:\d+(?:\.\d*)?|\.\d+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static decimal Normalize(decimal rawPrice, string symbol, string orderActionSource)
        => rawPrice / DetermineScale(rawPrice, symbol, orderActionSource);

    public static decimal DetermineScale(decimal rawPrice, string symbol, string orderActionSource)
    {
        if (rawPrice == 0m) return 1m;

        var sourcePrices = ExtractSourcePrices(orderActionSource);
        if (sourcePrices.Count > 0)
        {
            var rawMagnitude = Math.Abs(rawPrice);
            var bestScale = 1m;
            var bestRelativeError = decimal.MaxValue;
            foreach (var scale in CandidateScales)
            {
                var normalized = rawPrice / scale;
                var closest = sourcePrices.Min(candidate => Math.Abs(normalized - candidate));
                var denominator = Math.Max(1m, rawMagnitude / scale);
                var relativeError = closest / denominator;
                if (relativeError < bestRelativeError)
                {
                    bestRelativeError = relativeError;
                    bestScale = scale;
                }
            }

            // The source text is normally an exact match. A small tolerance
            // also covers display rounding without changing ordinary prices.
            var normalizedMagnitude = rawMagnitude / bestScale;
            var tolerance = Math.Max(0.01m, normalizedMagnitude * 0.00001m);
            var sourceDistance = sourcePrices.Min(candidate => Math.Abs(rawPrice / bestScale - candidate));
            if (sourceDistance <= tolerance)
                return bestScale;
        }

        return FallbackScale(rawPrice, symbol);
    }

    private static List<decimal> ExtractSourcePrices(string source)
    {
        var values = new List<decimal>();
        if (string.IsNullOrWhiteSpace(source)) return values;
        foreach (Match match in SourcePricePattern.Matches(source))
        {
            if (decimal.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                values.Add(value);
        }
        return values;
    }

    private static decimal FallbackScale(decimal rawPrice, string symbol)
    {
        var magnitude = Math.Abs(rawPrice);
        var root = InstrumentCatalog.ExtractRoot(symbol);

        // Currency futures are quoted near 1.0; a large value is unambiguous.
        if (root.Equals("6E", StringComparison.OrdinalIgnoreCase) && magnitude >= 10_000m)
            return 100_000m;

        // Equity-index futures normally trade below 100,000. Sierra's
        // fixed-point representation therefore stands out above this bound.
        if (root is "ES" or "MES" or "NQ" or "MNQ" or "YM" or "MYM" or "RTY" or "M2K" && magnitude >= 100_000m)
            return 100m;

        // Energy and micro-energy contracts are normally well below 1,000;
        // use the same conservative bound for an export with no source text.
        if (root is "CL" or "MCL" && magnitude >= 1_000m)
            return 100m;

        // Gold is commonly quoted above 1,000, so only infer fixed-point form
        // at a much larger magnitude where a raw quote is implausible.
        if (root is "GC" or "MGC" && magnitude >= 100_000m)
            return 100m;

        return 1m;
    }
}
