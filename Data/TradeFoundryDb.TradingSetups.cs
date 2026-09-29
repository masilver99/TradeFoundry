using System.Globalization;
using Microsoft.Data.Sqlite;
using TradeFoundry.Core;

namespace TradeFoundry.Data;

public sealed partial class TradeFoundryDb
{
    public IReadOnlyList<TradingSetupSummary> GetTradingSetupSummaries(Guid journalId, bool includeInactive = true)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT s.id, s.journal_id, s.name, s.short_description, s.category, s.active,
                   v.id, COALESCE(v.version, 0),
                   (SELECT COUNT(*) FROM trading_setup_versions historical WHERE historical.setup_id = s.id),
                   COUNT(c.id),
                   (SELECT COUNT(DISTINCT usage.trade_id)
                       FROM trade_setups usage
                       INNER JOIN trading_setup_versions used_version ON used_version.id = usage.setup_version_id
                       WHERE used_version.setup_id = s.id AND used_version.journal_id = s.journal_id),
                   s.updated_utc
            FROM trading_setups s
            LEFT JOIN trading_setup_versions v ON v.setup_id = s.id
                AND v.version = (SELECT MAX(latest.version) FROM trading_setup_versions latest WHERE latest.setup_id = s.id)
            LEFT JOIN trading_setup_criteria c ON c.setup_version_id = v.id AND c.active = 1
            WHERE s.journal_id = $journal{(includeInactive ? string.Empty : " AND s.active = 1")}
            GROUP BY s.id, s.journal_id, s.name, s.short_description, s.category, s.active, v.id, v.version, s.updated_utc
            ORDER BY s.active DESC, s.name COLLATE NOCASE, s.id
            """;
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var summaries = new List<TradingSetupSummary>();
        while (reader.Read()) summaries.Add(ReadTradingSetupSummary(reader));
        return summaries;
    }

    public TradingSetup? GetTradingSetup(Guid journalId, Guid setupId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, name, short_description, detailed_description, category, active, created_utc, updated_utc FROM trading_setups WHERE id = $id AND journal_id = $journal";
        command.Parameters.AddWithValue("$id", setupId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTradingSetup(reader) : null;
    }

    public TradingSetupDetail? GetTradingSetupDetail(Guid journalId, Guid setupId)
    {
        var setup = GetTradingSetup(journalId, setupId);
        if (setup is null) return null;

        using var connection = OpenConnection();
        using var versionsCommand = connection.CreateCommand();
        versionsCommand.CommandText = "SELECT id, journal_id, setup_id, version, notes, created_utc FROM trading_setup_versions WHERE journal_id = $journal AND setup_id = $setup ORDER BY version DESC";
        versionsCommand.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        versionsCommand.Parameters.AddWithValue("$setup", setupId.ToString("D"));
        using var versionsReader = versionsCommand.ExecuteReader();
        var versions = new List<TradingSetupVersion>();
        while (versionsReader.Read()) versions.Add(ReadTradingSetupVersion(versionsReader));

        var completeVersions = new List<TradingSetupVersion>(versions.Count);
        foreach (var version in versions)
        {
            using var criteriaCommand = connection.CreateCommand();
            criteriaCommand.CommandText = "SELECT id, journal_id, setup_version_id, name, description, criterion_type, display_order, active, evaluation_mode, rule_metadata_json, stage, created_utc FROM trading_setup_criteria WHERE journal_id = $journal AND setup_version_id = $version ORDER BY display_order, id";
            criteriaCommand.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            criteriaCommand.Parameters.AddWithValue("$version", version.Id.ToString("D"));
            using var criteriaReader = criteriaCommand.ExecuteReader();
            var criteria = new List<TradingSetupCriterion>();
            while (criteriaReader.Read()) criteria.Add(ReadTradingSetupCriterion(criteriaReader));
            completeVersions.Add(new TradingSetupVersion
            {
                Id = version.Id,
                JournalId = version.JournalId,
                SetupId = version.SetupId,
                Version = version.Version,
                Notes = version.Notes,
                CreatedUtc = version.CreatedUtc,
                Criteria = criteria
            });
        }

        return new TradingSetupDetail { Setup = setup, Versions = completeVersions };
    }

    public TradingSetupDetail CreateTradingSetup(Guid journalId, TradingSetupDraft draft, IReadOnlyList<TradingSetupCriterionDraft> criteria)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        var now = DateTimeOffset.UtcNow;
        var setupId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var setup = connection.CreateCommand())
        {
            setup.Transaction = transaction;
            setup.CommandText = "INSERT INTO trading_setups (id, journal_id, name, short_description, detailed_description, category, active, created_utc, updated_utc) VALUES ($id, $journal, $name, $short, $detailed, $category, 1, $created, $updated)";
            setup.Parameters.AddWithValue("$id", setupId.ToString("D"));
            setup.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            setup.Parameters.AddWithValue("$name", draft.Name);
            setup.Parameters.AddWithValue("$short", draft.ShortDescription);
            setup.Parameters.AddWithValue("$detailed", draft.DetailedDescription);
            setup.Parameters.AddWithValue("$category", draft.Category);
            setup.Parameters.AddWithValue("$created", now.ToString("O", CultureInfo.InvariantCulture));
            setup.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            setup.ExecuteNonQuery();
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "INSERT INTO trading_setup_versions (id, journal_id, setup_id, version, notes, created_utc) VALUES ($id, $journal, $setup, 1, '', $created)";
            version.Parameters.AddWithValue("$id", versionId.ToString("D"));
            version.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            version.Parameters.AddWithValue("$setup", setupId.ToString("D"));
            version.Parameters.AddWithValue("$created", now.ToString("O", CultureInfo.InvariantCulture));
            version.ExecuteNonQuery();
        }

        InsertTradingSetupCriteria(connection, transaction, journalId, versionId, criteria, now);
        transaction.Commit();
        return GetTradingSetupDetail(journalId, setupId) ?? throw new InvalidOperationException("The setup could not be read after creation.");
    }

    public TradingSetup? UpdateTradingSetupMetadata(Guid journalId, Guid setupId, TradingSetupDraft draft)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE trading_setups SET name = $name, short_description = $short, detailed_description = $detailed, category = $category, updated_utc = $updated WHERE id = $id AND journal_id = $journal";
        command.Parameters.AddWithValue("$name", draft.Name);
        command.Parameters.AddWithValue("$short", draft.ShortDescription);
        command.Parameters.AddWithValue("$detailed", draft.DetailedDescription);
        command.Parameters.AddWithValue("$category", draft.Category);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", setupId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        return command.ExecuteNonQuery() == 0 ? null : GetTradingSetup(journalId, setupId);
    }

    public bool SetTradingSetupActive(Guid journalId, Guid setupId, bool active)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE trading_setups SET active = $active, updated_utc = $updated WHERE id = $id AND journal_id = $journal";
        command.Parameters.AddWithValue("$active", active ? 1 : 0);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", setupId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    public TradingSetupDetail CreateTradingSetupVersion(Guid journalId, Guid setupId, string notes, IReadOnlyList<TradingSetupCriterionDraft> criteria)
    {
        _ = GetTradingSetup(journalId, setupId) ?? throw new InvalidOperationException("The setup was not found.");
        var now = DateTimeOffset.UtcNow;
        var versionId = Guid.NewGuid();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        int versionNumber;
        using (var next = connection.CreateCommand())
        {
            next.Transaction = transaction;
            next.CommandText = "SELECT COALESCE(MAX(version), 0) + 1 FROM trading_setup_versions WHERE journal_id = $journal AND setup_id = $setup";
            next.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            next.Parameters.AddWithValue("$setup", setupId.ToString("D"));
            versionNumber = Convert.ToInt32(next.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "INSERT INTO trading_setup_versions (id, journal_id, setup_id, version, notes, created_utc) VALUES ($id, $journal, $setup, $version, $notes, $created)";
            version.Parameters.AddWithValue("$id", versionId.ToString("D"));
            version.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            version.Parameters.AddWithValue("$setup", setupId.ToString("D"));
            version.Parameters.AddWithValue("$version", versionNumber);
            version.Parameters.AddWithValue("$notes", notes);
            version.Parameters.AddWithValue("$created", now.ToString("O", CultureInfo.InvariantCulture));
            version.ExecuteNonQuery();
        }

        InsertTradingSetupCriteria(connection, transaction, journalId, versionId, criteria, now);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE trading_setups SET updated_utc = $updated WHERE id = $setup AND journal_id = $journal";
            update.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$setup", setupId.ToString("D"));
            update.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return GetTradingSetupDetail(journalId, setupId) ?? throw new InvalidOperationException("The setup version could not be read after creation.");
    }

    public TradingSetupDetail UpdateCurrentTradingSetupVersion(Guid journalId, Guid setupId, string notes, IReadOnlyList<TradingSetupCriterionDraft> criteria)
    {
        _ = GetTradingSetup(journalId, setupId) ?? throw new InvalidOperationException("The setup was not found.");
        var now = DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Guid? versionId = null;
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT id FROM trading_setup_versions WHERE journal_id = $journal AND setup_id = $setup ORDER BY version DESC LIMIT 1";
            current.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            current.Parameters.AddWithValue("$setup", setupId.ToString("D"));
            var value = current.ExecuteScalar();
            if (value is string id) versionId = Guid.Parse(id);
        }

        if (!versionId.HasValue)
        {
            transaction.Rollback();
            throw new InvalidOperationException("The setup has no current version.");
        }

        using (var usage = connection.CreateCommand())
        {
            usage.Transaction = transaction;
            usage.CommandText = """
                SELECT COUNT(*)
                FROM trade_setups association
                INNER JOIN trading_setup_versions used_version ON used_version.id = association.setup_version_id
                WHERE used_version.journal_id = $journal AND used_version.setup_id = $setup
                """;
            usage.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            usage.Parameters.AddWithValue("$setup", setupId.ToString("D"));
            if (Convert.ToInt64(usage.ExecuteScalar(), CultureInfo.InvariantCulture) > 0)
            {
                transaction.Rollback();
                throw new InvalidOperationException("This setup is already used by a trade. Create a new version to preserve historical evaluations.");
            }
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM trading_setup_criteria WHERE journal_id = $journal AND setup_version_id = $version";
            delete.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            delete.Parameters.AddWithValue("$version", versionId.Value.ToString("D"));
            delete.ExecuteNonQuery();
        }

        InsertTradingSetupCriteria(connection, transaction, journalId, versionId.Value, criteria, now);
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE trading_setup_versions SET notes = $notes WHERE id = $version AND journal_id = $journal";
            update.Parameters.AddWithValue("$notes", notes);
            update.Parameters.AddWithValue("$version", versionId.Value.ToString("D"));
            update.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            update.ExecuteNonQuery();
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE trading_setups SET updated_utc = $updated WHERE id = $setup AND journal_id = $journal";
            update.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            update.Parameters.AddWithValue("$setup", setupId.ToString("D"));
            update.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            update.ExecuteNonQuery();
        }
        transaction.Commit();
        return GetTradingSetupDetail(journalId, setupId) ?? throw new InvalidOperationException("The current setup version could not be read after updating.");
    }

    public bool MoveTradingSetupCriterion(Guid journalId, Guid criterionId, bool moveUp)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Guid? versionId = null;
        var currentOrder = 0;
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT setup_version_id, display_order FROM trading_setup_criteria WHERE id = $criterion AND journal_id = $journal";
            current.Parameters.AddWithValue("$criterion", criterionId.ToString("D"));
            current.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            using var reader = current.ExecuteReader();
            if (reader.Read())
            {
                versionId = Guid.Parse(reader.GetString(0));
                currentOrder = reader.GetInt32(1);
            }
        }
        if (!versionId.HasValue)
        {
            transaction.Rollback();
            return false;
        }

        Guid? neighborId = null;
        var neighborOrder = 0;
        using (var neighbor = connection.CreateCommand())
        {
            neighbor.Transaction = transaction;
            var comparison = moveUp ? "<" : ">";
            var ordering = moveUp ? "DESC" : "ASC";
            neighbor.CommandText = $"SELECT id, display_order FROM trading_setup_criteria WHERE journal_id = $journal AND setup_version_id = $version AND display_order {comparison} $order ORDER BY display_order {ordering}, id {ordering} LIMIT 1";
            neighbor.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            neighbor.Parameters.AddWithValue("$version", versionId.Value.ToString("D"));
            neighbor.Parameters.AddWithValue("$order", currentOrder);
            using var reader = neighbor.ExecuteReader();
            if (reader.Read())
            {
                neighborId = Guid.Parse(reader.GetString(0));
                neighborOrder = reader.GetInt32(1);
            }
        }
        if (!neighborId.HasValue)
        {
            transaction.Rollback();
            return false;
        }

        using (var updateCurrent = connection.CreateCommand())
        {
            updateCurrent.Transaction = transaction;
            updateCurrent.CommandText = "UPDATE trading_setup_criteria SET display_order = $order WHERE id = $id AND journal_id = $journal";
            updateCurrent.Parameters.AddWithValue("$order", neighborOrder);
            updateCurrent.Parameters.AddWithValue("$id", criterionId.ToString("D"));
            updateCurrent.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            updateCurrent.ExecuteNonQuery();
        }
        using (var updateNeighbor = connection.CreateCommand())
        {
            updateNeighbor.Transaction = transaction;
            updateNeighbor.CommandText = "UPDATE trading_setup_criteria SET display_order = $order WHERE id = $id AND journal_id = $journal";
            updateNeighbor.Parameters.AddWithValue("$order", currentOrder);
            updateNeighbor.Parameters.AddWithValue("$id", neighborId.Value.ToString("D"));
            updateNeighbor.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            updateNeighbor.ExecuteNonQuery();
        }
        transaction.Commit();
        return true;
    }

    public IReadOnlyList<TradeSetup> GetTradeSetupsForTrade(Guid journalId, Guid tradeId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, trade_id, setup_version_id, role, note, created_utc, updated_utc FROM trade_setups WHERE journal_id = $journal AND trade_id = $trade ORDER BY CASE role WHEN 'primary' THEN 0 ELSE 1 END, created_utc, id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$trade", tradeId.ToString("D"));
        using var reader = command.ExecuteReader();
        var result = new List<TradeSetup>();
        while (reader.Read()) result.Add(ReadTradeSetup(reader));
        return result;
    }

    public IReadOnlyList<TradeSetup> GetTradeSetupsForJournal(Guid journalId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, trade_id, setup_version_id, role, note, created_utc, updated_utc FROM trade_setups WHERE journal_id = $journal ORDER BY trade_id, CASE role WHEN 'primary' THEN 0 ELSE 1 END, created_utc, id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var result = new List<TradeSetup>();
        while (reader.Read()) result.Add(ReadTradeSetup(reader));
        return result;
    }

    public TradeSetup? GetTradeSetup(Guid journalId, Guid tradeSetupId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, trade_id, setup_version_id, role, note, created_utc, updated_utc FROM trade_setups WHERE journal_id = $journal AND id = $id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$id", tradeSetupId.ToString("D"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTradeSetup(reader) : null;
    }

    public IReadOnlyList<TradeSetupCriterionEvaluation> GetTradeSetupCriterionEvaluations(Guid journalId, Guid tradeSetupId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, trade_id, trade_setup_id, setup_version_id, criterion_id, evaluation_state, note, evaluation_source, evaluated_utc FROM trade_setup_criterion_evaluations WHERE journal_id = $journal AND trade_setup_id = $tradeSetup ORDER BY criterion_id";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$tradeSetup", tradeSetupId.ToString("D"));
        using var reader = command.ExecuteReader();
        var result = new List<TradeSetupCriterionEvaluation>();
        while (reader.Read()) result.Add(ReadTradeSetupCriterionEvaluation(reader));
        return result;
    }

    public IReadOnlyList<TradeSetupCriterionEvaluation> GetTradeSetupCriterionEvaluationsForJournal(Guid journalId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, journal_id, trade_id, trade_setup_id, setup_version_id, criterion_id, evaluation_state, note, evaluation_source, evaluated_utc FROM trade_setup_criterion_evaluations WHERE journal_id = $journal";
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        using var reader = command.ExecuteReader();
        var result = new List<TradeSetupCriterionEvaluation>();
        while (reader.Read()) result.Add(ReadTradeSetupCriterionEvaluation(reader));
        return result;
    }

    public TradeSetup AttachTradeSetup(Guid journalId, Guid tradeId, Guid setupVersionId, TradeSetupRole role)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        var now = DateTimeOffset.UtcNow;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var validate = connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText = "SELECT COUNT(*) FROM trades t JOIN trading_setup_versions v ON v.journal_id = t.journal_id WHERE t.id = $trade AND t.journal_id = $journal AND v.id = $version";
            validate.Parameters.AddWithValue("$trade", tradeId.ToString("D"));
            validate.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            validate.Parameters.AddWithValue("$version", setupVersionId.ToString("D"));
            if (Convert.ToInt32(validate.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                throw new InvalidOperationException("The trade or setup version was not found in this journal.");
        }

        if (role == TradeSetupRole.Primary)
            DemotePrimaryTradeSetups(connection, transaction, journalId, tradeId, now);

        var associationId = Guid.NewGuid();
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id FROM trade_setups WHERE journal_id = $journal AND trade_id = $trade AND setup_version_id = $version";
            existing.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            existing.Parameters.AddWithValue("$trade", tradeId.ToString("D"));
            existing.Parameters.AddWithValue("$version", setupVersionId.ToString("D"));
            var value = existing.ExecuteScalar();
            if (value is string id && Guid.TryParse(id, out var parsed)) associationId = parsed;
        }

        using (var association = connection.CreateCommand())
        {
            association.Transaction = transaction;
            association.CommandText = "INSERT INTO trade_setups (id, journal_id, trade_id, setup_version_id, role, note, created_utc, updated_utc) VALUES ($id, $journal, $trade, $version, $role, '', $created, $updated) ON CONFLICT(trade_id, setup_version_id) DO UPDATE SET role = excluded.role, updated_utc = excluded.updated_utc";
            association.Parameters.AddWithValue("$id", associationId.ToString("D"));
            association.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            association.Parameters.AddWithValue("$trade", tradeId.ToString("D"));
            association.Parameters.AddWithValue("$version", setupVersionId.ToString("D"));
            association.Parameters.AddWithValue("$role", ToStorage(role));
            association.Parameters.AddWithValue("$created", now.ToString("O", CultureInfo.InvariantCulture));
            association.Parameters.AddWithValue("$updated", now.ToString("O", CultureInfo.InvariantCulture));
            association.ExecuteNonQuery();
        }

        using (var evaluations = connection.CreateCommand())
        {
            evaluations.Transaction = transaction;
            evaluations.CommandText = "INSERT INTO trade_setup_criterion_evaluations (id, journal_id, trade_id, trade_setup_id, setup_version_id, criterion_id, evaluation_state, note, evaluation_source, evaluated_utc) SELECT lower(hex(randomblob(16))), $journal, $trade, $tradeSetup, $version, c.id, 'unknown', '', 'manual', $evaluated FROM trading_setup_criteria c WHERE c.journal_id = $journal AND c.setup_version_id = $version ON CONFLICT(trade_setup_id, criterion_id) DO NOTHING";
            evaluations.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            evaluations.Parameters.AddWithValue("$trade", tradeId.ToString("D"));
            evaluations.Parameters.AddWithValue("$tradeSetup", associationId.ToString("D"));
            evaluations.Parameters.AddWithValue("$version", setupVersionId.ToString("D"));
            evaluations.Parameters.AddWithValue("$evaluated", now.ToString("O", CultureInfo.InvariantCulture));
            evaluations.ExecuteNonQuery();
        }
        transaction.Commit();
        return GetTradeSetup(journalId, associationId) ?? throw new InvalidOperationException("The trade setup could not be read after saving.");
    }

    public bool UpdateTradeSetupRole(Guid journalId, Guid tradeSetupId, TradeSetupRole role)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Guid? tradeId = null;
        using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT trade_id FROM trade_setups WHERE id = $id AND journal_id = $journal";
            lookup.Parameters.AddWithValue("$id", tradeSetupId.ToString("D"));
            lookup.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            var value = lookup.ExecuteScalar();
            if (value is string text && Guid.TryParse(text, out var parsed)) tradeId = parsed;
        }
        if (!tradeId.HasValue)
        {
            transaction.Rollback();
            return false;
        }
        if (role == TradeSetupRole.Primary)
            DemotePrimaryTradeSetups(connection, transaction, journalId, tradeId.Value, DateTimeOffset.UtcNow, tradeSetupId);
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE trade_setups SET role = $role, updated_utc = $updated WHERE id = $id AND journal_id = $journal";
        update.Parameters.AddWithValue("$role", ToStorage(role));
        update.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        update.Parameters.AddWithValue("$id", tradeSetupId.ToString("D"));
        update.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        var saved = update.ExecuteNonQuery() > 0;
        transaction.Commit();
        return saved;
    }

    public bool RemoveTradeSetup(Guid journalId, Guid tradeSetupId)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM trade_setups WHERE id = $id AND journal_id = $journal";
        command.Parameters.AddWithValue("$id", tradeSetupId.ToString("D"));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        return command.ExecuteNonQuery() > 0;
    }

    public void SaveTradeSetupCriterionEvaluations(Guid journalId, Guid tradeSetupId, IReadOnlyList<TradeSetupCriterionEvaluation> evaluations)
    {
        _ = GetJournal(journalId) ?? throw new InvalidOperationException("Journal was not found.");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var evaluation in evaluations)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO trade_setup_criterion_evaluations (id, journal_id, trade_id, trade_setup_id, setup_version_id, criterion_id, evaluation_state, note, evaluation_source, evaluated_utc) VALUES ($id, $journal, $trade, $tradeSetup, $version, $criterion, $state, $note, $source, $evaluated) ON CONFLICT(trade_setup_id, criterion_id) DO UPDATE SET evaluation_state = excluded.evaluation_state, note = excluded.note, evaluation_source = excluded.evaluation_source, evaluated_utc = excluded.evaluated_utc";
            command.Parameters.AddWithValue("$id", evaluation.Id == Guid.Empty ? Guid.NewGuid().ToString("D") : evaluation.Id.ToString("D"));
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$trade", evaluation.TradeId.ToString("D"));
            command.Parameters.AddWithValue("$tradeSetup", tradeSetupId.ToString("D"));
            command.Parameters.AddWithValue("$version", evaluation.SetupVersionId.ToString("D"));
            command.Parameters.AddWithValue("$criterion", evaluation.CriterionId.ToString("D"));
            command.Parameters.AddWithValue("$state", ToStorage(evaluation.EvaluationState));
            command.Parameters.AddWithValue("$note", evaluation.Note);
            command.Parameters.AddWithValue("$source", ToStorage(evaluation.EvaluationSource));
            command.Parameters.AddWithValue("$evaluated", evaluation.EvaluatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static IReadOnlyList<DerivedTradeSetupSnapshot> SnapshotDerivedTradeSetups(SqliteConnection connection, SqliteTransaction transaction, Guid journalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ts.id, t.review_key, ts.setup_version_id, ts.role, ts.note, ts.created_utc, ts.updated_utc,
                   e.id, e.setup_version_id, e.criterion_id, e.evaluation_state, e.note, e.evaluation_source, e.evaluated_utc
            FROM trade_setups ts
            JOIN trades t ON t.id = ts.trade_id AND t.journal_id = ts.journal_id
            LEFT JOIN trade_setup_criterion_evaluations e ON e.trade_setup_id = ts.id
            WHERE ts.journal_id = $journal AND t.source_type = $source AND t.review_key <> ''
            ORDER BY ts.id, e.criterion_id
            """;
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$source", DerivedFillSource);
        using var reader = command.ExecuteReader();
        var snapshots = new Dictionary<Guid, DerivedTradeSetupSnapshot>();
        while (reader.Read())
        {
            var associationId = Guid.Parse(reader.GetString(0));
            if (!snapshots.TryGetValue(associationId, out var snapshot))
            {
                snapshot = new DerivedTradeSetupSnapshot
                {
                    ReviewKey = reader.GetString(1),
                    SetupVersionId = Guid.Parse(reader.GetString(2)),
                    Role = ParseTradeSetupRole(reader.GetString(3)),
                    Note = reader.GetString(4),
                    CreatedUtc = ParseDate(reader.GetString(5)),
                    UpdatedUtc = ParseDate(reader.GetString(6))
                };
                snapshots.Add(associationId, snapshot);
            }

            if (!reader.IsDBNull(7))
            {
                snapshot.Evaluations.Add(new DerivedTradeEvaluationSnapshot
                {
                    SetupVersionId = Guid.Parse(reader.GetString(8)),
                    CriterionId = Guid.Parse(reader.GetString(9)),
                    EvaluationState = ParseEvaluationState(reader.GetString(10)),
                    Note = reader.GetString(11),
                    EvaluationSource = ParseEvaluationSource(reader.GetString(12)),
                    EvaluatedUtc = ParseDate(reader.GetString(13))
                });
            }
        }
        return snapshots.Values.ToArray();
    }

    private static void RestoreDerivedTradeSetups(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, IReadOnlyList<DerivedTradeSetupSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return;

        var tradeIdsByReviewKey = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var trades = connection.CreateCommand())
        {
            trades.Transaction = transaction;
            trades.CommandText = "SELECT id, review_key FROM trades WHERE journal_id = $journal AND source_type = $source AND review_key <> ''";
            trades.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            trades.Parameters.AddWithValue("$source", DerivedFillSource);
            using var reader = trades.ExecuteReader();
            while (reader.Read()) tradeIdsByReviewKey[reader.GetString(1)] = reader.GetString(0);
        }

        foreach (var snapshot in snapshots.OrderBy(item => item.ReviewKey, StringComparer.Ordinal).ThenBy(item => item.Role))
        {
            if (!tradeIdsByReviewKey.TryGetValue(snapshot.ReviewKey, out var tradeId)) continue;
            var associationId = Guid.NewGuid().ToString("D");
            using (var association = connection.CreateCommand())
            {
                association.Transaction = transaction;
                association.CommandText = "INSERT INTO trade_setups (id, journal_id, trade_id, setup_version_id, role, note, created_utc, updated_utc) VALUES ($id, $journal, $trade, $version, $role, $note, $created, $updated)";
                association.Parameters.AddWithValue("$id", associationId);
                association.Parameters.AddWithValue("$journal", journalId.ToString("D"));
                association.Parameters.AddWithValue("$trade", tradeId);
                association.Parameters.AddWithValue("$version", snapshot.SetupVersionId.ToString("D"));
                association.Parameters.AddWithValue("$role", ToStorage(snapshot.Role));
                association.Parameters.AddWithValue("$note", snapshot.Note);
                association.Parameters.AddWithValue("$created", snapshot.CreatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                association.Parameters.AddWithValue("$updated", snapshot.UpdatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                association.ExecuteNonQuery();
            }

            foreach (var evaluation in snapshot.Evaluations)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO trade_setup_criterion_evaluations (id, journal_id, trade_id, trade_setup_id, setup_version_id, criterion_id, evaluation_state, note, evaluation_source, evaluated_utc) VALUES ($id, $journal, $trade, $tradeSetup, $version, $criterion, $state, $note, $source, $evaluated)";
                command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
                command.Parameters.AddWithValue("$trade", tradeId);
                command.Parameters.AddWithValue("$tradeSetup", associationId);
                command.Parameters.AddWithValue("$version", evaluation.SetupVersionId.ToString("D"));
                command.Parameters.AddWithValue("$criterion", evaluation.CriterionId.ToString("D"));
                command.Parameters.AddWithValue("$state", ToStorage(evaluation.EvaluationState));
                command.Parameters.AddWithValue("$note", evaluation.Note);
                command.Parameters.AddWithValue("$source", ToStorage(evaluation.EvaluationSource));
                command.Parameters.AddWithValue("$evaluated", evaluation.EvaluatedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
            }
        }
    }

    private static void InsertTradingSetupCriteria(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid versionId, IReadOnlyList<TradingSetupCriterionDraft> criteria, DateTimeOffset createdUtc)
    {
        for (var index = 0; index < criteria.Count; index++)
        {
            var criterion = criteria[index];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO trading_setup_criteria (id, journal_id, setup_version_id, name, description, criterion_type, display_order, active, evaluation_mode, rule_metadata_json, stage, created_utc) VALUES ($id, $journal, $version, $name, $description, $type, $order, $active, $mode, $rules, $stage, $created)";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
            command.Parameters.AddWithValue("$version", versionId.ToString("D"));
            command.Parameters.AddWithValue("$name", criterion.Name);
            command.Parameters.AddWithValue("$description", criterion.Description);
            command.Parameters.AddWithValue("$type", ToStorage(criterion.CriterionType));
            command.Parameters.AddWithValue("$order", criterion.DisplayOrder >= 0 ? criterion.DisplayOrder : index);
            command.Parameters.AddWithValue("$active", criterion.Active ? 1 : 0);
            command.Parameters.AddWithValue("$mode", ToStorage(criterion.EvaluationMode));
            command.Parameters.AddWithValue("$rules", criterion.RuleMetadataJson);
            command.Parameters.AddWithValue("$stage", ToStorage(criterion.Stage));
            command.Parameters.AddWithValue("$created", createdUtc.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
    }

    private sealed class DerivedTradeSetupSnapshot
    {
        public string ReviewKey { get; init; } = string.Empty;
        public Guid SetupVersionId { get; init; }
        public TradeSetupRole Role { get; init; }
        public string Note { get; init; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; init; }
        public DateTimeOffset UpdatedUtc { get; init; }
        public List<DerivedTradeEvaluationSnapshot> Evaluations { get; } = new();
    }

    private sealed class DerivedTradeEvaluationSnapshot
    {
        public Guid SetupVersionId { get; init; }
        public Guid CriterionId { get; init; }
        public CriterionEvaluationState EvaluationState { get; init; }
        public string Note { get; init; } = string.Empty;
        public SetupEvaluationSource EvaluationSource { get; init; }
        public DateTimeOffset EvaluatedUtc { get; init; }
    }

    private static void DemotePrimaryTradeSetups(SqliteConnection connection, SqliteTransaction transaction, Guid journalId, Guid tradeId, DateTimeOffset updatedUtc, Guid? exceptId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE trade_setups SET role = 'secondary', updated_utc = $updated WHERE journal_id = $journal AND trade_id = $trade AND role = 'primary' AND ($except IS NULL OR id <> $except)";
        command.Parameters.AddWithValue("$updated", updatedUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$journal", journalId.ToString("D"));
        command.Parameters.AddWithValue("$trade", tradeId.ToString("D"));
        command.Parameters.AddWithValue("$except", exceptId.HasValue ? exceptId.Value.ToString("D") : DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static TradingSetupSummary ReadTradingSetupSummary(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        Name = reader.GetString(2),
        ShortDescription = reader.GetString(3),
        Category = reader.GetString(4),
        Active = Convert.ToInt32(reader.GetValue(5), CultureInfo.InvariantCulture) != 0,
        CurrentVersionId = reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)),
        CurrentVersion = reader.GetInt32(7),
        VersionCount = reader.GetInt32(8),
        CriteriaCount = reader.GetInt32(9),
        TradeCount = reader.GetInt32(10),
        UpdatedUtc = ParseDate(reader.GetString(11))
    };

    private static TradingSetup ReadTradingSetup(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        Name = reader.GetString(2),
        ShortDescription = reader.GetString(3),
        DetailedDescription = reader.GetString(4),
        Category = reader.GetString(5),
        Active = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture) != 0,
        CreatedUtc = ParseDate(reader.GetString(7)),
        UpdatedUtc = ParseDate(reader.GetString(8))
    };

    private static TradingSetupVersion ReadTradingSetupVersion(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        SetupId = Guid.Parse(reader.GetString(2)),
        Version = reader.GetInt32(3),
        Notes = reader.GetString(4),
        CreatedUtc = ParseDate(reader.GetString(5))
    };

    private static TradingSetupCriterion ReadTradingSetupCriterion(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        SetupVersionId = Guid.Parse(reader.GetString(2)),
        Name = reader.GetString(3),
        Description = reader.GetString(4),
        CriterionType = ParseCriterionType(reader.GetString(5)),
        DisplayOrder = reader.GetInt32(6),
        Active = Convert.ToInt32(reader.GetValue(7), CultureInfo.InvariantCulture) != 0,
        EvaluationMode = ParseEvaluationMode(reader.GetString(8)),
        RuleMetadataJson = reader.GetString(9),
        Stage = ParseCriterionStage(reader.GetString(10)),
        CreatedUtc = ParseDate(reader.GetString(11))
    };

    private static TradeSetup ReadTradeSetup(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        TradeId = Guid.Parse(reader.GetString(2)),
        SetupVersionId = Guid.Parse(reader.GetString(3)),
        Role = ParseTradeSetupRole(reader.GetString(4)),
        Note = reader.GetString(5),
        CreatedUtc = ParseDate(reader.GetString(6)),
        UpdatedUtc = ParseDate(reader.GetString(7))
    };

    private static TradeSetupCriterionEvaluation ReadTradeSetupCriterionEvaluation(SqliteDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        JournalId = Guid.Parse(reader.GetString(1)),
        TradeId = Guid.Parse(reader.GetString(2)),
        TradeSetupId = Guid.Parse(reader.GetString(3)),
        SetupVersionId = Guid.Parse(reader.GetString(4)),
        CriterionId = Guid.Parse(reader.GetString(5)),
        EvaluationState = ParseEvaluationState(reader.GetString(6)),
        Note = reader.GetString(7),
        EvaluationSource = ParseEvaluationSource(reader.GetString(8)),
        EvaluatedUtc = ParseDate(reader.GetString(9))
    };

    private static string ToStorage(SetupCriterionType value) => value switch
    {
        SetupCriterionType.Required => "required",
        SetupCriterionType.Supporting => "supporting",
        SetupCriterionType.Disqualifier => "disqualifier",
        SetupCriterionType.Context => "context",
        _ => "required"
    };

    private static string ToStorage(SetupEvaluationMode value) => value switch
    {
        SetupEvaluationMode.Automatic => "automatic",
        SetupEvaluationMode.Suggested => "suggested",
        _ => "manual"
    };

    private static string ToStorage(SetupCriterionStage value) => value switch
    {
        SetupCriterionStage.Context => "context",
        SetupCriterionStage.Trigger => "trigger",
        SetupCriterionStage.Management => "management",
        SetupCriterionStage.Exit => "exit",
        _ => "setup"
    };

    private static string ToStorage(TradeSetupRole value) => value == TradeSetupRole.Primary ? "primary" : "secondary";

    private static string ToStorage(CriterionEvaluationState value) => value switch
    {
        CriterionEvaluationState.Met => "met",
        CriterionEvaluationState.NotMet => "not_met",
        CriterionEvaluationState.NotApplicable => "not_applicable",
        _ => "unknown"
    };

    private static string ToStorage(SetupEvaluationSource value) => value switch
    {
        SetupEvaluationSource.Automatic => "automatic",
        SetupEvaluationSource.Suggested => "suggested",
        SetupEvaluationSource.Override => "override",
        _ => "manual"
    };

    private static SetupCriterionType ParseCriterionType(string value) => value switch
    {
        "supporting" => SetupCriterionType.Supporting,
        "disqualifier" => SetupCriterionType.Disqualifier,
        "context" => SetupCriterionType.Context,
        _ => SetupCriterionType.Required
    };

    private static SetupEvaluationMode ParseEvaluationMode(string value) => value switch
    {
        "automatic" => SetupEvaluationMode.Automatic,
        "suggested" => SetupEvaluationMode.Suggested,
        _ => SetupEvaluationMode.Manual
    };

    private static SetupCriterionStage ParseCriterionStage(string value) => value switch
    {
        "context" => SetupCriterionStage.Context,
        "trigger" => SetupCriterionStage.Trigger,
        "management" => SetupCriterionStage.Management,
        "exit" => SetupCriterionStage.Exit,
        _ => SetupCriterionStage.Setup
    };

    private static TradeSetupRole ParseTradeSetupRole(string value) => value == "primary" ? TradeSetupRole.Primary : TradeSetupRole.Secondary;

    private static CriterionEvaluationState ParseEvaluationState(string value) => value switch
    {
        "met" => CriterionEvaluationState.Met,
        "not_met" => CriterionEvaluationState.NotMet,
        "not_applicable" => CriterionEvaluationState.NotApplicable,
        _ => CriterionEvaluationState.Unknown
    };

    private static SetupEvaluationSource ParseEvaluationSource(string value) => value switch
    {
        "automatic" => SetupEvaluationSource.Automatic,
        "suggested" => SetupEvaluationSource.Suggested,
        "override" => SetupEvaluationSource.Override,
        _ => SetupEvaluationSource.Manual
    };
}
