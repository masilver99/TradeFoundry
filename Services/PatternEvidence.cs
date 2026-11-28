using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

internal enum PatternFeature
{
    Session, Weekday, Side, InitialSize, PreviousResult, GapPoints, OvernightAlignment,
    CandleRange, CandleBody, RejectionWick, MovePoints, MoveAtr, AtrPoints, EmaDistance, EmaTouch,
    RelativeVolume, VolumeDelta, StopAtr, TargetAtr, BreakEven, Trailing, NativeTrailing
}

internal sealed class PatternObservation(Trade trade, DateOnly date, DateOnly reviewDate, int session)
{
    public Trade Trade { get; } = trade;
    public DateOnly Date { get; } = date;
    public DateOnly ReviewDate { get; } = reviewDate;
    public int Session { get; } = session;
    public decimal NetPoints => Trade.NetPnl / Trade.ClosedQuantity / Trade.PointValue;
    public Dictionary<PatternFeature, decimal> Features { get; } = new();
    public bool HasBar { get; set; }
    public bool HasWorkingStop { get; set; }
    public int? BreakEvenProgress { get; set; }
    public int? TrailingProgress { get; set; }
}

internal sealed class PatternEvidence(TradeFoundryDb database)
{
    private const int EarlyBreakEvenMinutes = 3;
    private const int TrailingMinutes = 5;
    private static readonly TimeZoneInfo ExchangeZone = TimeZoneCatalog.Resolve("America/New_York");
    private static readonly TimeOnly RthOpen = new(9, 30);
    private static readonly TimeOnly RthLastMinute = new(15, 59);

    public Dictionary<string, List<PatternObservation>> Load(Journal journal, CancellationToken cancellationToken)
    {
        var trades = database.GetAllTrades(journal.Id)
            .Where(trade => trade.ExitUtc > trade.EntryUtc && trade.Status == "closed"
                && trade.ClosedQuantity > 0 && trade.PointValue > 0 && trade.Direction is "Long" or "Short")
            .GroupBy(trade => InstrumentCatalog.ExtractRoot(trade.Instrument))
            .Where(group => group.Key is "ES" or "MES").ToArray();
        var result = new Dictionary<string, List<PatternObservation>>();
        if (trades.Length == 0) return result;

        var entries = database.GetPatternEntries(journal.Id);
        var orders = database.GetPatternProtectiveEvents(journal.Id)
            .ToLookup(item => OrderKey(item.Account, item.Symbol, item.ParentOrderId));
        var journalZone = TimeZoneCatalog.Resolve(journal.TimeZone);

        foreach (var instrument in trades)
        {
            var observations = new List<PatternObservation>();
            result.Add(instrument.Key, observations);
            var eligible = instrument.Where(trade => entries.TryGetValue(trade.SourceKey, out var entry)
                && entry.Time == trade.EntryUtc).ToArray();
            var previousTrades = eligible.ToLookup(trade => (trade.Account.ToUpperInvariant(), ExchangeDate(trade.ExitUtc!.Value)));
            foreach (var day in eligible.GroupBy(trade => ExchangeDate(trade.EntryUtc)).OrderBy(group => group.Key))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var first = Minute(day.Min(trade => trade.EntryUtc)).AddMinutes(-100);
                var last = Minute(day.Max(trade => trade.EntryUtc)).AddMinutes(TrailingMinutes);
                var bars = database.GetBarsForTrade(journal.Id, instrument.Key, first, last, "1m")
                    .ToDictionary(bar => bar.EventUtc);
                var openUtc = ExchangeTime(day.Key, RthOpen);
                var hasRthTrades = day.Any(trade => trade.EntryUtc > openUtc
                    && trade.EntryUtc < ExchangeTime(day.Key, new TimeOnly(16, 0)));
                var market = hasRthTrades
                    ? LoadOpeningContext(journal.Id, instrument.Key, day.Key, openUtc, bars)
                    : (Gap: (decimal?)null, Overnight: (decimal?)null);

                foreach (var trade in day)
                {
                    var local = TimeZoneInfo.ConvertTime(trade.EntryUtc, ExchangeZone);
                    var reviewDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(trade.ExitUtc!.Value, journalZone).DateTime);
                    var session = SessionBucket(local.Hour * 60 + local.Minute);
                    var row = new PatternObservation(trade, day.Key, reviewDate, session);
                    var direction = trade.Direction == "Long" ? 1 : -1;
                    row.Features[PatternFeature.Session] = session;
                    row.Features[PatternFeature.Weekday] = (int)local.DayOfWeek;
                    row.Features[PatternFeature.Side] = direction;
                    if (session != 0 && trade.EntryUtc > openUtc)
                    {
                        if (market.Gap is { } gap) row.Features[PatternFeature.GapPoints] = gap;
                        if (market.Overnight is { } overnight)
                            row.Features[PatternFeature.OvernightAlignment] = Math.Sign(overnight) * direction;
                    }

                    var previous = previousTrades[(trade.Account.ToUpperInvariant(), day.Key)]
                        .Where(other => other.ExitUtc < trade.EntryUtc).MaxBy(other => other.ExitUtc);
                    if (previous is not null)
                        row.Features[PatternFeature.PreviousResult] = previous.NetPnl < 0
                            ? (trade.EntryUtc - previous.ExitUtc!.Value).TotalMinutes < 5 ? -2 : -1
                            : Math.Sign(previous.NetPnl);

                    ReadPriceFeatures(row, bars, direction);
                    var entry = entries[trade.SourceKey];
                    row.Features[PatternFeature.InitialSize] = entry.Quantity;
                    if (entry.SingleOpeningFill && entry.OrderId is not ("" or "0"))
                        ReadManagement(row, entry, orders[OrderKey(trade.Account, trade.Symbol, entry.OrderId)], bars, direction);
                    observations.Add(row);
                }
            }
        }
        return result;
    }

    internal static void ReadPriceFeatures(PatternObservation row, IReadOnlyDictionary<DateTimeOffset, Bar> bars, int direction)
    {
        // Bar timestamps mark the start of each minute. The entry minute can contain post-fill activity.
        var minute = Minute(row.Trade.EntryUtc);
        var closed = new List<Bar>(100);
        for (var offset = 1; offset <= 100 && bars.TryGetValue(minute.AddMinutes(-offset), out var bar); offset++)
        {
            if (bar.Low <= 0 || bar.High < bar.Low || bar.Open < bar.Low || bar.Open > bar.High
                || bar.Close < bar.Low || bar.Close > bar.High) break;
            closed.Add(bar);
        }
        if (closed.Count == 0) return;
        row.HasBar = true;
        var last = closed[0];
        var range = last.High - last.Low;
        if (range > 0)
        {
            row.Features[PatternFeature.CandleBody] = direction * (last.Close - last.Open) / range;
            var wick = direction > 0 ? Math.Min(last.Open, last.Close) - last.Low : last.High - Math.Max(last.Open, last.Close);
            row.Features[PatternFeature.RejectionWick] = wick / range;
        }
        if (last is { BidVolume: >= 0, AskVolume: >= 0 } && (decimal)last.BidVolume.Value + last.AskVolume.Value > 0)
            row.Features[PatternFeature.VolumeDelta] = direction * ((decimal)last.AskVolume.Value - last.BidVolume.Value)
                / ((decimal)last.AskVolume.Value + last.BidVolume.Value);
        if (closed.Count >= 6)
            row.Features[PatternFeature.MovePoints] = direction * (last.Close - closed[5].Close);
        if (closed.Count >= 15)
        {
            var atr = Enumerable.Range(0, 14).Average(index =>
            {
                var previousClose = closed[index + 1].Close;
                return Math.Max(closed[index].High, previousClose) - Math.Min(closed[index].Low, previousClose);
            });
            if (atr > 0)
            {
                row.Features[PatternFeature.AtrPoints] = atr;
                row.Features[PatternFeature.MoveAtr] = row.Features[PatternFeature.MovePoints] / atr;
            }
        }
        if (closed.Count >= 21)
        {
            var prior = closed.Skip(1).Take(20).ToArray();
            var typicalRange = Median(prior.Select(bar => bar.High - bar.Low));
            if (typicalRange is > 0) row.Features[PatternFeature.CandleRange] = range / typicalRange.Value;
            if (last.Volume is > 0 && prior.All(bar => bar.Volume is > 0))
                row.Features[PatternFeature.RelativeVolume] = last.Volume.Value / Median(prior.Select(bar => (decimal)bar.Volume!.Value))!.Value;
        }
        if (closed.Count == 100)
        {
            var ema = closed[^1].Close;
            for (var index = closed.Count - 2; index >= 0; index--)
                ema += (closed[index].Close - ema) / 11m;
            row.Features[PatternFeature.EmaDistance] = direction * (last.Close - ema);
            row.Features[PatternFeature.EmaTouch] = last.Low <= ema && last.High >= ema ? 1 : 0;
        }
    }

    internal static void ReadManagement(PatternObservation row, TradeFoundryDb.PatternEntry entry,
        IEnumerable<OrderEvent> events, IReadOnlyDictionary<DateTimeOffset, Bar> bars, int direction)
    {
        var firstExit = entry.FirstExitUtc;
        if (firstExit is null || firstExit <= entry.Time || !row.Features.TryGetValue(PatternFeature.AtrPoints, out var atr)) return;
        // Require a single attached, full-size working stop. A split bracket or an unlinked order is ambiguous.
        var protection = events.Where(item => item.EventUtc >= entry.Time.AddMinutes(-1)
            && item.EventUtc <= entry.Time.AddMinutes(TrailingMinutes)
            && item.InternalOrderId is not ("" or "0")
            && item.Side.Equals(direction > 0 ? "Sell" : "Buy", StringComparison.OrdinalIgnoreCase)
            && (item.OpenClose.Length == 0 || item.OpenClose.Equals("Close", StringComparison.OrdinalIgnoreCase))).ToArray();
        var initialDeadline = entry.Time.AddSeconds(15);
        var stopOrders = protection.Where(item => item.OrderType.Contains("stop", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.InternalOrderId, StringComparer.OrdinalIgnoreCase).ToArray();
        var initialStops = stopOrders.Select(order => order.FirstOrDefault(item => item.EventUtc <= initialDeadline
            && item.EventUtc < firstExit && IsWorking(item))).Where(item => item is not null).ToArray();
        if (initialStops.Length != 1 || initialStops[0] is not { Price: > 0, Quantity: { } stopSize } initial
            || stopSize != entry.Quantity) return;

        var initialRisk = direction * (entry.Price - initial.Price.Value);
        if (initialRisk <= 0) return;
        var history = stopOrders.Single(order => order.Key.Equals(initial.InternalOrderId, StringComparison.OrdinalIgnoreCase))
            .SkipWhile(item => item.Id != initial.Id).ToArray();
        if (history.Any(item => item.EventUtc < entry.Time && IsTerminal(item))) return;
        row.HasWorkingStop = true;
        row.Features[PatternFeature.StopAtr] = initialRisk / atr;

        var targets = protection.Where(item => item.OrderType.Equals("Limit", StringComparison.OrdinalIgnoreCase)
            && item.EventUtc <= initialDeadline && item.EventUtc < firstExit && IsWorking(item))
            .DistinctBy(item => item.InternalOrderId).ToArray();
        if (targets.Length == 1 && targets[0] is { Price: > 0, Quantity: { } targetSize } target
            && targetSize == entry.Quantity && direction * (target.Price.Value - entry.Price) > 0)
            row.Features[PatternFeature.TargetAtr] = direction * (target.Price.Value - entry.Price) / atr;

        ScanLandmark(EarlyBreakEvenMinutes);
        ScanLandmark(TrailingMinutes);

        void ScanLandmark(int minutes)
        {
            var landmark = entry.Time.AddMinutes(minutes);
            if (firstExit <= landmark || !TryGetProgress(entry, landmark, bars, direction, atr, out var progress)) return;
            if (stopOrders.Any(order => !order.Key.Equals(initial.InternalOrderId, StringComparison.OrdinalIgnoreCase)
                && order.Any(item => item.EventUtc <= landmark && IsWorking(item)))) return;

            var price = initial.Price.Value;
            var advances = 0;
            var reachedBreakEven = false;
            var nativeTrailing = false;
            foreach (var item in history.Where(item => item.EventUtc <= landmark))
            {
                if (IsTerminal(item)) return;
                if (!IsWorking(item)) continue;
                if (item.Price is not > 0 || item.Quantity != entry.Quantity) return;
                var change = direction * (item.Price.Value - price);
                advances = change > 0 ? advances + 1 : change < 0 ? 0 : advances;
                if (item.EventUtc >= entry.Time && direction * (item.Price.Value - entry.Price) >= 0)
                    reachedBreakEven = true;
                price = item.Price.Value;
                nativeTrailing = item.OrderType.Contains("trailing", StringComparison.OrdinalIgnoreCase);
            }
            if (minutes == EarlyBreakEvenMinutes)
            {
                row.BreakEvenProgress = progress;
                row.Features[PatternFeature.BreakEven] = reachedBreakEven ? 1 : 0;
            }
            else
            {
                row.TrailingProgress = progress;
                row.Features[PatternFeature.Trailing] = advances >= 2 ? 1 : 0;
                row.Features[PatternFeature.NativeTrailing] = nativeTrailing ? 1 : 0;
            }
        }
    }

    private static bool TryGetProgress(TradeFoundryDb.PatternEntry entry, DateTimeOffset landmark,
        IReadOnlyDictionary<DateTimeOffset, Bar> bars, int direction, decimal atr, out int progress)
    {
        progress = 0;
        var lastMinute = Minute(landmark).AddMinutes(-1);
        for (var time = Minute(entry.Time).AddMinutes(1); time <= lastMinute; time = time.AddMinutes(1))
            if (!bars.ContainsKey(time)) return false;
        if (!bars.TryGetValue(lastMinute, out var last) || last.Close <= 0) return false;
        var move = direction * (last.Close - entry.Price) / atr;
        progress = move < 0 ? -1 : move < 1 ? 0 : 1;
        return true;
    }

    private (decimal? Gap, decimal? Overnight) LoadOpeningContext(Guid journalId, string instrument,
        DateOnly day, DateTimeOffset open, IReadOnlyDictionary<DateTimeOffset, Bar> bars)
    {
        if (day.DayOfWeek is DayOfWeek.Sunday or DayOfWeek.Saturday) return (null, null);
        var currentOpen = Find(open);
        if (currentOpen is not > 0) return (null, null);
        var previous = day.AddDays(day.DayOfWeek == DayOfWeek.Monday ? -3 : -1);
        var priorClose = Find(ExchangeTime(previous, RthLastMinute), close: true);
        var overnightOpen = Find(ExchangeTime(day.AddDays(-1), new TimeOnly(18, 0)));
        var overnightClose = Find(open.AddMinutes(-1), close: true);
        return (priorClose is > 0 ? currentOpen - priorClose : null,
            overnightOpen is > 0 && overnightClose is > 0 ? overnightClose - overnightOpen : null);

        decimal? Find(DateTimeOffset time, bool close = false)
        {
            var bar = bars.GetValueOrDefault(time) ?? database.GetBarsForTrade(journalId, instrument, time, time, "1m").FirstOrDefault();
            return close ? bar?.Close : bar?.Open;
        }
    }

    internal static decimal? Median(IEnumerable<decimal> values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length == 0 ? null : (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
    }

    private static bool IsWorking(OrderEvent item) => item.OrderStatus.Equals("Open", StringComparison.OrdinalIgnoreCase)
        || item.OrderStatus.Equals("Working", StringComparison.OrdinalIgnoreCase);
    private static bool IsTerminal(OrderEvent item) => item.OrderStatus.ToUpperInvariant() is "CANCELED" or "CANCELLED" or "FILLED" or "ERROR" or "REJECTED";
    private static string OrderKey(string account, string symbol, string id) => $"{account}\u001f{symbol}\u001f{id}".ToUpperInvariant();
    private static DateOnly ExchangeDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, ExchangeZone).DateTime);
    private static DateTimeOffset ExchangeTime(DateOnly date, TimeOnly time) => new(
        TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time), ExchangeZone));
    private static DateTimeOffset Minute(DateTimeOffset utc) => new(utc.UtcTicks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, TimeSpan.Zero);
    private static int SessionBucket(int minutes) => minutes switch
    {
        >= 570 and < 600 => 1,
        >= 600 and < 660 => 2,
        >= 660 and < 720 => 3,
        >= 720 and < 840 => 4,
        >= 840 and < 960 => 5,
        _ => 0
    };
}
