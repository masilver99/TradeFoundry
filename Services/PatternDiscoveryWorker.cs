using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services.Backup;

namespace TradeFoundry.Services;

public sealed class PatternDiscoveryWorker(
    TradeFoundryDb database, AppActivityGate activityGate, TimeProvider time, ILogger<PatternDiscoveryWorker> logger) : BackgroundService
{
    private const string AlgorithmVersion = "2";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _requested = new(0, 1);
    private readonly object _requestGate = new();

    public void RequestCheck()
    {
        lock (_requestGate)
        {
            if (_requested.CurrentCount == 0) _requested.Release();
        }
    }

    public (PatternDiscoverySnapshot? Snapshot, bool IsCurrent) Read(Guid journalId)
    {
        var snapshot = database.GetPatternSnapshot(journalId);
        return snapshot is { } saved
            ? (saved.Snapshot, saved.Version == InputVersion(journalId))
            : (null, false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAsync(stoppingToken);
                await _requested.WaitAsync(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Pattern discovery could not check for new journal evidence.");
                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
    }

    internal async Task CheckAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Journal> journals;
        using (await activityGate.EnterAsync(longRunning: false, cancellationToken))
            journals = database.GetJournals();

        foreach (var journal in journals)
        {
            using var lease = await activityGate.EnterAsync(longRunning: false, cancellationToken);
            try
            {
                var version = InputVersion(journal.Id);
                var previous = database.GetPatternSnapshot(journal.Id);
                if (previous is { } saved && saved.Version == version) continue;
                var evidence = new PatternEvidence(database).Load(journal, cancellationToken);
                var priorReports = previous?.Snapshot.Instruments ?? [];
                var instruments = evidence.OrderBy(item => item.Key).Select(item =>
                    PatternDiscovery.Scan(item.Key, item.Value, priorReports.FirstOrDefault(report => report.Instrument == item.Key), cancellationToken)).ToArray();
                if (version == InputVersion(journal.Id))
                    database.SavePatternSnapshot(journal.Id, version, new PatternDiscoverySnapshot(time.GetUtcNow(), instruments));
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception, "Pattern discovery could not scan journal {JournalId}.", journal.Id);
            }
        }
    }

    private string InputVersion(Guid journalId) => $"{AlgorithmVersion}:{database.GetPatternInputVersion(journalId)}";
}
