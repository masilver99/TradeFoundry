using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class BrokerFeeProfileTests
{
    [Fact]
    public void BrokerFeeProfilesAreInstrumentScopedAndRevisioned()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tradefoundry-broker-fee-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var database = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = directory, DatabaseFileName = "journal.db" }));
            database.CreateOwner("Owner", "test-hash");
            var journal = database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
            var draft = new BrokerFeeProfileDraft
            {
                Name = "AMP Futures",
                Instrument = "MES",
                Notes = "Plan A",
                CommissionPerContractSide = 0.42m,
                ExchangePerContractSide = 0.18m,
                NfaPerContractSide = 0.02m,
                ClearingPerContractSide = 0.15m,
                PlatformMonthly = 10m
            };

            var created = database.CreateBrokerFeeProfile(journal.Id, draft);
            Assert.Equal("MES", created.Instrument);
            Assert.Equal(1, created.Revision);
            Assert.Single(database.GetBrokerFeeProfiles(journal.Id, "MES"));
            Assert.Empty(database.GetBrokerFeeProfiles(journal.Id, "NQ"));
            Assert.Throws<InvalidOperationException>(() => database.CreateBrokerFeeProfile(journal.Id, draft));

            var updated = database.UpdateBrokerFeeProfile(journal.Id, created.Id, 1, new BrokerFeeProfileDraft
            {
                Name = "AMP Futures",
                Instrument = "MES",
                Notes = "Plan B",
                CommissionPerContractSide = 0.39m,
                ExchangePerContractSide = 0.18m,
                NfaPerContractSide = 0.02m,
                ClearingPerContractSide = 0.15m,
                PlatformMonthly = 12m
            });
            Assert.True(updated.Saved);
            Assert.Equal(2, updated.Profile!.Revision);
            Assert.Equal("Plan B", updated.Profile.Notes);

            var conflict = database.UpdateBrokerFeeProfile(journal.Id, created.Id, 1, draft);
            Assert.True(conflict.Conflict);
            Assert.Equal(2, conflict.Profile!.Revision);

            using var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM broker_fee_profile_history WHERE journal_id = $journal AND profile_id = $profile";
            command.Parameters.AddWithValue("$journal", journal.Id.ToString("D"));
            command.Parameters.AddWithValue("$profile", created.Id.ToString("D"));
            Assert.Equal(2L, (long)(command.ExecuteScalar() ?? 0L));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
