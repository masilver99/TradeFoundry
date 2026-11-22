using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

// Links store the tag the trader picked. Merges are resolved when reading, which is what lets a merge
// be undone without losing which trade had which tag.
public sealed partial class TradeFoundryDb
{
    private const string TagMigrationKey = "tags_v1_migrated";

    private void InitializeTags(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS tags (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, name TEXT NOT NULL, name_key TEXT NOT NULL, description TEXT NOT NULL DEFAULT '', known TEXT NOT NULL DEFAULT 'Unset', scope TEXT NOT NULL CHECK(scope IN ('Trade', 'Day', 'Both')), archived INTEGER NOT NULL DEFAULT 0, merged_into_id TEXT NULL REFERENCES tags(id), revision INTEGER NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL, UNIQUE(journal_id, name_key));
                CREATE TABLE IF NOT EXISTS tag_aliases (journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, alias_key TEXT NOT NULL, alias TEXT NOT NULL, tag_id TEXT NOT NULL REFERENCES tags(id) ON DELETE CASCADE, PRIMARY KEY(journal_id, alias_key));
                CREATE TABLE IF NOT EXISTS tag_history (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, tag_id TEXT NOT NULL, revision INTEGER NOT NULL, action TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL, created_utc TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_tag_history_tag ON tag_history(journal_id, tag_id, revision DESC);
                CREATE TABLE IF NOT EXISTS trade_review_tags (journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_key TEXT NOT NULL, tag_id TEXT NOT NULL REFERENCES tags(id), position INTEGER NOT NULL, assigned_utc TEXT NOT NULL, PRIMARY KEY(journal_id, review_key, tag_id));
                CREATE INDEX IF NOT EXISTS ix_trade_review_tags_tag ON trade_review_tags(tag_id);
                CREATE TABLE IF NOT EXISTS daily_tag_sets (journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_date TEXT NOT NULL, revision INTEGER NOT NULL, updated_utc TEXT NOT NULL, PRIMARY KEY(journal_id, review_date));
                CREATE TABLE IF NOT EXISTS daily_tag_links (journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_date TEXT NOT NULL, tag_id TEXT NOT NULL REFERENCES tags(id), position INTEGER NOT NULL, assigned_utc TEXT NOT NULL, PRIMARY KEY(journal_id, review_date, tag_id));
                CREATE INDEX IF NOT EXISTS ix_daily_tag_links_tag ON daily_tag_links(tag_id);
                CREATE TABLE IF NOT EXISTS daily_tag_history (id TEXT PRIMARY KEY, journal_id TEXT NOT NULL REFERENCES journals(id) ON DELETE CASCADE, review_date TEXT NOT NULL, revision INTEGER NOT NULL, action TEXT NOT NULL, before_json TEXT NOT NULL, after_json TEXT NOT NULL, created_utc TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_daily_tag_history_date ON daily_tag_history(journal_id, review_date, revision DESC);
                """;
            command.ExecuteNonQuery();
        }

        MigrateTextTags(connection);
    }

    private void MigrateTextTags(SqliteConnection connection)
    {
        if (TagScalar(connection, null, "SELECT settings_value FROM app_settings WHERE settings_key = $key", ("$key", TagMigrationKey)) is not null) return;
        if (TagScalar(connection, null, "SELECT 1 FROM trade_review_annotations WHERE tags_json NOT IN ('', '[]') LIMIT 1") is not null)
            BackUpReviewTables(connection);

        using var transaction = connection.BeginTransaction();
        if (TagScalar(connection, transaction, "SELECT settings_value FROM app_settings WHERE settings_key = $key", ("$key", TagMigrationKey)) is not null) return;

        var rows = new List<(Guid JournalId, string ReviewKey, string Name, int Position, string AssignedUtc)>();
        using (var command = TagSql(connection, transaction, "SELECT journal_id, review_key, tags_json, updated_utc FROM trade_review_annotations WHERE tags_json NOT IN ('', '[]')"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                string?[] names;
                try { names = JsonSerializer.Deserialize<string?[]>(reader.GetString(2)) ?? []; }
                // An unreadable value was already shown as "no tags"; it stays in the column untouched.
                catch (JsonException) { continue; }
                for (var index = 0; index < names.Length; index++)
                {
                    var name = TagNames.Clean(names[index]);
                    if (TagNames.Key(name).Length > 0) rows.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1), name, index, reader.GetString(3)));
                }
            }
        }

        // Only names that are the same after normalization become one tag. Likely typos are left for the trader to merge.
        foreach (var group in rows.GroupBy(row => (row.JournalId, Key: TagNames.Key(row.Name))))
        {
            var name = group.GroupBy(row => row.Name, StringComparer.Ordinal).OrderByDescending(spelling => spelling.Count()).ThenBy(spelling => spelling.Key, StringComparer.Ordinal).First().Key;
            var tag = InsertTag(connection, transaction, group.Key.JournalId, new TagDraft(name, string.Empty, TagKnown.Unset, TagScope.Trade), "migrated");
            foreach (var row in group)
                TagExecute(connection, transaction, "INSERT OR IGNORE INTO trade_review_tags (journal_id, review_key, tag_id, position, assigned_utc) VALUES ($journal, $key, $tag, $position, $assigned)",
                    ("$journal", row.JournalId), ("$key", row.ReviewKey), ("$tag", tag.Id), ("$position", row.Position), ("$assigned", row.AssignedUtc));
        }

        TagExecute(connection, transaction, "INSERT INTO app_settings (settings_key, settings_value) VALUES ($key, '1')", ("$key", TagMigrationKey));
        transaction.Commit();
    }

    // Copies just the tables being converted. A copy of the whole database can be several gigabytes and
    // would outlast the desktop shell's startup timeout.
    private void BackUpReviewTables(SqliteConnection connection)
    {
        var directory = Path.Combine(Path.GetDirectoryName(_databasePath) ?? AppContext.BaseDirectory, "backups");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"tags-migration-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.db");
        TagExecute(connection, null, "ATTACH DATABASE $path AS tag_backup", ("$path", path));
        try
        {
            TagExecute(connection, null, "CREATE TABLE tag_backup.trade_review_annotations AS SELECT * FROM main.trade_review_annotations");
            TagExecute(connection, null, "CREATE TABLE tag_backup.trade_review_history AS SELECT * FROM main.trade_review_history");
        }
        finally
        {
            TagExecute(connection, null, "DETACH DATABASE tag_backup");
        }
    }

    public TagCatalog GetTagCatalog(Guid journalId)
    {
        using var connection = OpenConnection();
        return ReadTagCatalog(connection, null, journalId);
    }

    /// <summary>Each trade's current tags by review key, for trades that exist.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<Tag>> GetTradeTags(Guid journalId)
    {
        using var connection = OpenConnection();
        var catalog = ReadTagCatalog(connection, null, journalId);
        using var command = TagSql(connection, null, "SELECT l.review_key, l.tag_id FROM trade_review_tags l WHERE l.journal_id = $journal AND EXISTS (SELECT 1 FROM trades t WHERE t.journal_id = l.journal_id AND t.review_key = l.review_key) ORDER BY l.review_key, l.position, l.tag_id", ("$journal", journalId));
        return ReadResolvedLinks(command, catalog, value => value);
    }

    public IReadOnlyDictionary<DateOnly, IReadOnlyList<Tag>> GetDayTags(Guid journalId)
    {
        using var connection = OpenConnection();
        var catalog = ReadTagCatalog(connection, null, journalId);
        using var command = TagSql(connection, null, "SELECT review_date, tag_id FROM daily_tag_links WHERE journal_id = $journal ORDER BY review_date, position, tag_id", ("$journal", journalId));
        return ReadResolvedLinks(command, catalog, value => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    public IReadOnlyDictionary<DateOnly, DailyTagSet> GetDailyTagSets(Guid journalId)
    {
        var tags = GetDayTags(journalId);
        using var connection = OpenConnection();
        using var command = TagSql(connection, null, "SELECT review_date, revision FROM daily_tag_sets WHERE journal_id = $journal", ("$journal", journalId));
        using var reader = command.ExecuteReader();
        var sets = new Dictionary<DateOnly, DailyTagSet>();
        while (reader.Read())
        {
            var date = DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            sets[date] = new DailyTagSet(date, reader.GetInt32(1), tags.TryGetValue(date, out var list) ? list.Select(tag => new TagReference(tag.Id, tag.Name)).ToArray() : Array.Empty<TagReference>());
        }
        return sets;
    }

    public DailyTagSet GetDailyTagSet(Guid journalId, DateOnly date)
    {
        using var connection = OpenConnection();
        return ReadDailyTagSet(connection, null, journalId, date, ReadTagCatalog(connection, null, journalId));
    }

    public DailyTagSaveResult SaveDailyTags(Guid journalId, DateOnly date, IReadOnlyList<Guid> tagIds, int expectedRevision)
    {
        if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var catalog = ReadTagCatalog(connection, transaction, journalId);
        var current = ReadDailyTagSet(connection, transaction, journalId, date, catalog);
        if (current.Revision != expectedRevision)
        {
            InsertDailyTagHistory(connection, transaction, journalId, date, current.Revision, "conflict", current, new { tagIds });
            transaction.Commit();
            return new DailyTagSaveResult(false, current);
        }

        var day = DayText(date);
        var selected = ValidateSelection(catalog, tagIds, current.Tags.Select(tag => tag.Id).ToHashSet(), TagTarget.Day);
        WriteTagLinks(connection, transaction, catalog, selected, "daily_tag_links", "review_date", journalId, day);
        var revision = current.Revision + 1;
        WriteDailyTagRevision(connection, transaction, journalId, date, revision);
        var saved = ReadDailyTagSet(connection, transaction, journalId, date, catalog);
        InsertDailyTagHistory(connection, transaction, journalId, date, revision, "saved", current, saved);
        transaction.Commit();
        return new DailyTagSaveResult(true, saved);
    }

    /// <summary>Creates a tag. Look-alikes must be named in <paramref name="confirmedDifferent"/>.</summary>
    public Tag CreateTag(Guid journalId, TagDraft draft, IReadOnlyCollection<Guid>? confirmedDifferent = null)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        var clean = ValidateTagDraft(draft);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        EnsureNameAvailable(ReadTagCatalog(connection, transaction, journalId), clean.Name!, confirmedDifferent, null);
        var tag = InsertTag(connection, transaction, journalId, clean, "created");
        transaction.Commit();
        return tag;
    }

    public Tag UpdateTag(Guid journalId, Guid tagId, int expectedRevision, TagDraft draft, IReadOnlyCollection<Guid>? confirmedDifferent = null)
    {
        var clean = ValidateTagDraft(draft);
        return MutateTag(journalId, tagId, expectedRevision, "updated", (connection, transaction, catalog, current) =>
        {
            var name = clean.Name!;
            var key = TagNames.Key(name);
            var oldKey = TagNames.Key(current.Name);
            if (key != oldKey)
            {
                EnsureNameAvailable(catalog, name, confirmedDifferent, current.Id);
                // Taking back a former name turns that alias into the name again.
                TagExecute(connection, transaction, "DELETE FROM tag_aliases WHERE journal_id = $journal AND alias_key = $key AND tag_id = $tag", ("$journal", journalId), ("$key", key), ("$tag", tagId));
                TagExecute(connection, transaction, "INSERT INTO tag_aliases (journal_id, alias_key, alias, tag_id) VALUES ($journal, $key, $alias, $tag)", ("$journal", journalId), ("$key", oldKey), ("$alias", current.Name), ("$tag", tagId));
            }
            if (clean.Scope != TagScope.Both && clean.Scope != current.Scope)
            {
                var (table, label) = clean.Scope == TagScope.Trade ? ("daily_tag_links", "days") : ("trade_review_tags", "trades");
                if (ReadLinkedTagIds(connection, transaction, journalId, table).Any(id => catalog.Resolve(id)?.Id == tagId))
                    throw new ArgumentException($"This tag is already on {label}. Remove it from them, or choose Trades and days.");
            }
            return current with { Name = name, Description = clean.Description!, Known = clean.Known, Scope = clean.Scope };
        });
    }

    public Tag AllowTagEverywhere(Guid journalId, Guid tagId, int expectedRevision) =>
        MutateTag(journalId, tagId, expectedRevision, "scope widened", (_, _, _, current) => current with { Scope = TagScope.Both });

    public Tag SetTagArchived(Guid journalId, Guid tagId, int expectedRevision, bool archived) =>
        MutateTag(journalId, tagId, expectedRevision, archived ? "archived" : "restored", (_, _, _, current) => current with { Archived = archived });

    public Tag MergeTag(Guid journalId, Guid sourceId, int expectedRevision, Guid targetId) =>
        MutateTag(journalId, sourceId, expectedRevision, "merged", (_, _, catalog, source) =>
        {
            var target = catalog.Get(targetId) ?? throw new ArgumentException("Choose a tag to merge into.");
            if (target.Id == source.Id || target.MergedIntoId.HasValue) throw new ArgumentException("Choose a different tag to merge into.");
            if (target.Archived) throw new ArgumentException($"Restore {target.Name} before merging into it.");
            if (target.Scope != TagScope.Both && target.Scope != source.Scope)
                throw new ArgumentException($"{target.Name} is only for {Tag.ScopeLabel(target.Scope).ToLowerInvariant()}. Change it to Trades and days before merging.");
            return source with { MergedIntoId = target.Id };
        });

    public Tag UndoTagMerge(Guid journalId, Guid sourceId, int expectedRevision)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var before = ReadTagCatalog(connection, transaction, journalId);
        var source = before.Get(sourceId) ?? throw new InvalidOperationException("Tag was not found.");
        if (source.MergedIntoId is null) throw new ArgumentException("This tag is not merged.");
        if (source.Revision != expectedRevision) throw new ArgumentException("This tag changed in another window. Reload it and try again.");

        // Tags that were merged into the source earlier move back with it.
        var moving = before.All.Where(tag => ResolvesThrough(before, tag, sourceId)).Select(tag => tag.Id).ToHashSet();
        var keys = ReadLinkOwners(connection, transaction, journalId, "trade_review_tags", "review_key", moving);
        var dates = ReadLinkOwners(connection, transaction, journalId, "daily_tag_links", "review_date", moving);
        var reviewsBefore = keys.ToDictionary(key => key, key => ReadTradeReviewOrHistoryRevision(connection, transaction, journalId, key));
        var daysBefore = dates.Select(value => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToDictionary(date => date, date => ReadDailyTagSet(connection, transaction, journalId, date, before));

        var restored = WriteTag(connection, transaction, source, source with { MergedIntoId = null }, "merge undone");
        var after = ReadTagCatalog(connection, transaction, journalId);

        // The items' tags changed, so their revisions do too. A page opened before the undo then conflicts
        // instead of saving the merged tag over the restored one.
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, previous) in reviewsBefore)
        {
            var revision = previous.Revision + 1;
            TagExecute(connection, transaction, "UPDATE trade_review_annotations SET revision = $revision, updated_utc = $updated WHERE journal_id = $journal AND review_key = $key", ("$revision", revision), ("$updated", now), ("$journal", journalId), ("$key", key));
            InsertTradeReviewHistory(connection, transaction, journalId, key, revision, "tag merge undone", SerializeReview(previous), SerializeReview(ReadTradeReviewOrHistoryRevision(connection, transaction, journalId, key)), $"Undid merging {source.Name}.", now);
        }
        foreach (var (date, previous) in daysBefore)
        {
            WriteDailyTagRevision(connection, transaction, journalId, date, previous.Revision + 1);
            InsertDailyTagHistory(connection, transaction, journalId, date, previous.Revision + 1, "tag merge undone", previous, ReadDailyTagSet(connection, transaction, journalId, date, after));
        }
        transaction.Commit();
        return restored;
    }

    public void DeleteTag(Guid journalId, Guid tagId, int expectedRevision)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var catalog = ReadTagCatalog(connection, transaction, journalId);
        var tag = catalog.Get(tagId) ?? throw new InvalidOperationException("Tag was not found.");
        if (tag.Revision != expectedRevision) throw new ArgumentException("This tag changed in another window. Reload it and try again.");
        if (tag.MergedIntoId.HasValue || catalog.All.Any(other => other.MergedIntoId == tagId)
            || ReadLinkedTagIds(connection, transaction, journalId, "trade_review_tags").Contains(tagId)
            || ReadLinkedTagIds(connection, transaction, journalId, "daily_tag_links").Contains(tagId))
            throw new ArgumentException("Only an unused tag can be deleted. Archive it to keep its history and hide it from entry.");
        TagExecute(connection, transaction, "DELETE FROM tags WHERE journal_id = $journal AND id = $tag", ("$journal", journalId), ("$tag", tagId));
        InsertTagHistory(connection, transaction, tag with { Revision = tag.Revision + 1 }, "deleted", JsonSerializer.Serialize(tag), "{}");
        transaction.Commit();
    }

    private Tag MutateTag(Guid journalId, Guid tagId, int expectedRevision, string action, Func<SqliteConnection, SqliteTransaction, TagCatalog, Tag, Tag> change)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var catalog = ReadTagCatalog(connection, transaction, journalId);
        var current = catalog.Get(tagId) ?? throw new InvalidOperationException("Tag was not found.");
        if (current.Revision != expectedRevision) throw new ArgumentException("This tag changed in another window. Reload it and try again.");
        if (current.MergedIntoId.HasValue) throw new ArgumentException("Undo the merge before changing this tag.");
        var updated = WriteTag(connection, transaction, current, change(connection, transaction, catalog, current), action);
        transaction.Commit();
        return updated;
    }

    private static Tag WriteTag(SqliteConnection connection, SqliteTransaction transaction, Tag before, Tag after, string action)
    {
        var saved = after with { Revision = before.Revision + 1 };
        TagExecute(connection, transaction, "UPDATE tags SET name = $name, name_key = $key, description = $description, known = $known, scope = $scope, archived = $archived, merged_into_id = $merged, revision = $revision, updated_utc = $updated WHERE journal_id = $journal AND id = $id",
            ("$name", saved.Name), ("$key", TagNames.Key(saved.Name)), ("$description", saved.Description), ("$known", saved.Known.ToString()), ("$scope", saved.Scope.ToString()), ("$archived", saved.Archived ? 1 : 0), ("$merged", saved.MergedIntoId), ("$revision", saved.Revision), ("$updated", DateTimeOffset.UtcNow), ("$journal", saved.JournalId), ("$id", saved.Id));
        InsertTagHistory(connection, transaction, saved, action, JsonSerializer.Serialize(before), JsonSerializer.Serialize(saved));
        return saved;
    }

    private static Tag InsertTag(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, TagDraft draft, string action)
    {
        var tag = new Tag { Id = Guid.NewGuid(), JournalId = journalId, Name = draft.Name!, Description = draft.Description ?? string.Empty, Known = draft.Known, Scope = draft.Scope, Revision = 1 };
        TagExecute(connection, transaction, "INSERT INTO tags (id, journal_id, name, name_key, description, known, scope, revision, created_utc, updated_utc) VALUES ($id, $journal, $name, $key, $description, $known, $scope, 1, $now, $now)",
            ("$id", tag.Id), ("$journal", journalId), ("$name", tag.Name), ("$key", TagNames.Key(tag.Name)), ("$description", tag.Description), ("$known", tag.Known.ToString()), ("$scope", tag.Scope.ToString()), ("$now", DateTimeOffset.UtcNow));
        InsertTagHistory(connection, transaction, tag, action, "{}", JsonSerializer.Serialize(tag));
        return tag;
    }

    private static void InsertTagHistory(SqliteConnection connection, SqliteTransaction transaction, Tag tag, string action, string before, string after) =>
        TagExecute(connection, transaction, "INSERT INTO tag_history (id, journal_id, tag_id, revision, action, before_json, after_json, created_utc) VALUES ($id, $journal, $tag, $revision, $action, $before, $after, $now)",
            ("$id", Guid.NewGuid()), ("$journal", tag.JournalId), ("$tag", tag.Id), ("$revision", tag.Revision), ("$action", action), ("$before", before), ("$after", after), ("$now", DateTimeOffset.UtcNow));

    private static TagDraft ValidateTagDraft(TagDraft draft)
    {
        var name = TagNames.Clean(draft.Name);
        var description = (draft.Description ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (name.Length == 0 || TagNames.Key(name).Length == 0) throw new ArgumentException("Enter a tag name.");
        if (name.Length > Tag.NameMaxLength) throw new ArgumentException($"A tag name can be at most {Tag.NameMaxLength} characters.");
        if (name.Contains(',') || name.Any(char.IsControl)) throw new ArgumentException("A tag name cannot contain commas. Create one tag at a time.");
        if (description.Length == 0) throw new ArgumentException("Describe when this tag applies.");
        if (description.Length > Tag.DescriptionMaxLength) throw new ArgumentException($"The description can be at most {Tag.DescriptionMaxLength} characters.");
        if (draft.Known == TagKnown.Unset || !Enum.IsDefined(draft.Known)) throw new ArgumentException("Choose when this tag is known.");
        if (!Enum.IsDefined(draft.Scope)) throw new ArgumentException("Choose what this tag applies to.");
        return draft with { Name = name, Description = description };
    }

    private static void EnsureNameAvailable(TagCatalog catalog, string name, IReadOnlyCollection<Guid>? confirmedDifferent, Guid? selfId)
    {
        if (catalog.Owner(name) is { } owner && owner.Id != selfId)
        {
            var existing = catalog.Resolve(owner.Id) ?? owner;
            throw new TagNameException($"\"{existing.Name}\" already covers that name.", existing, Array.Empty<Tag>());
        }
        var unconfirmed = catalog.Similar(name, selfId).Where(tag => confirmedDifferent?.Contains(tag.Id) != true).ToArray();
        if (unconfirmed.Length > 0) throw new TagNameException("Confirm that this is different from the similar tag.", null, unconfirmed);
    }

    private static TagCatalog ReadTagCatalog(SqliteConnection connection, SqliteTransaction? transaction, Guid journalId)
    {
        var aliases = new List<(Guid TagId, string Alias)>();
        using (var command = TagSql(connection, transaction, "SELECT tag_id, alias FROM tag_aliases WHERE journal_id = $journal ORDER BY alias", ("$journal", journalId)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) aliases.Add((Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        var aliasesByTag = aliases.ToLookup(item => item.TagId, item => item.Alias);

        using var tagsCommand = TagSql(connection, transaction, "SELECT id, name, description, known, scope, archived, merged_into_id, revision FROM tags WHERE journal_id = $journal", ("$journal", journalId));
        using var tagsReader = tagsCommand.ExecuteReader();
        var tags = new List<Tag>();
        while (tagsReader.Read())
        {
            var id = Guid.Parse(tagsReader.GetString(0));
            tags.Add(new Tag
            {
                Id = id,
                JournalId = journalId,
                Name = tagsReader.GetString(1),
                Description = tagsReader.GetString(2),
                Known = Enum.Parse<TagKnown>(tagsReader.GetString(3)),
                Scope = Enum.Parse<TagScope>(tagsReader.GetString(4)),
                Archived = tagsReader.GetInt32(5) != 0,
                MergedIntoId = tagsReader.IsDBNull(6) ? null : Guid.Parse(tagsReader.GetString(6)),
                Revision = tagsReader.GetInt32(7),
                Aliases = aliasesByTag[id].ToArray()
            });
        }
        return new TagCatalog(tags);
    }

    private static IReadOnlyDictionary<T, IReadOnlyList<Tag>> ReadResolvedLinks<T>(SqliteCommand command, TagCatalog catalog, Func<string, T> owner) where T : notnull
    {
        var links = new Dictionary<T, List<Tag>>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (catalog.Resolve(Guid.Parse(reader.GetString(1))) is not { } tag) continue;
            var key = owner(reader.GetString(0));
            if (!links.TryGetValue(key, out var list)) links[key] = list = new List<Tag>();
            if (list.All(existing => existing.Id != tag.Id)) list.Add(tag);
        }
        return links.ToDictionary(item => item.Key, item => (IReadOnlyList<Tag>)item.Value);
    }

    /// <summary>The current tags on every review in the journal, or on just one.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<Tag>> ReadReviewTags(SqliteConnection connection, SqliteTransaction? transaction, Guid journalId, string? reviewKey = null)
    {
        var catalog = ReadTagCatalog(connection, transaction, journalId);
        var filter = reviewKey is null ? string.Empty : " AND review_key = $key";
        using var command = TagSql(connection, transaction, $"SELECT review_key, tag_id FROM trade_review_tags WHERE journal_id = $journal{filter} ORDER BY review_key, position, tag_id", ("$journal", journalId));
        if (reviewKey is not null) command.Parameters.AddWithValue("$key", reviewKey);
        return ReadResolvedLinks(command, catalog, value => value);
    }

    private static DailyTagSet ReadDailyTagSet(SqliteConnection connection, SqliteTransaction? transaction, Guid journalId, DateOnly date, TagCatalog catalog)
    {
        var day = DayText(date);
        var revision = Convert.ToInt32(TagScalar(connection, transaction, "SELECT revision FROM daily_tag_sets WHERE journal_id = $journal AND review_date = $date", ("$journal", journalId), ("$date", day)) ?? 0, CultureInfo.InvariantCulture);
        using var command = TagSql(connection, transaction, "SELECT review_date, tag_id FROM daily_tag_links WHERE journal_id = $journal AND review_date = $date ORDER BY position, tag_id", ("$journal", journalId), ("$date", day));
        var tags = ReadResolvedLinks(command, catalog, value => value).GetValueOrDefault(day) ?? Array.Empty<Tag>();
        return new DailyTagSet(date, revision, tags.Select(tag => new TagReference(tag.Id, tag.Name)).ToArray());
    }

    private static IReadOnlyList<Guid> ValidateSelection(TagCatalog catalog, IEnumerable<Guid> requested, IReadOnlySet<Guid> alreadyOn, TagTarget target)
    {
        var selected = new List<Guid>();
        foreach (var id in requested)
        {
            var tag = catalog.Resolve(id) ?? throw new ArgumentException("One of these tags no longer exists. Reload the page.");
            if (selected.Contains(tag.Id)) continue;
            if (!alreadyOn.Contains(tag.Id))
            {
                if (tag.Archived) throw new ArgumentException($"{tag.Name} is archived. Restore it on the Tags page first.");
                if (!tag.AppliesTo(target)) throw new ArgumentException($"{tag.Name} does not apply to {(target == TagTarget.Day ? "days" : "trades")}.");
            }
            selected.Add(tag.Id);
        }
        if (selected.Count > Tag.MaxPerItem && selected.Count > alreadyOn.Count) throw new ArgumentException($"Choose at most {Tag.MaxPerItem} tags.");
        return selected;
    }

    /// <summary>Makes the stored links resolve to <paramref name="selected"/> while keeping original links, so merges stay undoable.</summary>
    private static void WriteTagLinks(SqliteConnection connection, SqliteTransaction transaction, TagCatalog catalog, IReadOnlyList<Guid> selected, string table, string ownerColumn, Guid journalId, string owner)
    {
        var present = new HashSet<Guid>();
        var stale = new List<Guid>();
        var next = 0;
        using (var command = TagSql(connection, transaction, $"SELECT tag_id, position FROM {table} WHERE journal_id = $journal AND {ownerColumn} = $owner", ("$journal", journalId), ("$owner", owner)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                next = Math.Max(next, reader.GetInt32(1) + 1);
                if (catalog.Resolve(id) is { } tag && selected.Contains(tag.Id)) present.Add(tag.Id);
                else stale.Add(id);
            }
        }
        foreach (var id in stale)
            TagExecute(connection, transaction, $"DELETE FROM {table} WHERE journal_id = $journal AND {ownerColumn} = $owner AND tag_id = $tag", ("$journal", journalId), ("$owner", owner), ("$tag", id));
        foreach (var id in selected.Where(id => !present.Contains(id)))
            TagExecute(connection, transaction, $"INSERT INTO {table} (journal_id, {ownerColumn}, tag_id, position, assigned_utc) VALUES ($journal, $owner, $tag, $position, $now)",
                ("$journal", journalId), ("$owner", owner), ("$tag", id), ("$position", next++), ("$now", DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Maps a saved revision's tags onto today's. A name with no tag (older history, or a deleted tag)
    /// comes back as a tag that needs review.
    /// </summary>
    private static IReadOnlyList<Guid> RestoreTagSelection(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, TradeReviewAnnotation saved)
    {
        var catalog = ReadTagCatalog(connection, transaction, journalId);
        var selected = new List<Guid>();
        for (var index = 0; index < saved.Tags.Count; index++)
        {
            var name = TagNames.Clean(saved.Tags[index]);
            var tag = index < saved.TagIds.Count ? catalog.Resolve(saved.TagIds[index]) : null;
            if (tag is null && TagNames.Key(name).Length > 0)
            {
                tag = catalog.Find(name);
                if (tag is null)
                {
                    tag = InsertTag(connection, transaction, journalId, new TagDraft(name, string.Empty, TagKnown.Unset, TagScope.Trade), "restored from history");
                    catalog = ReadTagCatalog(connection, transaction, journalId);
                }
            }
            if (tag is not null && !selected.Contains(tag.Id)) selected.Add(tag.Id);
        }
        return selected;
    }

    private static IReadOnlyList<Guid> ParseTagIds(string text)
    {
        var ids = new List<Guid>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id)) throw new ArgumentException("The tag selection could not be read. Reload the page.");
            ids.Add(id);
        }
        return ids;
    }

    private static bool ResolvesThrough(TagCatalog catalog, Tag tag, Guid throughId)
    {
        for (var hops = 0; hops <= catalog.All.Count; hops++)
        {
            if (tag.Id == throughId) return true;
            if (tag.MergedIntoId is not { } next || catalog.Get(next) is not { } parent) return false;
            tag = parent;
        }
        return false;
    }

    private static HashSet<Guid> ReadLinkedTagIds(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, string table)
    {
        using var command = TagSql(connection, transaction, $"SELECT DISTINCT tag_id FROM {table} WHERE journal_id = $journal", ("$journal", journalId));
        using var reader = command.ExecuteReader();
        var ids = new HashSet<Guid>();
        while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        return ids;
    }

    private static HashSet<string> ReadLinkOwners(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, string table, string ownerColumn, IReadOnlySet<Guid> tagIds)
    {
        using var command = TagSql(connection, transaction, $"SELECT {ownerColumn}, tag_id FROM {table} WHERE journal_id = $journal", ("$journal", journalId));
        using var reader = command.ExecuteReader();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            if (tagIds.Contains(Guid.Parse(reader.GetString(1)))) owners.Add(reader.GetString(0));
        return owners;
    }

    private static void WriteDailyTagRevision(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, DateOnly date, int revision) =>
        TagExecute(connection, transaction, "INSERT INTO daily_tag_sets (journal_id, review_date, revision, updated_utc) VALUES ($journal, $date, $revision, $now) ON CONFLICT(journal_id, review_date) DO UPDATE SET revision = excluded.revision, updated_utc = excluded.updated_utc",
            ("$journal", journalId), ("$date", DayText(date)), ("$revision", revision), ("$now", DateTimeOffset.UtcNow));

    private static void InsertDailyTagHistory(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, DateOnly date, int revision, string action, object before, object after) =>
        TagExecute(connection, transaction, "INSERT INTO daily_tag_history (id, journal_id, review_date, revision, action, before_json, after_json, created_utc) VALUES ($id, $journal, $date, $revision, $action, $before, $after, $now)",
            ("$id", Guid.NewGuid()), ("$journal", journalId), ("$date", DayText(date)), ("$revision", revision), ("$action", action), ("$before", JsonSerializer.Serialize(before)), ("$after", JsonSerializer.Serialize(after)), ("$now", DateTimeOffset.UtcNow));

    private static string DayText(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static SqliteCommand TagSql(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value switch
            {
                null => DBNull.Value,
                Guid id => id.ToString("D"),
                DateTimeOffset time => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                _ => value
            });
        return command;
    }

    private static void TagExecute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        using var command = TagSql(connection, transaction, sql, values);
        command.ExecuteNonQuery();
    }

    private static object? TagScalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] values)
    {
        using var command = TagSql(connection, transaction, sql, values);
        return command.ExecuteScalar() is { } value and not DBNull ? value : null;
    }
}
