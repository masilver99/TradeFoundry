using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

// Reviews that lost their trade before keys were carried through rebuilds, or that were too ambiguous
// to carry. Attaching one can change fees and net P&L, so it only happens when the trader asks.
public sealed partial class TradeFoundryDb
{
    public int CountDetachedReviews(Guid journalId)
    {
        using var connection = OpenConnection();
        return ReadDetached(connection, null, journalId).Count;
    }

    public IReadOnlyList<DetachedReview> GetDetachedReviews(Guid journalId)
    {
        var journal = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        var detached = ReadDetached(connection, transaction, journalId);
        if (detached.Count == 0) return Array.Empty<DetachedReview>();

        // An old key is a hash of its opening fill and the grouping setting, so trying each fill finds
        // the trade that now contains that fill.
        var wanted = detached.Select(item => item.Review.ReviewKey).ToHashSet(StringComparer.Ordinal);
        var suggestions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var policies = new[] { "flat_to_flat", "fifo_lots", TradeFoundryConstants.SourceDefinedGrouping, journal.GroupingPolicy }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var trades = ReadDerivedTradeIdentities(connection, transaction, journalId).ToLookup(trade => trade.Anchor, StringComparer.Ordinal);
        foreach (var state in BuildPositions(ReadFills(connection, transaction, journalId)))
        {
            if (trades[state.EntrySourceKey.Trim()].FirstOrDefault(trade => trade.IsSamePosition(state)) is not { } trade) continue;
            foreach (var fillKey in state.OpeningKeys)
                foreach (var policy in policies)
                {
                    var oldKey = CreateTradeReviewKey(journalId, DerivedFillSource, fillKey, policy, state.Account, state.Symbol, state.Direction);
                    if (!wanted.Contains(oldKey)) continue;
                    if (!suggestions.TryGetValue(oldKey, out var list)) suggestions[oldKey] = list = new List<string>();
                    if (!list.Contains(trade.ReviewKey)) list.Add(trade.ReviewKey);
                }
        }

        return detached
            .OrderByDescending(item => item.Review.UpdatedUtc)
            .Select(item => new DetachedReview(item.Review, item.AttachmentCount, suggestions.GetValueOrDefault(item.Review.ReviewKey) ?? new List<string>()))
            .ToArray();
    }

    /// <summary>
    /// Fills the trade's blank fields, adds tags and moves screenshots. Values already on the trade win;
    /// the detached original is kept as its own revision in that trade's history.
    /// </summary>
    public TradeReviewAnnotation AttachDetachedReview(Guid journalId, string detachedKey, string targetKey)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var detached = ReadDetached(connection, transaction, journalId).FirstOrDefault(item => item.Review.ReviewKey == detachedKey).Review
            ?? throw new InvalidOperationException("That review is no longer detached.");
        if (TagScalar(connection, transaction, "SELECT 1 FROM trades WHERE journal_id = $journal AND review_key = $key", ("$journal", journalId), ("$key", targetKey)) is null)
            throw new InvalidOperationException("Choose a trade in this journal.");

        var target = ReadTradeReviewOrHistoryRevision(connection, transaction, journalId, targetKey);
        var now = DateTimeOffset.UtcNow;
        var merged = new TradeReviewAnnotation
        {
            JournalId = journalId,
            ReviewKey = targetKey,
            Revision = target.Revision + 2,
            ReviewNote = Prefer(target.ReviewNote, detached.ReviewNote, value => !string.IsNullOrWhiteSpace(LexicalPlainText.Extract(value))),
            Setup = Prefer(target.Setup, detached.Setup),
            TagIds = target.TagIds.Concat(detached.TagIds).Distinct().ToArray(),
            Tags = target.Tags.Concat(detached.Tags).Distinct().ToArray(),
            PlannedEntryPrice = target.PlannedEntryPrice ?? detached.PlannedEntryPrice,
            PlannedStopPrice = target.PlannedStopPrice ?? detached.PlannedStopPrice,
            PlannedTargetPrice = target.PlannedTargetPrice ?? detached.PlannedTargetPrice,
            PlannedRiskPoints = target.PlannedRiskPoints ?? detached.PlannedRiskPoints,
            PlannedRiskCurrency = target.PlannedRiskCurrency ?? detached.PlannedRiskCurrency,
            ExchangeFees = target.ExchangeFees ?? detached.ExchangeFees,
            NfaFees = target.NfaFees ?? detached.NfaFees,
            ClearingFees = target.ClearingFees ?? detached.ClearingFees,
            AllInCommission = target.AllInCommission ?? detached.AllInCommission,
            PlanAdherence = Prefer(target.PlanAdherence, detached.PlanAdherence),
            ProcessRating = target.ProcessRating ?? detached.ProcessRating,
            Mistakes = Prefer(target.Mistakes, detached.Mistakes),
            Lessons = Prefer(target.Lessons, detached.Lessons),
            UpdatedUtc = now
        };

        InsertTradeReviewHistory(connection, transaction, journalId, targetKey, target.Revision + 1, "detached original", SerializeReview(target), SerializeReview(detached), "The detached review before it was combined with this trade's.", now);
        WriteTradeReview(connection, transaction, merged);
        TagExecute(connection, transaction, "INSERT OR IGNORE INTO trade_review_tags (journal_id, review_key, tag_id, position, assigned_utc) SELECT journal_id, $target, tag_id, position + 1000, assigned_utc FROM trade_review_tags WHERE journal_id = $journal AND review_key = $source",
            ("$target", targetKey), ("$journal", journalId), ("$source", detachedKey));
        TagExecute(connection, transaction, "UPDATE trade_review_attachments SET review_key = $target WHERE journal_id = $journal AND review_key = $source AND removed_utc IS NULL",
            ("$target", targetKey), ("$journal", journalId), ("$source", detachedKey));
        var saved = ReadTradeReviewOrHistoryRevision(connection, transaction, journalId, targetKey);
        InsertTradeReviewHistory(connection, transaction, journalId, targetKey, merged.Revision, "reattached", SerializeReview(target), SerializeReview(saved), "Attached a detached review.", now);

        InsertTradeReviewHistory(connection, transaction, journalId, detachedKey, detached.Revision + 1, "moved", SerializeReview(detached), "{}", "Attached to a trade.", now);
        TagExecute(connection, transaction, "DELETE FROM trade_review_tags WHERE journal_id = $journal AND review_key = $source", ("$journal", journalId), ("$source", detachedKey));
        TagExecute(connection, transaction, "DELETE FROM trade_review_annotations WHERE journal_id = $journal AND review_key = $source", ("$journal", journalId), ("$source", detachedKey));
        transaction.Commit();
        return saved;
    }

    private static string Prefer(string current, string fallback, Func<string, bool>? hasValue = null) =>
        (hasValue?.Invoke(current) ?? !string.IsNullOrWhiteSpace(current)) ? current : fallback;

    private static List<(TradeReviewAnnotation Review, int AttachmentCount)> ReadDetached(SqliteConnection connection, SqliteTransaction? transaction, Guid journalId)
    {
        var attachments = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var command = TagSql(connection, transaction, "SELECT review_key, COUNT(*) FROM trade_review_attachments WHERE journal_id = $journal AND removed_utc IS NULL GROUP BY review_key", ("$journal", journalId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) attachments[reader.GetString(0)] = reader.GetInt32(1);

        var tradeKeys = new HashSet<string>(StringComparer.Ordinal);
        using (var command = TagSql(connection, transaction, "SELECT review_key FROM trades WHERE journal_id = $journal", ("$journal", journalId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) tradeKeys.Add(reader.GetString(0));

        var detached = new List<(TradeReviewAnnotation, int)>();
        foreach (var review in ReadTradeReviews(connection, transaction, journalId))
        {
            var images = attachments.GetValueOrDefault(review.ReviewKey);
            attachments.Remove(review.ReviewKey);
            if (!tradeKeys.Contains(review.ReviewKey) && (review.HasContent || images > 0)) detached.Add((review, images));
        }
        // A trade can have screenshots without a written review.
        foreach (var (key, images) in attachments)
            if (!tradeKeys.Contains(key)) detached.Add((EmptyTradeReview(journalId, key), images));
        return detached;
    }
}
