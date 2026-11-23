using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;
using TradeFoundry.Services.Backup;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class BackupScheduleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tradefoundry-schedule-tests", Guid.NewGuid().ToString("N"));
    private static readonly BackupSchedule DailyUtc = BackupSchedule.Default with { TimeZoneId = "UTC" };

    [Fact]
    public void DailySlotsFallOnTheConfiguredTime()
    {
        var now = Utc(2026, 11, 23, 10, 0);
        Assert.Equal(Utc(2026, 11, 23, 2, 0), BackupSchedulePlanner.LatestSlotAtOrBefore(DailyUtc, now));
        Assert.Equal(Utc(2026, 11, 24, 2, 0), BackupSchedulePlanner.NextSlotAfter(DailyUtc, now));
        Assert.Equal(Utc(2026, 11, 22, 2, 0), BackupSchedulePlanner.LatestSlotAtOrBefore(DailyUtc, Utc(2026, 11, 23, 1, 59)));
    }

    [Fact]
    public void WeeklySlotsFallOnTheConfiguredDay()
    {
        var weekly = DailyUtc with { Frequency = BackupFrequency.Weekly, Day = DayOfWeek.Sunday };
        // 2026-11-23 is a Monday.
        Assert.Equal(Utc(2026, 11, 22, 2, 0), BackupSchedulePlanner.LatestSlotAtOrBefore(weekly, Utc(2026, 11, 23, 10, 0)));
        Assert.Equal(Utc(2026, 11, 29, 2, 0), BackupSchedulePlanner.NextSlotAfter(weekly, Utc(2026, 11, 23, 10, 0)));
    }

    [Fact]
    public void ATimeSkippedByDaylightSavingRunsAtTheNextValidMinute()
    {
        var schedule = DailyUtc with { TimeZoneId = "America/New_York", Time = new TimeOnly(2, 30) };
        // On 2026-03-08 New York clocks jump from 02:00 to 03:00 EDT (07:00 UTC).
        Assert.Equal(Utc(2026, 3, 8, 7, 0), BackupSchedulePlanner.NextSlotAfter(schedule, Utc(2026, 3, 8, 0, 0)));
        Assert.Equal(Utc(2026, 3, 9, 6, 30), BackupSchedulePlanner.NextSlotAfter(schedule, Utc(2026, 3, 8, 7, 0)));
    }

    [Fact]
    public void ATimeRepeatedByDaylightSavingRunsOnce()
    {
        var schedule = DailyUtc with { TimeZoneId = "America/New_York", Time = new TimeOnly(1, 30) };
        // On 2026-11-01 01:30 happens at 05:30 UTC (EDT) and again at 06:30 UTC (EST): only the first counts.
        Assert.Equal(Utc(2026, 11, 1, 5, 30), BackupSchedulePlanner.NextSlotAfter(schedule, Utc(2026, 11, 1, 0, 0)));
        Assert.Equal(Utc(2026, 11, 1, 5, 30), BackupSchedulePlanner.LatestSlotAtOrBefore(schedule, Utc(2026, 11, 1, 6, 45)));
        Assert.Equal(Utc(2026, 11, 2, 6, 30), BackupSchedulePlanner.NextSlotAfter(schedule, Utc(2026, 11, 1, 6, 45)));
    }

    [Fact]
    public void RetentionKeepsDailyWeeklyAndMonthlyAndNeverTouchesOtherBackups()
    {
        var installation = Guid.NewGuid();
        var listings = Enumerable.Range(0, 120)
            .Select(day => Listing(Utc(2026, 11, 23, 2, 0).AddDays(-day), installation, BackupTrigger.Scheduled))
            .Append(Listing(Utc(2025, 1, 1, 12, 0), installation, BackupTrigger.Manual))
            .Append(Listing(Utc(2025, 1, 2, 12, 0), Guid.NewGuid(), BackupTrigger.Scheduled, fromThisInstallation: false))
            .ToList();

        var prune = BackupRetention.SelectForPruning(listings, DailyUtc);
        var kept = listings.Except(prune).ToList();

        Assert.DoesNotContain(prune, listing => listing.Sidecar.Trigger == BackupTrigger.Manual || !listing.FromThisInstallation);
        Assert.Contains(listings[0], kept);
        var keptScheduled = kept.Where(listing => listing.FromThisInstallation && listing.Sidecar.Trigger == BackupTrigger.Scheduled).ToList();
        Assert.InRange(keptScheduled.Count, 7, 14);
        Assert.All(listings.Take(7), recent => Assert.Contains(recent, kept));
        Assert.Contains(keptScheduled, listing => listing.Sidecar.CreatedUtc < Utc(2026, 10, 1, 0, 0));
        Assert.DoesNotContain(keptScheduled, listing => listing.Sidecar.CreatedUtc < Utc(2026, 9, 1, 0, 0));
    }

    [Fact]
    public void RetentionNeverDeletesTheOnlyBackup()
    {
        var listing = Listing(Utc(2020, 1, 1, 2, 0), Guid.NewGuid(), BackupTrigger.Scheduled);
        Assert.Empty(BackupRetention.SelectForPruning([listing], DailyUtc with { KeepDaily = 1, KeepWeekly = 0, KeepMonthly = 0 }));
    }

    [Fact]
    public async Task AMissedBackupRunsOnceFiveMinutesAfterStart()
    {
        var (database, scheduler, clock) = Create();
        database.SetLastScheduledSlot(Utc(2026, 11, 22, 2, 0));
        clock.Now = Utc(2026, 11, 23, 10, 0);

        await scheduler.CheckAsync(CancellationToken.None);
        Assert.Empty(database.GetBackupRuns(10));
        Assert.Equal(Utc(2026, 11, 23, 10, 5), scheduler.NextRun);

        await TickUntilAsync(scheduler, clock, Utc(2026, 11, 23, 10, 4, 50));
        Assert.Empty(database.GetBackupRuns(10));
        await TickUntilAsync(scheduler, clock, Utc(2026, 11, 23, 10, 5, 10));
        Assert.Equal(BackupRunStatus.Completed, Assert.Single(database.GetBackupRuns(10)).Status);
        Assert.Equal(Utc(2026, 11, 23, 2, 0), database.GetLastScheduledSlot());

        await TickUntilAsync(scheduler, clock, Utc(2026, 11, 23, 10, 30, 0));
        Assert.Single(database.GetBackupRuns(10));
        Assert.Equal(Utc(2026, 11, 24, 2, 0), scheduler.NextRun);
    }

    [Fact]
    public async Task AnOnTimeBackupRunsImmediatelyAndWakingFromSleepWaitsFiveMinutes()
    {
        var (database, scheduler, clock) = Create();
        database.SetLastScheduledSlot(Utc(2026, 11, 22, 2, 0));
        clock.Now = Utc(2026, 11, 23, 1, 59, 50);
        await scheduler.CheckAsync(CancellationToken.None);
        clock.Now = Utc(2026, 11, 23, 2, 0, 20);
        await scheduler.CheckAsync(CancellationToken.None);
        Assert.Single(database.GetBackupRuns(10));

        // Asleep from 02:01 until 09:00 the next day: the 02:00 slot was missed.
        clock.Now = Utc(2026, 11, 23, 2, 1, 0);
        await scheduler.CheckAsync(CancellationToken.None);
        clock.Now = Utc(2026, 11, 24, 9, 0, 0);
        await scheduler.CheckAsync(CancellationToken.None);
        await TickUntilAsync(scheduler, clock, Utc(2026, 11, 24, 9, 4, 30));
        Assert.Single(database.GetBackupRuns(10));
        await TickUntilAsync(scheduler, clock, Utc(2026, 11, 24, 9, 5, 0));
        Assert.Equal(2, database.GetBackupRuns(10).Count);
    }

    [Fact]
    public async Task FailuresAreRetriedThreeTimesThenLeftForTheNextSlotWithAWarning()
    {
        var (database, scheduler, clock) = Create();
        database.SetLastScheduledSlot(Utc(2026, 11, 22, 2, 0));
        database.SetBackupFolder(Path.Combine(_root, "data", "inside-the-data-directory"));
        clock.Now = Utc(2026, 11, 23, 2, 0, 10);

        var times = new[] { (0, 1), (3, 1), (5, 2), (20, 2), (35, 3), (100, 3), (155, 4), (300, 4) };
        await scheduler.CheckAsync(CancellationToken.None);
        foreach (var (minutes, expectedRuns) in times)
        {
            await TickUntilAsync(scheduler, clock, Utc(2026, 11, 23, 2, 0, 10).AddMinutes(minutes));
            Assert.Equal(expectedRuns, database.GetBackupRuns(10).Count);
        }

        Assert.All(database.GetBackupRuns(10), run => Assert.Equal(BackupRunStatus.Failed, run.Status));
        Assert.Equal(Utc(2026, 11, 23, 2, 0), database.GetLastScheduledSlot());
        Assert.StartsWith("backup-failed:", scheduler.Warning?.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACleanInstallationIsNotBackedUpOnSchedule()
    {
        var (database, scheduler, clock) = Create(withJournal: false);
        clock.Now = Utc(2026, 11, 23, 2, 0, 10);

        await scheduler.CheckAsync(CancellationToken.None);

        Assert.Empty(database.GetBackupRuns(10));
        Assert.Equal(Utc(2026, 11, 23, 2, 0), database.GetLastScheduledSlot());
    }

    [Fact]
    public void AnOldLastBackupIsAWarning()
    {
        var (database, scheduler, _) = Create();
        database.InsertBackupRun(new BackupRun(Guid.NewGuid(), BackupTrigger.Scheduled, BackupRunStatus.Completed, Utc(2026, 11, 20, 2, 0), Utc(2026, 11, 20, 2, 1), 1000, "a.tfbackup", 1, string.Empty, []));

        scheduler.RefreshWarning(DailyUtc, Utc(2026, 11, 21, 12, 0));
        Assert.Null(scheduler.Warning);

        scheduler.RefreshWarning(DailyUtc, Utc(2026, 11, 22, 12, 0));
        Assert.StartsWith("backup-stale:", scheduler.Warning?.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PruningDeletesTheSidecarAndTheArchive()
    {
        var (database, _, _) = Create();
        var backups = new BackupService(database, new AppActivityGate(), Options.Create(new BackupOptions()), NullLogger<BackupService>.Instance);
        Assert.True(backups.TryStart(BackupTrigger.Scheduled, out _, out _));
        await backups.WaitAsync();
        var listing = Assert.Single(backups.ListBackups());

        Assert.Equal(1, backups.Prune([listing]));

        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "backups")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (TradeFoundryDb Database, BackupScheduler Scheduler, FakeClock Clock) Create(bool withJournal = true)
    {
        var database = new TradeFoundryDb(Options.Create(new StorageOptions { DataDirectory = Path.Combine(_root, "data"), DatabaseFileName = "journal.db" }));
        database.CreateOwner("Owner", PasswordService.Hash(""));
        if (withJournal) database.CreateJournal("Live", "live", string.Empty, "UTC", "USD", "flat_to_flat");
        database.SetBackupFolder(Path.Combine(_root, "backups"));
        database.SetBackupSchedule(DailyUtc);
        var clock = new FakeClock();
        var backups = new BackupService(database, new AppActivityGate(), Options.Create(new BackupOptions()), NullLogger<BackupService>.Instance);
        return (database, new BackupScheduler(database, backups, clock, NullLogger<BackupScheduler>.Instance), clock);
    }

    /// <summary>Advances the clock in the scheduler's real 30-second steps, checking at each one.</summary>
    private static async Task TickUntilAsync(BackupScheduler scheduler, FakeClock clock, DateTimeOffset target)
    {
        while (clock.Now < target)
        {
            var next = clock.Now + BackupScheduler.CheckInterval;
            clock.Now = next < target ? next : target;
            await scheduler.CheckAsync(CancellationToken.None);
        }
    }

    private static BackupListing Listing(DateTimeOffset created, Guid installation, string trigger, bool fromThisInstallation = true) =>
        new(new BackupSidecar(BackupManifest.FormatName, 1, Guid.NewGuid(), installation, created, trigger, "test", 1, false, $"{created:yyyyMMddHHmm}.tfbackup", 1, new string('0', 64), new Dictionary<string, long>(), []),
            $"{created:yyyyMMddHHmm}.tfbackup", fromThisInstallation);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second = 0) =>
        new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
