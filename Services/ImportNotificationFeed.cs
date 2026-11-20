namespace TradeFoundry.Services;

/// <summary>
/// Recent results of automatic imports from monitored folders and files. The import monitor runs in
/// the background, so the layout renders these as notifications and open pages poll for new ones.
/// The feed is in memory only: once a browser captures a notice it keeps it in its own notification list.
/// </summary>
public sealed class ImportNotificationFeed
{
    private const int Capacity = 50;
    private readonly object _gate = new();
    private readonly LinkedList<ImportNotification> _items = new();
    private long _sequence;

    public void Add(Guid journalId, string severity, string message)
    {
        lock (_gate)
        {
            _items.AddLast(new ImportNotification(++_sequence, Guid.NewGuid(), journalId, severity, message, DateTimeOffset.UtcNow));
            while (_items.Count > Capacity) _items.RemoveFirst();
        }
    }

    public long LatestSequence
    {
        get { lock (_gate) return _sequence; }
    }

    /// <summary>Notifications newer than <paramref name="afterSequence"/>, oldest first.</summary>
    public IReadOnlyList<ImportNotification> Since(long afterSequence)
    {
        lock (_gate) return _items.Where(item => item.Sequence > afterSequence).ToArray();
    }
}

public sealed record ImportNotification(long Sequence, Guid Key, Guid JournalId, string Severity, string Message, DateTimeOffset CreatedUtc);
