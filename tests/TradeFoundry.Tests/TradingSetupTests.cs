using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class TradingSetupTests
{
    [Fact]
    public void SetupDefinitionsVersionCriteriaAndHistoricalEvaluationsRemainSeparate()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var service = new TradingSetupService(database);
            var setup = service.CreateSetup(journal.Id, new TradingSetupDraft
            {
                Name = "Second Entry Long",
                ShortDescription = "Second entry in a bull trend",
                Category = "Pullback"
            }, new[]
            {
                new TradingSetupCriterionDraft { Name = "Established bull trend", CriterionType = SetupCriterionType.Required, Stage = SetupCriterionStage.Context },
                new TradingSetupCriterionDraft { Name = "EMA support", CriterionType = SetupCriterionType.Supporting, Stage = SetupCriterionStage.Context },
                new TradingSetupCriterionDraft { Name = "Major resistance overhead", CriterionType = SetupCriterionType.Disqualifier, Stage = SetupCriterionStage.Context },
                new TradingSetupCriterionDraft { Name = "Opening hour", CriterionType = SetupCriterionType.Context, Stage = SetupCriterionStage.Context }
            });

            var versionOne = setup.CurrentVersion;
            Assert.NotNull(versionOne);
            Assert.Equal(1, versionOne.Version);
            Assert.Equal(4, versionOne.Criteria.Count);

            var trade = ImportRoundTrip(database, journal.Id);
            var association = service.AttachToTrade(journal.Id, trade.Id, versionOne.Id, TradeSetupRole.Primary);
            Assert.Equal(TradeSetupRole.Primary, association.Association.Role);

            var initial = Assert.Single(service.GetTradeSetupWorkspace(journal.Id, trade.Id).Evaluations);
            Assert.Equal(1, initial.Adherence.RequiredUnknown);
            Assert.Equal(1, initial.Adherence.ContextUnknown);
            Assert.False(initial.Adherence.IsFullySatisfied);

            var criteria = versionOne.Criteria.ToDictionary(criterion => criterion.Name);
            service.SaveEvaluations(journal.Id, association.Association.Id, new[]
            {
                new TradeSetupCriterionEvaluationInput { CriterionId = criteria["Established bull trend"].Id, EvaluationState = CriterionEvaluationState.Met },
                new TradeSetupCriterionEvaluationInput { CriterionId = criteria["EMA support"].Id, EvaluationState = CriterionEvaluationState.Met },
                new TradeSetupCriterionEvaluationInput { CriterionId = criteria["Major resistance overhead"].Id, EvaluationState = CriterionEvaluationState.Met, Note = "Prior high is close" },
                new TradeSetupCriterionEvaluationInput { CriterionId = criteria["Opening hour"].Id, EvaluationState = CriterionEvaluationState.NotApplicable }
            });

            var evaluated = Assert.Single(service.GetTradeSetupWorkspace(journal.Id, trade.Id).Evaluations);
            Assert.True(evaluated.Adherence.IsFullySatisfied);
            Assert.Equal(1, evaluated.Adherence.RequiredMet);
            Assert.Equal(1, evaluated.Adherence.SupportingMet);
            Assert.Equal(1, evaluated.Adherence.DisqualifiersPresent);
            Assert.Equal(1, evaluated.Adherence.ContextNotApplicable);
            Assert.Equal(4, evaluated.Adherence.TotalEvaluatedCriteria);
            Assert.Equal(100m, evaluated.Adherence.RequiredAdherencePercent);

            database.RebuildFlatTrades(journal.Id, "flat_to_flat");
            trade = Assert.Single(database.GetAllTrades(journal.Id));
            var afterRebuild = Assert.Single(service.GetTradeSetupWorkspace(journal.Id, trade.Id).Evaluations);
            Assert.Equal(versionOne.Id, afterRebuild.Version.Id);
            Assert.Equal(CriterionEvaluationState.Met, afterRebuild.Criteria.Single(item => item.Criterion.Name == "Major resistance overhead").EvaluationState);
            Assert.Equal("Prior high is close", afterRebuild.Criteria.Single(item => item.Criterion.Name == "Major resistance overhead").Note);

            var versionTwo = service.CreateVersion(journal.Id, setup.Setup.Id, "Require the pullback to be near the EMA.", versionOne.Criteria.Select(TradingSetupCriterionDraftFrom).Append(new TradingSetupCriterionDraft
            {
                Name = "Pullback near EMA",
                CriterionType = SetupCriterionType.Required,
                Stage = SetupCriterionStage.Setup
            }).ToArray());
            Assert.NotNull(versionTwo.CurrentVersion);
            Assert.Equal(2, versionTwo.CurrentVersion!.Version);
            Assert.Equal(5, versionTwo.CurrentVersion.Criteria.Count);

            var afterVersion = Assert.Single(service.GetTradeSetupWorkspace(journal.Id, trade.Id).Evaluations);
            Assert.Equal(versionOne.Id, afterVersion.Version.Id);
            Assert.Equal(4, afterVersion.Criteria.Count);
            Assert.Equal(CriterionEvaluationState.Met, afterVersion.Criteria.Single(item => item.Criterion.Name == "Major resistance overhead").EvaluationState);

            var secondary = service.AttachToTrade(journal.Id, trade.Id, versionTwo.CurrentVersion.Id, TradeSetupRole.Secondary);
            var workspace = service.GetTradeSetupWorkspace(journal.Id, trade.Id);
            Assert.Equal(2, workspace.Evaluations.Count);
            Assert.Contains(workspace.Evaluations, item => item.Association.Role == TradeSetupRole.Primary && item.Version.Id == versionOne.Id);
            Assert.Contains(workspace.Evaluations, item => item.Association.Role == TradeSetupRole.Secondary && item.Version.Id == versionTwo.CurrentVersion.Id);
            Assert.Equal(5, secondary.Criteria.Count);

            var performance = Assert.Single(service.GetPerformance(journal.Id), item => item.SetupVersionId == versionOne.Id);
            Assert.Equal(1, performance.Trades);
            Assert.Equal(100m, performance.WinRatePercent);
            Assert.Equal(100m, performance.RequiredAdherencePercent);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void UnusedSetupEditsCurrentVersionWithoutCreatingAnotherVersionAndReportsTradeUsage()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var service = new TradingSetupService(database);
            var setup = service.CreateSetup(journal.Id, new TradingSetupDraft { Name = "Unused setup" }, new[]
            {
                new TradingSetupCriterionDraft { Name = "Initial condition" }
            });

            var summary = Assert.Single(service.GetSetups(journal.Id));
            Assert.Equal(0, summary.TradeCount);

            var updated = service.UpdateCurrentVersion(journal.Id, setup.Setup.Id, "Refined before first use.", new[]
            {
                new TradingSetupCriterionDraft { Name = "Refined condition", CriterionType = SetupCriterionType.Supporting }
            });
            Assert.NotNull(updated.CurrentVersion);
            Assert.Equal(1, updated.CurrentVersion!.Version);
            Assert.Equal("Refined condition", Assert.Single(updated.CurrentVersion.Criteria).Name);
            Assert.Single(updated.Versions);

            var trade = ImportRoundTrip(database, journal.Id);
            service.AttachToTrade(journal.Id, trade.Id, updated.CurrentVersion.Id, TradeSetupRole.Primary);
            Assert.Equal(1, Assert.Single(service.GetSetups(journal.Id)).TradeCount);
            Assert.Throws<InvalidOperationException>(() => service.UpdateCurrentVersion(journal.Id, setup.Setup.Id, null, new[]
            {
                new TradingSetupCriterionDraft { Name = "Should require a new version" }
            }));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void PrimaryRoleIsUniqueAndUnknownNotApplicableRemainDistinct()
    {
        var directory = NewDirectory();
        try
        {
            var database = CreateDatabase(directory);
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var service = new TradingSetupService(database);
            var first = service.CreateSetup(journal.Id, new TradingSetupDraft { Name = "First" }, Array.Empty<TradingSetupCriterionDraft>());
            var second = service.CreateSetup(journal.Id, new TradingSetupDraft { Name = "Second" }, Array.Empty<TradingSetupCriterionDraft>());
            var trade = ImportRoundTrip(database, journal.Id);

            service.AttachToTrade(journal.Id, trade.Id, first.CurrentVersion!.Id, TradeSetupRole.Primary);
            service.AttachToTrade(journal.Id, trade.Id, second.CurrentVersion!.Id, TradeSetupRole.Primary);

            var associations = database.GetTradeSetupsForTrade(journal.Id, trade.Id);
            Assert.Equal(2, associations.Count);
            Assert.Single(associations, item => item.Role == TradeSetupRole.Primary);
            Assert.Single(associations, item => item.Role == TradeSetupRole.Secondary);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static TradeFoundryDb CreateDatabase(string directory) => new(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));

    private static Trade ImportRoundTrip(TradeFoundryDb database, Guid journalId)
    {
        var entry = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var parsed = new ParsedImport { SourceType = TradeFoundryConstants.SierraFills, SourceApplication = TradeFoundryConstants.SierraChart };
        parsed.Records.Add(FillRecord("entry-key", "Buy", 5000m, entry, 1));
        parsed.Records.Add(FillRecord("exit-key", "Sell", 5001m, entry.AddHours(1), 2));
        var result = database.CommitImport(journalId, "test.txt", parsed, "flat_to_flat", "source");
        return Assert.Single(database.GetAllTrades(journalId));
    }

    private static ParsedRecord FillRecord(string sourceKey, string side, decimal price, DateTimeOffset timestamp, int row) => new()
    {
        SourceType = TradeFoundryConstants.SierraFills,
        SourceKey = sourceKey,
        RowNumber = row,
        PayloadJson = "{}",
        Fill = new FillDraft
        {
            SourceType = TradeFoundryConstants.SierraFills,
            SourceKey = sourceKey,
            EventUtc = timestamp,
            SourceTimeText = timestamp.ToString("O"),
            Symbol = "MESZ26",
            Instrument = "MES",
            Account = "SIM",
            Side = side,
            Quantity = 1,
            Price = price,
            Fees = 0m,
            RowNumber = row,
            PointValue = 5m,
            TickSize = .25m
        }
    };

    private static TradingSetupCriterionDraft TradingSetupCriterionDraftFrom(TradingSetupCriterion criterion) => new()
    {
        Name = criterion.Name,
        Description = criterion.Description,
        CriterionType = criterion.CriterionType,
        Active = criterion.Active,
        EvaluationMode = criterion.EvaluationMode,
        RuleMetadataJson = criterion.RuleMetadataJson,
        Stage = criterion.Stage
    };

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TradeFoundry.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string directory)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
