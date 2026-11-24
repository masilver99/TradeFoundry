using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services.Backup;

/// <summary>A reason the owner should look at backups, shown in the notifications panel and the tray.</summary>
public sealed record BackupWarning(string Key, string Message);

/// <summary>
/// Runs scheduled backups. It checks the wall clock every 30 seconds rather than sleeping until the next
/// slot, so sleep, hibernation and clock changes are handled by the next check.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>A slot missed while TradeFoundry was off or asleep runs once, five minutes after start or wake.</item>
/// <item>A failed run is retried after 5, 30 and 120 minutes, then left for the next slot.</item>
/// <item>Retention runs only after a successful scheduled backup.</item>
/// <item>An installation with no journals is never backed up on schedule: its empty backups would
/// otherwise push real ones out of retention.</item>
/// </list>
/// </remarks>
public sealed class BackupScheduler : BackgroundService
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CatchUpDelay = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(120)];
    private static readonly TimeSpan WakeGap = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(2);

    private readonly TradeFoundryDb _database;
    private readonly BackupService _backups;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupScheduler> _logger;
    private DateTimeOffset? _lastCheck;
    private DateTimeOffset _resumedAt;
    private DateTimeOffset? _retryAt;
    private DateTimeOffset? _retrySlot;
    private int _failures;
    private Guid? _uploadRun;
    private int _uploadFailures;
    private DateTimeOffset? _uploadRetryAt;
    private volatile BackupWarning? _warning;

    public BackupScheduler(TradeFoundryDb database, BackupService backups, TimeProvider time, ILogger<BackupScheduler> logger)
    {
        _database = database;
        _backups = backups;
        _time = time;
        _logger = logger;
    }

    /// <summary>The current warning, refreshed on every check. Read from memory, so it is safe during maintenance.</summary>
    public BackupWarning? Warning => _warning;

    public DateTimeOffset? NextRun { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The backup schedule check failed.");
            }

            try
            {
                await Task.Delay(CheckInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One check of the schedule. Runs a backup and waits for it when one is due.</summary>
    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        // The first check, or one after a long gap (sleep), starts the catch-up delay.
        if (_lastCheck is null || now - _lastCheck > WakeGap) _resumedAt = now;
        _lastCheck = now;

        var schedule = _database.GetBackupSchedule();
        try
        {
            await RetryUploadAsync(now, cancellationToken);
            if (!schedule.Enabled)
            {
                NextRun = null;
                return;
            }

            var slot = BackupSchedulePlanner.LatestSlotAtOrBefore(schedule, now);
            NextRun = BackupSchedulePlanner.NextSlotAfter(schedule, now);
            if (_database.GetLastScheduledSlot() is { } handled && slot <= handled) return;
            if (_retrySlot != slot)
            {
                _retrySlot = slot;
                _retryAt = null;
                _failures = 0;
            }

            if (_retryAt is { } retryAt && now < retryAt)
            {
                NextRun = retryAt;
                return;
            }

            if (now - slot > LateAfter && now < _resumedAt + CatchUpDelay)
            {
                NextRun = _resumedAt + CatchUpDelay;
                return;
            }

            if (_database.IsClean())
            {
                _database.SetLastScheduledSlot(slot);
                return;
            }

            if (!_backups.TryStart(BackupTrigger.Scheduled, out var runId, out var message))
            {
                _logger.LogInformation("Scheduled backup postponed: {Reason}", message);
                _retryAt = now + RetryDelays[0];
                return;
            }

            await _backups.WaitAsync();
            var run = _database.GetBackupRun(runId);
            var after = _time.GetUtcNow();
            if (run?.Status is BackupRunStatus.Completed or BackupRunStatus.CompletedWithWarnings)
            {
                _database.SetLastScheduledSlot(slot);
                _retryAt = null;
                _failures = 0;
                Prune(schedule);
                if (run.CloudStatus == BackupCloudStatus.Uploaded) await _backups.RefreshCloudAsync(schedule, cancellationToken);
            }
            else if (run?.Status == BackupRunStatus.Cancelled && cancellationToken.IsCancellationRequested)
            {
                // Shutting down: the slot counts as missed and runs again after the next start.
            }
            else if (++_failures > RetryDelays.Length)
            {
                _logger.LogWarning("Scheduled backup for {Slot} failed {Count} times; waiting for the next scheduled time.", slot, _failures);
                _database.SetLastScheduledSlot(slot);
                _retryAt = null;
            }
            else
            {
                _retryAt = after + RetryDelays[_failures - 1];
                _logger.LogWarning("Scheduled backup failed; retrying at {RetryAt}.", _retryAt);
            }
        }
        finally
        {
            RefreshWarning(schedule, _time.GetUtcNow());
        }
    }

    private void Prune(BackupSchedule schedule)
    {
        try
        {
            var prune = BackupRetention.SelectForPruning(_backups.ListBackups(), schedule);
            if (prune.Count > 0) _backups.Prune(prune);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not apply backup retention.");
        }
    }

    /// <summary>
    /// Uploads the newest backup again when its upload to B2 failed: 5, 30 and 120 minutes after each
    /// failure, without pausing the site, because the archive is already in the folder.
    /// </summary>
    private async Task RetryUploadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!_backups.CloudEnabled || _backups.Progress.Running) return;
        var latest = _database.GetBackupRuns(10).FirstOrDefault(run => run.Status is BackupRunStatus.Completed or BackupRunStatus.CompletedWithWarnings);
        if (latest is not { CloudStatus: BackupCloudStatus.Failed }) return;
        if (_uploadRun != latest.Id)
        {
            _uploadRun = latest.Id;
            _uploadFailures = 0;
            _uploadRetryAt = (latest.FinishedUtc ?? now) + RetryDelays[0];
        }

        if (_uploadFailures >= RetryDelays.Length || now < _uploadRetryAt) return;
        if (!_backups.TryStartUpload(latest.Id, out var message))
        {
            // Counts as an attempt, so a backup that left the folder is not retried forever.
            _logger.LogInformation("Upload retry did not start: {Reason}", message);
            _uploadFailures++;
            if (_uploadFailures < RetryDelays.Length) _uploadRetryAt = now + RetryDelays[_uploadFailures];
            return;
        }

        await _backups.WaitAsync();
        if (_database.GetBackupRun(latest.Id)?.CloudStatus == BackupCloudStatus.Uploaded) return;
        _uploadFailures++;
        if (_uploadFailures < RetryDelays.Length) _uploadRetryAt = _time.GetUtcNow() + RetryDelays[_uploadFailures];
    }

    internal void RefreshWarning(BackupSchedule schedule, DateTimeOffset now)
    {
        try
        {
            var runs = _database.GetBackupRuns(50);
            var latest = runs.FirstOrDefault(run => run.Status != BackupRunStatus.Running);
            if (latest?.Status is BackupRunStatus.Failed or BackupRunStatus.Interrupted)
            {
                _warning = new BackupWarning($"backup-failed:{latest.Id:D}", $"The last backup failed: {latest.Message}");
                return;
            }

            if (latest is { CloudStatus: BackupCloudStatus.Failed } && _backups.CloudEnabled)
            {
                _warning = new BackupWarning($"backup-upload-failed:{latest.Id:D}", $"The last backup was saved to the folder but not uploaded to Backblaze B2. {latest.CloudMessage}");
                return;
            }

            var missing = _database.GetMissingCloudBackups();
            if (missing.Count > 0 && _backups.CloudEnabled)
            {
                _warning = new BackupWarning($"backup-cloud-missing:{missing.Count}:{missing[0]}",
                    $"{(missing.Count == 1 ? "A backup" : $"{missing.Count} backups")} uploaded to Backblaze B2 can no longer be seen in the bucket, although TradeFoundry did not prune {(missing.Count == 1 ? "it" : "them")}. Hidden files are erased when the bucket's recovery window ends, so check the bucket now. Restore can still list hidden backups.");
                return;
            }

            var lastGood = runs.FirstOrDefault(run => run.Status is BackupRunStatus.Completed or BackupRunStatus.CompletedWithWarnings);
            if (schedule.Enabled && lastGood is not null && now - lastGood.StartedUtc > schedule.Interval * 2 && !_database.IsClean())
            {
                _warning = new BackupWarning($"backup-stale:{lastGood.Id:D}", $"The last backup is from {lastGood.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm}. Scheduled backups have not succeeded since.");
                return;
            }

            _warning = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check backup health.");
        }
    }
}
