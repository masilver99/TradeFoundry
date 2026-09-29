using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public sealed class TradingSetupService
{
    private readonly TradeFoundryDb _database;

    public TradingSetupService(TradeFoundryDb database)
    {
        _database = database;
    }

    public IReadOnlyList<TradingSetupSummary> GetSetups(Guid journalId, bool includeInactive = true)
        => _database.GetTradingSetupSummaries(journalId, includeInactive);

    public TradingSetupDetail? GetSetup(Guid journalId, Guid setupId)
        => _database.GetTradingSetupDetail(journalId, setupId);

    public TradingSetupDetail CreateSetup(Guid journalId, TradingSetupDraft draft, IReadOnlyList<TradingSetupCriterionDraft> criteria)
    {
        var normalizedDraft = NormalizeDraft(draft);
        var normalizedCriteria = NormalizeCriteria(criteria);
        EnsureUniqueSetupName(journalId, normalizedDraft.Name);
        return _database.CreateTradingSetup(journalId, normalizedDraft, normalizedCriteria);
    }

    public TradingSetup? UpdateMetadata(Guid journalId, Guid setupId, TradingSetupDraft draft)
    {
        var normalizedDraft = NormalizeDraft(draft);
        EnsureUniqueSetupName(journalId, normalizedDraft.Name, setupId);
        return _database.UpdateTradingSetupMetadata(journalId, setupId, normalizedDraft);
    }

    public bool SetActive(Guid journalId, Guid setupId, bool active)
        => _database.SetTradingSetupActive(journalId, setupId, active);

    public TradingSetupDetail CreateVersion(Guid journalId, Guid setupId, string? notes, IReadOnlyList<TradingSetupCriterionDraft> criteria)
    {
        _ = _database.GetTradingSetup(journalId, setupId) ?? throw new InvalidOperationException("The setup was not found.");
        return _database.CreateTradingSetupVersion(journalId, setupId, Trim(notes, 1000), NormalizeCriteria(criteria));
    }

    public TradingSetupDetail UpdateCurrentVersion(Guid journalId, Guid setupId, string? notes, IReadOnlyList<TradingSetupCriterionDraft> criteria)
    {
        _ = _database.GetTradingSetup(journalId, setupId) ?? throw new InvalidOperationException("The setup was not found.");
        return _database.UpdateCurrentTradingSetupVersion(journalId, setupId, Trim(notes, 1000), NormalizeCriteria(criteria));
    }

    public bool MoveCriterion(Guid journalId, Guid criterionId, bool moveUp)
        => _database.MoveTradingSetupCriterion(journalId, criterionId, moveUp);

    public TradeSetupWorkspace GetTradeSetupWorkspace(Guid journalId, Guid tradeId)
    {
        var evaluations = _database.GetTradeSetupsForTrade(journalId, tradeId)
            .Select(association => BuildEvaluation(journalId, association))
            .Where(evaluation => evaluation is not null)
            .Cast<TradeSetupEvaluation>()
            .ToArray();
        return new TradeSetupWorkspace { Evaluations = evaluations };
    }

    public TradeSetupEvaluation AttachToTrade(Guid journalId, Guid tradeId, Guid setupVersionId, TradeSetupRole role)
    {
        var association = _database.AttachTradeSetup(journalId, tradeId, setupVersionId, role);
        return BuildEvaluation(journalId, association) ?? throw new InvalidOperationException("The setup definition could not be read after attaching it.");
    }

    public bool SetRole(Guid journalId, Guid tradeSetupId, TradeSetupRole role)
        => _database.UpdateTradeSetupRole(journalId, tradeSetupId, role);

    public bool RemoveFromTrade(Guid journalId, Guid tradeSetupId)
        => _database.RemoveTradeSetup(journalId, tradeSetupId);

    public TradeSetupEvaluation SaveEvaluations(Guid journalId, Guid tradeSetupId, IReadOnlyList<TradeSetupCriterionEvaluationInput> inputs)
    {
        var association = _database.GetTradeSetup(journalId, tradeSetupId)
            ?? throw new InvalidOperationException("The trade setup association was not found.");
        var detail = FindSetupForVersion(journalId, association.SetupVersionId);
        var version = detail.Version;
        var current = _database.GetTradeSetupCriterionEvaluations(journalId, tradeSetupId)
            .ToDictionary(evaluation => evaluation.CriterionId);
        var submitted = inputs
            .GroupBy(input => input.CriterionId)
            .ToDictionary(group => group.Key, group => group.Last());
        var now = DateTimeOffset.UtcNow;
        var evaluations = version.Criteria
            .Where(criterion => criterion.Active)
            .Select(criterion =>
            {
                current.TryGetValue(criterion.Id, out var existing);
                var input = submitted.TryGetValue(criterion.Id, out var posted)
                    ? posted
                    : new TradeSetupCriterionEvaluationInput
                    {
                        CriterionId = criterion.Id,
                        EvaluationState = existing?.EvaluationState ?? CriterionEvaluationState.Unknown,
                        Note = existing?.Note ?? string.Empty
                    };
                var normalizedState = input.EvaluationState;
                var source = existing is null
                    ? SetupEvaluationSource.Manual
                    : existing.EvaluationState != normalizedState
                        && (existing.EvaluationSource is SetupEvaluationSource.Automatic or SetupEvaluationSource.Suggested)
                        ? SetupEvaluationSource.Override
                        : existing.EvaluationState == normalizedState
                            ? existing.EvaluationSource
                            : SetupEvaluationSource.Manual;
                return new TradeSetupCriterionEvaluation
                {
                    Id = existing?.Id ?? Guid.NewGuid(),
                    JournalId = journalId,
                    TradeId = association.TradeId,
                    TradeSetupId = association.Id,
                    SetupVersionId = association.SetupVersionId,
                    CriterionId = criterion.Id,
                    EvaluationState = normalizedState,
                    Note = Trim(input.Note, 500),
                    EvaluationSource = source,
                    EvaluatedUtc = now
                };
            })
            .ToArray();

        _database.SaveTradeSetupCriterionEvaluations(journalId, tradeSetupId, evaluations);
        return BuildEvaluation(journalId, association) ?? throw new InvalidOperationException("The trade setup evaluation could not be read after saving.");
    }

    public SetupAdherenceSummary CalculateAdherence(TradeSetup association, TradingSetup setup, TradingSetupVersion version, IReadOnlyList<TradeSetupCriterionEvaluation> evaluations)
    {
        var states = evaluations.ToDictionary(evaluation => evaluation.CriterionId, evaluation => evaluation.EvaluationState);
        var activeCriteria = version.Criteria.Where(criterion => criterion.Active).ToArray();
        var counts = activeCriteria
            .GroupBy(criterion => criterion.CriterionType)
            .ToDictionary(group => group.Key, group => CountStates(group, states));

        var required = CountsFor(counts, SetupCriterionType.Required);
        var supporting = CountsFor(counts, SetupCriterionType.Supporting);
        var disqualifier = CountsFor(counts, SetupCriterionType.Disqualifier);
        var context = CountsFor(counts, SetupCriterionType.Context);
        return new SetupAdherenceSummary
        {
            TradeSetupId = association.Id,
            SetupVersionId = version.Id,
            SetupName = setup.Name,
            Version = version.Version,
            RequiredTotal = required.Total,
            RequiredMet = required.Met,
            RequiredNotMet = required.NotMet,
            RequiredUnknown = required.Unknown,
            RequiredNotApplicable = required.NotApplicable,
            SupportingTotal = supporting.Total,
            SupportingMet = supporting.Met,
            SupportingNotMet = supporting.NotMet,
            SupportingUnknown = supporting.Unknown,
            SupportingNotApplicable = supporting.NotApplicable,
            DisqualifierTotal = disqualifier.Total,
            DisqualifiersPresent = disqualifier.Met,
            DisqualifiersAbsent = disqualifier.NotMet,
            DisqualifiersUnknown = disqualifier.Unknown,
            DisqualifiersNotApplicable = disqualifier.NotApplicable,
            ContextTotal = context.Total,
            ContextMet = context.Met,
            ContextNotMet = context.NotMet,
            ContextUnknown = context.Unknown,
            ContextNotApplicable = context.NotApplicable,
            TotalCriteria = activeCriteria.Length,
            TotalEvaluatedCriteria = activeCriteria.Count(criterion => states.TryGetValue(criterion.Id, out var state) && state != CriterionEvaluationState.Unknown)
        };
    }

    public IReadOnlyList<SetupPerformanceSummary> GetPerformance(Guid journalId)
    {
        var trades = _database.GetAllTrades(journalId)
            .Where(trade => trade.ExitUtc.HasValue && trade.Status.Equals("closed", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(trade => trade.Id);
        var associations = _database.GetTradeSetupsForJournal(journalId);
        if (associations.Count == 0) return Array.Empty<SetupPerformanceSummary>();

        var evaluationsByAssociation = _database.GetTradeSetupCriterionEvaluationsForJournal(journalId)
            .GroupBy(evaluation => evaluation.TradeSetupId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<TradeSetupCriterionEvaluation>)group.ToArray());
        var detailBySetup = associations
            .Select(association => association.SetupVersionId)
            .Distinct()
            .Select(versionId => FindSetupForVersion(journalId, versionId))
            .ToDictionary(detail => detail.Version.Id, detail => detail);

        return associations
            .Where(association => trades.ContainsKey(association.TradeId) && detailBySetup.ContainsKey(association.SetupVersionId))
            .GroupBy(association => association.SetupVersionId)
            .Select(group =>
            {
                var detail = detailBySetup[group.Key];
                var outcomes = group.Select(association => trades[association.TradeId]).ToArray();
                var adherence = group
                    .Select(association => CalculateAdherence(association, detail.Setup, detail.Version, evaluationsByAssociation.TryGetValue(association.Id, out var values) ? values : Array.Empty<TradeSetupCriterionEvaluation>()).RequiredAdherencePercent)
                    .Where(value => value.HasValue)
                    .Select(value => value!.Value)
                    .ToArray();
                var mae = outcomes.Where(trade => trade.MaePoints.HasValue).Select(trade => Math.Abs(trade.MaePoints!.Value)).ToArray();
                var mfe = outcomes.Where(trade => trade.MfePoints.HasValue).Select(trade => Math.Max(0m, trade.MfePoints!.Value)).ToArray();
                return new SetupPerformanceSummary
                {
                    SetupId = detail.Setup.Id,
                    SetupVersionId = detail.Version.Id,
                    SetupName = detail.Setup.Name,
                    Version = detail.Version.Version,
                    Trades = outcomes.Length,
                    WinningTrades = outcomes.Count(trade => trade.NetPnl > 0m),
                    WinRatePercent = outcomes.Length == 0 ? null : outcomes.Count(trade => trade.NetPnl > 0m) * 100m / outcomes.Length,
                    AveragePnl = outcomes.Length == 0 ? null : outcomes.Average(trade => trade.NetPnl),
                    AverageR = Average(outcomes.Select(trade => trade.RMultiple)),
                    AverageMaePoints = mae.Length == 0 ? null : mae.Average(),
                    AverageMfePoints = mfe.Length == 0 ? null : mfe.Average(),
                    RequiredAdherencePercent = adherence.Length == 0 ? null : adherence.Average()
                };
            })
            .OrderByDescending(summary => summary.Trades)
            .ThenBy(summary => summary.SetupName, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(summary => summary.Version)
            .ToArray();
    }

    private TradeSetupEvaluation? BuildEvaluation(Guid journalId, TradeSetup association)
    {
        var detail = FindSetupForVersion(journalId, association.SetupVersionId);
        var setup = detail.Setup;
        var version = detail.Version;
        var evaluations = _database.GetTradeSetupCriterionEvaluations(journalId, association.Id)
            .ToDictionary(evaluation => evaluation.CriterionId);
        var criteria = version.Criteria
            .Where(criterion => criterion.Active)
            .OrderBy(criterion => criterion.DisplayOrder)
            .ThenBy(criterion => criterion.Id)
            .Select(criterion =>
            {
                evaluations.TryGetValue(criterion.Id, out var evaluation);
                return new TradeSetupCriterionEvaluationView
                {
                    Criterion = criterion,
                    EvaluationState = evaluation?.EvaluationState ?? CriterionEvaluationState.Unknown,
                    Note = evaluation?.Note ?? string.Empty,
                    EvaluationSource = evaluation?.EvaluationSource ?? SetupEvaluationSource.Manual,
                    EvaluatedUtc = evaluation?.EvaluatedUtc
                };
            })
            .ToArray();
        return new TradeSetupEvaluation
        {
            Association = association,
            Setup = setup,
            Version = version,
            Criteria = criteria,
            Adherence = CalculateAdherence(association, setup, version, evaluations.Values.ToArray())
        };
    }

    private SetupVersionContext FindSetupForVersion(Guid journalId, Guid setupVersionId)
    {
        var summaries = _database.GetTradingSetupSummaries(journalId);
        foreach (var summary in summaries)
        {
            var detail = _database.GetTradingSetupDetail(journalId, summary.Id);
            var version = detail?.Versions.FirstOrDefault(candidate => candidate.Id == setupVersionId);
            if (detail is not null && version is not null) return new SetupVersionContext(detail.Setup, version);
        }
        throw new InvalidOperationException("The setup version was not found in this journal.");
    }

    private static TradingSetupDraft NormalizeDraft(TradingSetupDraft draft)
    {
        var name = Trim(draft.Name, 120);
        if (name.Length == 0) throw new ArgumentException("A setup name is required.", nameof(draft));
        return new TradingSetupDraft
        {
            Name = name,
            ShortDescription = Trim(draft.ShortDescription, 500),
            DetailedDescription = Trim(draft.DetailedDescription, 8000),
            Category = Trim(draft.Category, 100)
        };
    }

    private void EnsureUniqueSetupName(Guid journalId, string name, Guid? exceptSetupId = null)
    {
        if (_database.GetTradingSetupSummaries(journalId, true).Any(setup => setup.Id != exceptSetupId && string.Equals(setup.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A setup with this name already exists in the journal.", nameof(name));
    }

    private static IReadOnlyList<TradingSetupCriterionDraft> NormalizeCriteria(IReadOnlyList<TradingSetupCriterionDraft> criteria)
    {
        var normalized = criteria
            .Where(criterion => !string.IsNullOrWhiteSpace(criterion.Name))
            .Select((criterion, index) => new TradingSetupCriterionDraft
            {
                Name = Trim(criterion.Name, 240),
                Description = Trim(criterion.Description, 1200),
                CriterionType = criterion.CriterionType,
                DisplayOrder = index,
                Active = criterion.Active,
                EvaluationMode = criterion.EvaluationMode,
                RuleMetadataJson = NormalizeRuleMetadata(criterion.RuleMetadataJson),
                Stage = criterion.Stage
            })
            .ToArray();
        var duplicates = normalized
            .GroupBy(criterion => criterion.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null) throw new ArgumentException($"Criterion names must be unique within a version: {duplicates.Key}.", nameof(criteria));
        return normalized;
    }

    private static string NormalizeRuleMetadata(string? value)
    {
        var text = Trim(value, 4000);
        if (text.Length == 0) return string.Empty;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.GetRawText();
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Rule metadata must be valid JSON when supplied.", nameof(value), exception);
        }
    }

    private static (int Total, int Met, int NotMet, int Unknown, int NotApplicable) CountStates(IEnumerable<TradingSetupCriterion> criteria, IReadOnlyDictionary<Guid, CriterionEvaluationState> states)
    {
        var values = criteria.Select(criterion => states.TryGetValue(criterion.Id, out var state) ? state : CriterionEvaluationState.Unknown).ToArray();
        return (values.Length,
            values.Count(state => state == CriterionEvaluationState.Met),
            values.Count(state => state == CriterionEvaluationState.NotMet),
            values.Count(state => state == CriterionEvaluationState.Unknown),
            values.Count(state => state == CriterionEvaluationState.NotApplicable));
    }

    private static (int Total, int Met, int NotMet, int Unknown, int NotApplicable) CountsFor(IReadOnlyDictionary<SetupCriterionType, (int Total, int Met, int NotMet, int Unknown, int NotApplicable)> counts, SetupCriterionType type)
        => counts.TryGetValue(type, out var value) ? value : (0, 0, 0, 0, 0);

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var populated = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return populated.Length == 0 ? null : populated.Average();
    }

    private static string Trim(string? value, int maxLength)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed record SetupVersionContext(TradingSetup Setup, TradingSetupVersion Version);
}
