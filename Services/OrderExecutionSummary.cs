using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

/// <summary>
/// Order-lifecycle counts behind the execution indicators. Events are grouped into one lifecycle
/// per account, symbol, and order id (the source key when the order id is blank).
/// </summary>
public sealed record OrderExecutionCounts(int TotalOrders, int Filled, int Canceled, int Modified, int PartiallyFilled, int TotalModifications)
{
    public static readonly OrderExecutionCounts None = new(0, 0, 0, 0, 0, 0);

    public static OrderExecutionCounts From(IEnumerable<OrderLifecycleEvent> events)
    {
        var lifecycles = events
            .GroupBy(eventItem => $"{eventItem.Account}\u001f{eventItem.Symbol}\u001f{(string.IsNullOrWhiteSpace(eventItem.InternalOrderId) ? eventItem.SourceKey : eventItem.InternalOrderId)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(eventItem => eventItem.EventUtc).ThenBy(eventItem => eventItem.RowNumber).ToArray())
            .ToArray();
        if (lifecycles.Length == 0) return None;

        var modificationCounts = lifecycles.Select(lifecycle => lifecycle.Count(eventItem => eventItem.OrderStatus.Contains("modify", StringComparison.OrdinalIgnoreCase))).ToArray();
        return new OrderExecutionCounts(
            TotalOrders: lifecycles.Length,
            Filled: lifecycles.Count(lifecycle => lifecycle.Any(eventItem => eventItem.OrderStatus.Equals("Filled", StringComparison.OrdinalIgnoreCase))),
            Canceled: lifecycles.Count(lifecycle => lifecycle[^1].OrderStatus.Equals("Canceled", StringComparison.OrdinalIgnoreCase)),
            Modified: modificationCounts.Count(count => count > 0),
            PartiallyFilled: lifecycles.Count(lifecycle => lifecycle.Any(eventItem => eventItem.OrderStatus.Contains("partial", StringComparison.OrdinalIgnoreCase) || eventItem.OrderActionSource.Contains("partial", StringComparison.OrdinalIgnoreCase))),
            TotalModifications: modificationCounts.Sum());
    }

    public static OrderExecutionCounts From(IEnumerable<OrderEvent> events) => From(events.Select(OrderLifecycleEvent.From));
}

/// <summary>
/// Caches order-execution counts per journal and filtered trade set. A journal can hold well over
/// a hundred thousand order events, so reading them on every page view is the slowest part of the
/// analytics pages. Imported order events never change in place; the database's
/// <see cref="TradeFoundryDb.OrderEventsVersion"/> moves whenever an import adds or removes them,
/// which retires every cached entry.
/// </summary>
public sealed class OrderExecutionSummaryService
{
    private const int MaxEntries = 128;
    private const string WholeJournalKey = "all";
    private readonly TradeFoundryDb _database;
    private readonly ConcurrentDictionary<(Guid JournalId, string TradeWindowKey), (long Version, OrderExecutionCounts Counts)> _cache = new();

    public OrderExecutionSummaryService(TradeFoundryDb database) => _database = database;

    /// <summary>Counts for the order events the filter keeps alongside <paramref name="includedTrades"/>.</summary>
    public OrderExecutionCounts Get(Guid journalId, JournalFilterScope filter, IReadOnlyList<Trade> includedTrades)
    {
        // Read the version before the events: an import that lands mid-read leaves this entry already stale.
        var version = _database.OrderEventsVersion;
        var key = (journalId, filter.IsActive ? TradeWindowKey(includedTrades) : WholeJournalKey);
        if (_cache.TryGetValue(key, out var cached) && cached.Version == version) return cached.Counts;

        var events = filter.ApplyToOrders(_database.GetOrderLifecycleEvents(journalId), includedTrades);
        var counts = OrderExecutionCounts.From(events);
        if (_cache.Count >= MaxEntries) _cache.Clear();
        _cache[key] = (version, counts);
        return counts;
    }

    /// <summary>A filtered view keeps the orders inside its trades' symbol and entry-to-exit windows, so those identify it.</summary>
    private static string TradeWindowKey(IReadOnlyList<Trade> trades)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> ticks = stackalloc byte[sizeof(long)];
        foreach (var trade in trades)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(trade.Symbol));
            hash.AppendData([0]);
            BitConverter.TryWriteBytes(ticks, trade.EntryUtc.UtcTicks);
            hash.AppendData(ticks);
            BitConverter.TryWriteBytes(ticks, trade.ExitUtc?.UtcTicks ?? -1L);
            hash.AppendData(ticks);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
