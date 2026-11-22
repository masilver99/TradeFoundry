using TradeFoundry.Core;

namespace TradeFoundry.Services;

/// <summary>One outcome that tags can be measured against: a completed trade, or a trading day.</summary>
public sealed record TagObservation
{
    public required string Key { get; init; }
    public required DateOnly Date { get; init; }
    public required DateTimeOffset Order { get; init; }
    public required decimal NetPnl { get; init; }
    /// <summary>Net P&amp;L divided by risk. For a day, the sum, and only when every trade has risk.</summary>
    public decimal? NetR { get; init; }
    /// <summary>The furthest the trade got, in R: the greater of its MFE and its gross exit.</summary>
    public decimal? ReachedR { get; init; }
    /// <summary>Position MAE for a trade; the worst single trade's for a day.</summary>
    public decimal? Mae { get; init; }
    public decimal? Mfe { get; init; }
    public IReadOnlySet<Guid> Tags { get; init; } = new HashSet<Guid>();
    /// <summary>For a trade, the tags on its day. For a day, the same as <see cref="Tags"/>.</summary>
    public IReadOnlySet<Guid> DayTags { get; init; } = new HashSet<Guid>();
    public Trade? Trade { get; init; }
    public int TradeCount { get; init; } = 1;
}

public sealed record TagEstimate(decimal? Mean, decimal? Lower, decimal? Upper, int Count, int Days);

public sealed record TagSample
{
    public int Count { get; init; }
    public int Days { get; init; }
    public int Wins { get; init; }
    public decimal? WinRate { get; init; }
    public ProbabilityInterval WinRange { get; init; } = new(null, null, "Wilson 95%");
    public decimal? BreakevenWinRate { get; init; }
    public decimal TotalPnl { get; init; }
    public TagEstimate Pnl { get; init; } = new(null, null, null, 0, 0);
    public TagEstimate R { get; init; } = new(null, null, null, 0, 0);
    public decimal RiskCoverage => Count == 0 ? 0m : (decimal)R.Count / Count;
    public decimal? ProfitFactor { get; init; }
    public decimal? Payoff { get; init; }
    public decimal? AverageMae { get; init; }
    public decimal? AverageMfe { get; init; }
    public int ClosedAtOneR { get; init; }
    public int ReachSample { get; init; }
    public int ReachedOneR { get; init; }
    public int ReachedTwoR { get; init; }

    public TagEstimate Value(bool useR) => useR ? R : Pnl;
}

public static class TagStatistics
{
    private const double Z975 = 1.959963984540054;

    // Two-sided 95% Student t critical values for 1 to 30 degrees of freedom.
    private static readonly double[] T975 =
    [
        12.706, 4.303, 3.182, 2.776, 2.571, 2.447, 2.365, 2.306, 2.262, 2.228,
        2.201, 2.179, 2.160, 2.145, 2.131, 2.120, 2.110, 2.101, 2.093, 2.086,
        2.080, 2.074, 2.069, 2.064, 2.060, 2.056, 2.052, 2.048, 2.045, 2.042
    ];

    public static TagSample Measure(IReadOnlyList<TagObservation> items)
    {
        if (items.Count == 0) return new TagSample();

        var wins = items.Where(item => item.NetPnl > 0m).ToArray();
        var losses = items.Where(item => item.NetPnl < 0m).ToArray();
        var gain = wins.Sum(item => item.NetPnl);
        var loss = -losses.Sum(item => item.NetPnl);
        var payoff = wins.Length > 0 && losses.Length > 0 ? gain / wins.Length / (loss / losses.Length) : (decimal?)null;
        var reach = items.Where(item => item.ReachedR.HasValue).Select(item => item.ReachedR!.Value).ToArray();
        return new TagSample
        {
            Count = items.Count,
            Days = items.Select(item => item.Date).Distinct().Count(),
            Wins = wins.Length,
            WinRate = (decimal)wins.Length / items.Count,
            WinRange = ProbabilityMath.WilsonInterval(wins.Length, items.Count),
            BreakevenWinRate = payoff is { } ratio ? 1m / (1m + ratio) : null,
            TotalPnl = items.Sum(item => item.NetPnl),
            Pnl = Estimate(items.Select(item => (item.Date, item.NetPnl)).ToArray()),
            R = Estimate(items.Where(item => item.NetR.HasValue).Select(item => (item.Date, item.NetR!.Value)).ToArray()),
            ProfitFactor = loss > 0m ? gain / loss : null,
            Payoff = payoff,
            AverageMae = Average(items.Select(item => item.Mae)),
            AverageMfe = Average(items.Select(item => item.Mfe)),
            ClosedAtOneR = items.Count(item => item.NetR >= 1m),
            ReachSample = reach.Length,
            ReachedOneR = reach.Count(value => value >= 1m),
            ReachedTwoR = reach.Count(value => value >= 2m)
        };
    }

    /// <summary>
    /// The mean and its 95% range. Trades from the same day tend to win and lose together, so each day is
    /// one cluster: twelve trades on one day are much weaker evidence than twelve on twelve days.
    /// </summary>
    public static TagEstimate Estimate(IReadOnlyList<(DateOnly Date, decimal Value)> values)
    {
        if (values.Count == 0) return new TagEstimate(null, null, null, 0, 0);
        var mean = values.Average(item => item.Value);
        var days = values.GroupBy(item => item.Date).ToArray();
        if (days.Length < 2) return new TagEstimate(mean, null, null, values.Count, days.Length);

        var squares = days.Sum(day =>
        {
            var residual = (double)day.Sum(item => item.Value - mean);
            return residual * residual;
        });
        var error = Math.Sqrt(days.Length / (days.Length - 1d) * squares) / values.Count;
        var margin = (decimal)(Critical(days.Length - 1) * error);
        return new TagEstimate(mean, mean - margin, mean + margin, values.Count, days.Length);
    }

    internal static double Critical(int degrees)
    {
        if (degrees <= T975.Length) return T975[degrees - 1];
        var z3 = Z975 * Z975 * Z975;
        var z5 = z3 * Z975 * Z975;
        return Z975 + (z3 + Z975) / (4d * degrees) + (5d * z5 + 16d * z3 + 3d * Z975) / (96d * degrees * degrees);
    }

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var known = values.OfType<decimal>().ToArray();
        return known.Length == 0 ? null : known.Average();
    }
}
