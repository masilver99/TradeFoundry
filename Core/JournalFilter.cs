using System.Globalization;

namespace TradeFoundry.Core;

/// <summary>
/// The journal-wide browsing filter: an optional date period and a trade direction.
/// A preset is stored by name and turned into dates on each request, so a remembered
/// "This month" keeps following the calendar.
/// </summary>
public sealed record JournalFilter
{
    public const string PeriodKey = "period";
    public const string FromKey = "from";
    public const string ToKey = "to";
    public const string SideKey = "side";
    public static readonly IReadOnlyList<string> QueryKeys = new[] { PeriodKey, FromKey, ToKey, SideKey };

    public static readonly IReadOnlyList<(string Value, string Label)> Periods = new[]
    {
        ("all", "All dates"),
        ("this-month", "This month"),
        ("last-month", "Last month"),
        ("this-quarter", "This quarter"),
        ("this-year", "This year"),
        ("custom", "Custom range")
    };

    public static readonly JournalFilter None = new();

    /// <summary>One of <see cref="Periods"/>.</summary>
    public string Period { get; init; } = "all";

    /// <summary>Inclusive journal-local dates, only meaningful for the custom period.</summary>
    public DateOnly? CustomFrom { get; init; }
    public DateOnly? CustomTo { get; init; }

    /// <summary>all, long or short.</summary>
    public string Side { get; init; } = "all";

    public bool HasPeriod => Period != "all" && (Period != "custom" || CustomFrom.HasValue || CustomTo.HasValue);
    public bool HasSide => Side != "all";
    public bool IsActive => HasPeriod || HasSide;
    public int ActiveCount => (HasPeriod ? 1 : 0) + (HasSide ? 1 : 0);

    /// <summary>Reads query or cookie values. Unknown or malformed values fall back to "all"; a reversed custom range is swapped back.</summary>
    public static JournalFilter Parse(string? period, string? from, string? to, string? side)
    {
        var normalizedPeriod = (period ?? string.Empty).Trim().ToLowerInvariant();
        if (!Periods.Any(item => item.Value == normalizedPeriod)) normalizedPeriod = "all";
        var fromDate = ParseDate(from);
        var toDate = ParseDate(to);
        // Dates on their own (no period) mean a custom range, so a hand-written ?from= link works.
        if (normalizedPeriod == "all" && string.IsNullOrWhiteSpace(period) && (fromDate.HasValue || toDate.HasValue)) normalizedPeriod = "custom";
        if (normalizedPeriod != "custom")
        {
            fromDate = null;
            toDate = null;
        }
        else if (fromDate.HasValue && toDate.HasValue && fromDate > toDate)
        {
            (fromDate, toDate) = (toDate, fromDate);
        }

        var normalizedSide = (side ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedSide is not ("long" or "short")) normalizedSide = "all";
        return new JournalFilter { Period = normalizedPeriod, CustomFrom = fromDate, CustomTo = toDate, Side = normalizedSide };
    }

    public static DateOnly? ParseDate(string? value)
        => DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    /// <summary>The inclusive journal-local date range for <paramref name="today"/>. Either end can be open.</summary>
    public (DateOnly? From, DateOnly? To) DateRange(DateOnly today)
    {
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var quarterStart = new DateOnly(today.Year, (today.Month - 1) / 3 * 3 + 1, 1);
        return Period switch
        {
            "this-month" => (monthStart, monthStart.AddMonths(1).AddDays(-1)),
            "last-month" => (monthStart.AddMonths(-1), monthStart.AddDays(-1)),
            "this-quarter" => (quarterStart, quarterStart.AddMonths(3).AddDays(-1)),
            "this-year" => (new DateOnly(today.Year, 1, 1), new DateOnly(today.Year, 12, 31)),
            "custom" => (CustomFrom, CustomTo),
            _ => (null, null)
        };
    }

    public static DateOnly Today(string? timeZoneId) => DateOnly.FromDateTime(TimeZoneCatalog.Convert(DateTimeOffset.UtcNow, timeZoneId).DateTime);

    /// <summary>The journal-local day a trade belongs to: exit day when closed, otherwise entry day. Shared with daily review and the calendars.</summary>
    public static DateOnly TradeDate(Trade trade, string? timeZoneId) => TradeDate(trade, TimeZoneCatalog.Resolve(timeZoneId));

    public static DateOnly TradeDate(Trade trade, TimeZoneInfo timeZone)
    {
        var completed = trade.ExitUtc.HasValue && trade.Status.Equals("closed", StringComparison.OrdinalIgnoreCase);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(completed ? trade.ExitUtc!.Value : trade.EntryUtc, timeZone).DateTime);
    }

    public bool MatchesSide(Trade trade) => !HasSide || trade.Direction.Equals(Side, StringComparison.OrdinalIgnoreCase);

    public bool MatchesDate(DateOnly date, DateOnly today)
    {
        var (from, to) = DateRange(today);
        return (!from.HasValue || date >= from.Value) && (!to.HasValue || date <= to.Value);
    }

    public JournalFilterScope Scope(string? timeZoneId) => Scope(timeZoneId, Today(timeZoneId));

    public JournalFilterScope Scope(string? timeZoneId, DateOnly today)
    {
        var (from, to) = DateRange(today);
        return new JournalFilterScope(this, timeZoneId, from, to);
    }

    public IReadOnlyDictionary<string, string> ToQuery()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Period != "all") values[PeriodKey] = Period;
        if (Period == "custom" && CustomFrom.HasValue) values[FromKey] = CustomFrom.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Period == "custom" && CustomTo.HasValue) values[ToKey] = CustomTo.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (Side != "all") values[SideKey] = Side;
        return values;
    }

    public string ToQueryString() => string.Join("&", ToQuery().Select(item => $"{item.Key}={Uri.EscapeDataString(item.Value)}"));
}

/// <summary>A filter resolved against one journal's time zone and today's date.</summary>
public sealed class JournalFilterScope
{
    private readonly TimeZoneInfo _timeZone;

    public JournalFilterScope(JournalFilter filter, string? timeZoneId, DateOnly? from, DateOnly? to)
    {
        Filter = filter;
        _timeZone = TimeZoneCatalog.Resolve(timeZoneId);
        From = from;
        To = to;
        // Half-open UTC bounds: local midnight at the start of From, and local midnight after To.
        StartUtc = from.HasValue ? TimeZoneCatalog.FromLocal(from.Value.ToDateTime(TimeOnly.MinValue), timeZoneId).ToUniversalTime() : null;
        EndUtcExclusive = to.HasValue ? TimeZoneCatalog.FromLocal(to.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), timeZoneId).ToUniversalTime() : null;
    }

    public JournalFilter Filter { get; }
    public DateOnly? From { get; }
    public DateOnly? To { get; }
    public DateTimeOffset? StartUtc { get; }
    public DateTimeOffset? EndUtcExclusive { get; }
    public bool IsActive => Filter.IsActive;
    public bool HasDateBounds => From.HasValue || To.HasValue;
    public string? Side => Filter.HasSide ? Filter.Side : null;

    public bool IncludesDate(DateOnly date) => (!From.HasValue || date >= From.Value) && (!To.HasValue || date <= To.Value);

    public bool Includes(Trade trade) => Filter.MatchesSide(trade) && IncludesDate(JournalFilter.TradeDate(trade, _timeZone));

    public IReadOnlyList<Trade> Apply(IEnumerable<Trade> trades) => IsActive ? trades.Where(Includes).ToArray() : trades as IReadOnlyList<Trade> ?? trades.ToArray();

    /// <summary>Order events that happened while one of the included trades was open (with a minute either side for working orders).</summary>
    public IReadOnlyList<OrderEvent> ApplyToOrders(IEnumerable<OrderEvent> orders, IReadOnlyList<Trade> includedTrades)
    {
        if (!IsActive) return orders as IReadOnlyList<OrderEvent> ?? orders.ToArray();
        var windows = includedTrades
            .Select(trade => (Symbol: trade.Symbol, Start: trade.EntryUtc.AddMinutes(-1), End: (trade.ExitUtc ?? DateTimeOffset.MaxValue.AddDays(-1)).AddMinutes(1)))
            .ToArray();
        return orders.Where(order => windows.Any(window =>
            order.EventUtc >= window.Start && order.EventUtc <= window.End
            && (string.IsNullOrEmpty(order.Symbol) || string.Equals(order.Symbol, window.Symbol, StringComparison.OrdinalIgnoreCase)))).ToArray();
    }

    public string PeriodLabel
    {
        get
        {
            if (!Filter.HasPeriod) return "All dates";
            var preset = JournalFilter.Periods.First(item => item.Value == Filter.Period).Label;
            var dates = RangeText(From, To);
            return Filter.Period == "custom" ? dates : $"{preset} · {dates}";
        }
    }

    public string SideLabel => Filter.Side switch { "long" => "Long only", "short" => "Short only", _ => "All directions" };

    public string Summary => $"{PeriodLabel} · {SideLabel}";

    public static string RangeText(DateOnly? from, DateOnly? to)
    {
        static string Full(DateOnly date) => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        if (from.HasValue && to.HasValue)
        {
            if (from.Value == to.Value) return Full(from.Value);
            if (from.Value.Year != to.Value.Year) return $"{Full(from.Value)} – {Full(to.Value)}";
            if (from.Value.Month != to.Value.Month) return $"{from.Value.ToString("MMM d", CultureInfo.InvariantCulture)} – {Full(to.Value)}";
            return $"{from.Value.ToString("MMM d", CultureInfo.InvariantCulture)}–{to.Value.Day}, {to.Value.Year}";
        }
        if (from.HasValue) return $"From {Full(from.Value)}";
        if (to.HasValue) return $"Through {Full(to.Value)}";
        return "All dates";
    }
}
