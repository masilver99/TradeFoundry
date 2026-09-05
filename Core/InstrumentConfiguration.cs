using System.Text.RegularExpressions;

namespace TradeFoundry.Core;

public sealed class InstrumentDefinition
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public decimal? DefaultCommission { get; init; }
    public decimal PointValue { get; init; }
    public decimal TickSize { get; init; }
}

public sealed class InstrumentMapping
{
    public Guid Id { get; init; }
    public string ApplicationKey { get; init; } = string.Empty;
    public string MatchRegex { get; init; } = string.Empty;
    public string InstrumentCode { get; init; } = string.Empty;
    public decimal? CommissionOverride { get; init; }
    public int Position { get; init; }
}

public sealed class InstrumentResolution
{
    public string InstrumentCode { get; init; } = string.Empty;
    public decimal PointValue { get; init; }
    public decimal TickSize { get; init; }
    public decimal? CommissionPerContract { get; init; }
    public bool IsMapped { get; init; }
}

/// <summary>
/// Resolves an imported source symbol to a configured instrument. Mappings are
/// evaluated in their saved order, then the extracted symbol root and built-in
/// catalog provide a safe fallback for records without a source-specific match.
/// </summary>
public sealed class InstrumentConfiguration
{
    private static readonly RegexOptions MappingRegexOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static readonly TimeSpan MappingRegexTimeout = TimeSpan.FromMilliseconds(100);
    private readonly IReadOnlyDictionary<string, InstrumentDefinition> _definitions;
    private readonly IReadOnlyList<InstrumentMapping> _mappings;

    public static InstrumentConfiguration Empty { get; } = new(Array.Empty<InstrumentDefinition>(), Array.Empty<InstrumentMapping>());

    public InstrumentConfiguration(IReadOnlyList<InstrumentDefinition> definitions, IReadOnlyList<InstrumentMapping> mappings)
    {
        _definitions = definitions
            .Where(x => !string.IsNullOrWhiteSpace(x.Code))
            .GroupBy(x => NormalizeCode(x.Code), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        _mappings = mappings
            .Where(x => !string.IsNullOrWhiteSpace(x.ApplicationKey) && !string.IsNullOrWhiteSpace(x.MatchRegex))
            .OrderBy(x => x.ApplicationKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Position)
            .ThenBy(x => x.Id)
            .ToArray();
    }

    public InstrumentResolution Resolve(string applicationKey, string rawSymbol)
    {
        var symbol = (rawSymbol ?? string.Empty).Trim();
        var mapping = FindMapping(applicationKey, symbol);
        var code = NormalizeCode(mapping?.InstrumentCode ?? InstrumentCatalog.ExtractRoot(symbol));

        if (!_definitions.TryGetValue(code, out var definition))
        {
            var builtIn = InstrumentCatalog.Resolve(code);
            return new InstrumentResolution
            {
                InstrumentCode = code,
                PointValue = builtIn.PointValue,
                TickSize = builtIn.TickSize,
                CommissionPerContract = mapping?.CommissionOverride,
                IsMapped = mapping is not null
            };
        }

        return new InstrumentResolution
        {
            InstrumentCode = definition.Code,
            PointValue = definition.PointValue > 0m ? definition.PointValue : InstrumentCatalog.Resolve(definition.Code).PointValue,
            TickSize = definition.TickSize > 0m ? definition.TickSize : InstrumentCatalog.Resolve(definition.Code).TickSize,
            CommissionPerContract = mapping?.CommissionOverride ?? definition.DefaultCommission,
            IsMapped = mapping is not null
        };
    }

    private InstrumentMapping? FindMapping(string applicationKey, string symbol)
    {
        if (string.IsNullOrWhiteSpace(applicationKey) || string.IsNullOrWhiteSpace(symbol)) return null;

        foreach (var mapping in _mappings.Where(x => x.ApplicationKey.Equals(applicationKey, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (Regex.IsMatch(symbol, mapping.MatchRegex, MappingRegexOptions, MappingRegexTimeout))
                    return mapping;
            }
            catch (ArgumentException)
            {
                // Invalid or pathological legacy rows must not stop an import.
            }
            catch (RegexMatchTimeoutException)
            {
                // Invalid or pathological legacy rows must not stop an import.
            }
        }

        return null;
    }

    public static bool IsValidMatchRegex(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        try
        {
            _ = new Regex(pattern, MappingRegexOptions, MappingRegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string NormalizeCode(string value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
