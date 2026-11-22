using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    // A review key is the only link between a trade and everything the trader recorded about it, so a
    // rebuild hands each existing key to the trade that is still that trade. A key is only generated
    // for a trade that did not exist before.

    private sealed class DerivedTradeIdentity
    {
        public required string ReviewKey { get; init; }
        public required string Anchor { get; init; }
        public required string Account { get; init; }
        public required string Symbol { get; init; }
        public required string Direction { get; init; }
        /// <summary>Opening fills. Only the first is stored, so the rest are filled in when the old fills can still be replayed.</summary>
        public HashSet<string> OpeningKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ClosingKeys { get; } = new(StringComparer.Ordinal);

        public bool IsSamePosition(PositionState state) =>
            Account.Trim().Equals(state.Account.Trim(), StringComparison.OrdinalIgnoreCase)
            && Symbol.Trim().Equals(state.Symbol.Trim(), StringComparison.OrdinalIgnoreCase)
            && Direction.Equals(state.Direction, StringComparison.OrdinalIgnoreCase);

        public bool StartsWith(PositionState state) => Anchor == state.EntrySourceKey.Trim() && IsSamePosition(state);
    }

    /// <param name="Keys">The key for each rebuilt position, by index.</param>
    /// <param name="SurvivingKeys">Old keys whose fills are still part of a trade, including ones too ambiguous to carry over.</param>
    private sealed record ReviewKeyAssignment(IReadOnlyList<string> Keys, IReadOnlySet<string> SurvivingKeys);

    private static string DerivedAnchor(string sourceKey)
    {
        var separator = sourceKey.LastIndexOf(':');
        return (separator > 0 && int.TryParse(sourceKey[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? sourceKey[..separator]
            : sourceKey).Trim();
    }

    private static IReadOnlyList<DerivedTradeIdentity> ReadDerivedTradeIdentities(SqliteConnection connection, SqliteTransaction transaction, Guid journalId)
    {
        var byId = new Dictionary<string, DerivedTradeIdentity>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id, review_key, source_key, account, symbol, direction FROM trades WHERE journal_id = $journal AND source_type = $source AND review_key <> '' ORDER BY entry_utc, sequence";
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$source", DerivedFillSource);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var identity = new DerivedTradeIdentity
                {
                    ReviewKey = reader.GetString(1),
                    Anchor = DerivedAnchor(reader.GetString(2)),
                    Account = reader.GetString(3),
                    Symbol = reader.GetString(4),
                    Direction = reader.GetString(5)
                };
                identity.OpeningKeys.Add(identity.Anchor);
                byId.Add(reader.GetString(0), identity);
            }
        }

        // Allocations record closing fills only.
        using var allocations = connection.CreateCommand();
        allocations.Transaction = transaction;
        allocations.CommandText = "SELECT a.trade_id, f.source_key FROM trade_fill_allocations a JOIN fills f ON f.id = a.fill_id WHERE f.journal_id = $journal";
        allocations.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var closing = allocations.ExecuteReader();
        while (closing.Read())
            if (byId.TryGetValue(closing.GetString(0), out var identity)) identity.ClosingKeys.Add(closing.GetString(1).Trim());
        return byId.Values.ToArray();
    }

    /// <summary>
    /// For undo, which still has the old fills: completes each trade's membership and returns the keys
    /// of the trades that used a fill from the batch being removed.
    /// </summary>
    private static IReadOnlySet<string> ReplayMembership(IReadOnlyList<DerivedTradeIdentity> trades, IReadOnlyList<Fill> fills, Guid batchId)
    {
        var removed = fills.Where(fill => fill.ImportBatchId == batchId).Select(fill => fill.SourceKey.Trim()).ToHashSet(StringComparer.Ordinal);
        var byAnchor = trades.ToLookup(trade => trade.Anchor, StringComparer.Ordinal);
        foreach (var state in BuildPositions(fills))
        {
            if (byAnchor[state.EntrySourceKey.Trim()].FirstOrDefault(trade => trade.IsSamePosition(state)) is not { } trade) continue;
            trade.OpeningKeys.UnionWith(state.OpeningKeys);
            trade.ClosingKeys.UnionWith(state.Allocations.Select(allocation => allocation.SourceKey.Trim()));
        }
        return trades.Where(trade => trade.OpeningKeys.Overlaps(removed) || trade.ClosingKeys.Overlaps(removed)).Select(trade => trade.ReviewKey).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The same opening fill always keeps its key. Otherwise a key moves only when shared fills point to
    /// exactly one trade; anything less certain is left for the trader to attach.
    /// </summary>
    private static ReviewKeyAssignment AssignReviewKeys(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, string groupingPolicy, IReadOnlyList<DerivedTradeIdentity> previous, IReadOnlyList<PositionState> positions)
    {
        var keys = new string?[positions.Count];
        var surviving = new HashSet<string>(StringComparer.Ordinal);

        var byAnchor = previous.ToLookup(trade => trade.Anchor, StringComparer.Ordinal);
        for (var index = 0; index < positions.Count; index++)
        {
            var state = positions[index];
            var match = byAnchor[state.EntrySourceKey.Trim()].FirstOrDefault(trade => trade.StartsWith(state) && !surviving.Contains(trade.ReviewKey));
            if (match is null) continue;
            keys[index] = match.ReviewKey;
            surviving.Add(match.ReviewKey);
        }

        var unmatched = previous.Where(trade => !surviving.Contains(trade.ReviewKey)).ToArray();
        if (unmatched.Length > 0)
        {
            var byOpening = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var byClosing = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            static void Index(Dictionary<string, List<int>> map, string fillKey, int position)
            {
                if (!map.TryGetValue(fillKey, out var list)) map[fillKey] = list = new List<int>();
                list.Add(position);
            }
            for (var index = 0; index < positions.Count; index++)
            {
                foreach (var key in positions[index].OpeningKeys) Index(byOpening, key, index);
                foreach (var allocation in positions[index].Allocations) Index(byClosing, allocation.SourceKey.Trim(), index);
            }

            var candidates = new Dictionary<int, List<(DerivedTradeIdentity Trade, bool Unique)>>();
            foreach (var trade in unmatched)
            {
                var related = trade.OpeningKeys.SelectMany(key => byOpening.GetValueOrDefault(key) ?? [])
                    .Concat(trade.ClosingKeys.SelectMany(key => byClosing.GetValueOrDefault(key) ?? []))
                    .Distinct()
                    .Where(index => trade.IsSamePosition(positions[index]))
                    .ToArray();
                if (related.Length == 0) continue;
                surviving.Add(trade.ReviewKey);
                var open = related.Where(index => keys[index] is null).ToArray();
                foreach (var index in open)
                {
                    if (!candidates.TryGetValue(index, out var list)) candidates[index] = list = new();
                    list.Add((trade, related.Length == 1));
                }
            }

            var reviewed = candidates.Count == 0 ? new HashSet<string>() : ReadKeysWithReviewData(connection, transaction, journalId);
            foreach (var (index, list) in candidates)
            {
                var withData = list.Where(item => reviewed.Contains(item.Trade.ReviewKey)).ToArray();
                var choice = withData.Length == 1 ? withData : withData.Length == 0 && list.Count == 1 ? list.ToArray() : [];
                if (choice is [{ Unique: true } only]) keys[index] = only.Trade.ReviewKey;
            }
        }

        // An old key that was not carried over stays reserved, so it cannot land on a different trade.
        var used = previous.Select(trade => trade.ReviewKey).ToHashSet(StringComparer.Ordinal);
        for (var index = 0; index < positions.Count; index++)
        {
            if (keys[index] is not null) continue;
            var state = positions[index];
            string key;
            var attempt = 0;
            do
            {
                var anchor = attempt == 0 ? state.EntrySourceKey : $"{state.EntrySourceKey}\u001e{attempt}";
                key = CreateTradeReviewKey(journalId, DerivedFillSource, anchor, groupingPolicy, state.Account, state.Symbol, state.Direction);
                attempt++;
            }
            while (!used.Add(key));
            keys[index] = key;
        }

        return new ReviewKeyAssignment(keys.Select(key => key!).ToArray(), surviving);
    }

    private static HashSet<string> ReadKeysWithReviewData(SqliteConnection connection, SqliteTransaction transaction, Guid journalId)
    {
        var keys = ReadTradeReviews(connection, transaction, journalId).Where(review => review.HasContent).Select(review => review.ReviewKey).ToHashSet(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT review_key FROM trade_review_attachments WHERE journal_id = $journal AND removed_utc IS NULL";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }
}
