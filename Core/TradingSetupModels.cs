namespace TradeFoundry.Core;

public enum SetupCriterionType
{
    Required,
    Supporting,
    Disqualifier,
    Context
}

public enum SetupEvaluationMode
{
    Manual,
    Automatic,
    Suggested
}

public enum SetupCriterionStage
{
    Context,
    Setup,
    Trigger,
    Management,
    Exit
}

public enum CriterionEvaluationState
{
    Met,
    NotMet,
    Unknown,
    NotApplicable
}

public enum TradeSetupRole
{
    Primary,
    Secondary
}

public enum SetupEvaluationSource
{
    Manual,
    Automatic,
    Suggested,
    Override
}

public sealed class TradingSetup
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ShortDescription { get; init; } = string.Empty;
    public string DetailedDescription { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool Active { get; init; } = true;
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed class TradingSetupVersion
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid SetupId { get; init; }
    public int Version { get; init; }
    public string Notes { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public IReadOnlyList<TradingSetupCriterion> Criteria { get; init; } = Array.Empty<TradingSetupCriterion>();
}

public sealed class TradingSetupCriterion
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid SetupVersionId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public SetupCriterionType CriterionType { get; init; }
    public int DisplayOrder { get; init; }
    public bool Active { get; init; } = true;
    public SetupEvaluationMode EvaluationMode { get; init; } = SetupEvaluationMode.Manual;
    public string RuleMetadataJson { get; init; } = string.Empty;
    public SetupCriterionStage Stage { get; init; } = SetupCriterionStage.Setup;
    public DateTimeOffset CreatedUtc { get; init; }
}

public sealed class TradingSetupSummary
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string ShortDescription { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public bool Active { get; init; }
    public Guid? CurrentVersionId { get; init; }
    public int CurrentVersion { get; init; }
    public int VersionCount { get; init; }
    public int CriteriaCount { get; init; }
    public int TradeCount { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed class TradingSetupDetail
{
    public TradingSetup Setup { get; init; } = new();
    public IReadOnlyList<TradingSetupVersion> Versions { get; init; } = Array.Empty<TradingSetupVersion>();
    public TradingSetupVersion? CurrentVersion => Versions.OrderByDescending(version => version.Version).FirstOrDefault();
}

public sealed class TradingSetupDraft
{
    public string Name { get; set; } = string.Empty;
    public string ShortDescription { get; set; } = string.Empty;
    public string DetailedDescription { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

public sealed class TradingSetupCriterionDraft
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SetupCriterionType CriterionType { get; set; } = SetupCriterionType.Required;
    public int DisplayOrder { get; set; }
    public bool Active { get; set; } = true;
    public SetupEvaluationMode EvaluationMode { get; set; } = SetupEvaluationMode.Manual;
    public string RuleMetadataJson { get; set; } = string.Empty;
    public SetupCriterionStage Stage { get; set; } = SetupCriterionStage.Setup;
}

public sealed class TradeSetup
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid TradeId { get; init; }
    public Guid SetupVersionId { get; init; }
    public TradeSetupRole Role { get; init; }
    public string Note { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed class TradeSetupCriterionEvaluation
{
    public Guid Id { get; init; }
    public Guid JournalId { get; init; }
    public Guid TradeId { get; init; }
    public Guid TradeSetupId { get; init; }
    public Guid SetupVersionId { get; init; }
    public Guid CriterionId { get; init; }
    public CriterionEvaluationState EvaluationState { get; init; } = CriterionEvaluationState.Unknown;
    public string Note { get; init; } = string.Empty;
    public SetupEvaluationSource EvaluationSource { get; init; } = SetupEvaluationSource.Manual;
    public DateTimeOffset EvaluatedUtc { get; init; }
}

public sealed class TradeSetupCriterionEvaluationInput
{
    public Guid CriterionId { get; set; }
    public CriterionEvaluationState EvaluationState { get; set; } = CriterionEvaluationState.Unknown;
    public string Note { get; set; } = string.Empty;
}

public sealed class TradeSetupCriterionEvaluationView
{
    public TradingSetupCriterion Criterion { get; init; } = new();
    public CriterionEvaluationState EvaluationState { get; init; } = CriterionEvaluationState.Unknown;
    public string Note { get; init; } = string.Empty;
    public SetupEvaluationSource EvaluationSource { get; init; } = SetupEvaluationSource.Manual;
    public DateTimeOffset? EvaluatedUtc { get; init; }
}

public sealed class SetupAdherenceSummary
{
    public Guid TradeSetupId { get; init; }
    public Guid SetupVersionId { get; init; }
    public string SetupName { get; init; } = string.Empty;
    public int Version { get; init; }
    public int RequiredTotal { get; init; }
    public int RequiredMet { get; init; }
    public int RequiredNotMet { get; init; }
    public int RequiredUnknown { get; init; }
    public int RequiredNotApplicable { get; init; }
    public int SupportingTotal { get; init; }
    public int SupportingMet { get; init; }
    public int SupportingNotMet { get; init; }
    public int SupportingUnknown { get; init; }
    public int SupportingNotApplicable { get; init; }
    public int DisqualifierTotal { get; init; }
    public int DisqualifiersPresent { get; init; }
    public int DisqualifiersAbsent { get; init; }
    public int DisqualifiersUnknown { get; init; }
    public int DisqualifiersNotApplicable { get; init; }
    public int ContextTotal { get; init; }
    public int ContextMet { get; init; }
    public int ContextNotMet { get; init; }
    public int ContextUnknown { get; init; }
    public int ContextNotApplicable { get; init; }
    public int TotalCriteria { get; init; }
    public int TotalEvaluatedCriteria { get; init; }

    public decimal? RequiredAdherencePercent
    {
        get
        {
            var evaluable = RequiredMet + RequiredNotMet + RequiredUnknown;
            return evaluable == 0 ? null : RequiredMet * 100m / evaluable;
        }
    }

    public bool IsFullySatisfied => RequiredNotMet == 0 && RequiredUnknown == 0;
    public bool HasDisqualifiers => DisqualifiersPresent > 0;
}

public sealed class TradeSetupEvaluation
{
    public TradeSetup Association { get; init; } = new();
    public TradingSetup Setup { get; init; } = new();
    public TradingSetupVersion Version { get; init; } = new();
    public IReadOnlyList<TradeSetupCriterionEvaluationView> Criteria { get; init; } = Array.Empty<TradeSetupCriterionEvaluationView>();
    public SetupAdherenceSummary Adherence { get; init; } = new();
}

public sealed class TradeSetupWorkspace
{
    public IReadOnlyList<TradeSetupEvaluation> Evaluations { get; init; } = Array.Empty<TradeSetupEvaluation>();
}

public sealed class SetupPerformanceSummary
{
    public Guid SetupId { get; init; }
    public Guid SetupVersionId { get; init; }
    public string SetupName { get; init; } = string.Empty;
    public int Version { get; init; }
    public int Trades { get; init; }
    public int WinningTrades { get; init; }
    public decimal? WinRatePercent { get; init; }
    public decimal? AveragePnl { get; init; }
    public decimal? AverageR { get; init; }
    public decimal? AverageMaePoints { get; init; }
    public decimal? AverageMfePoints { get; init; }
    public decimal? RequiredAdherencePercent { get; init; }
}
