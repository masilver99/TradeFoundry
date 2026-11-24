namespace TradeFoundry.Services.Backup;

/// <summary>
/// Coordinates normal work with maintenance (a backup snapshot). Every request and background work
/// item holds a shared lease while it runs. Maintenance is exclusive: once it starts, new leases wait
/// until it ends, and maintenance itself starts only when every existing lease has been released.
/// </summary>
/// <remarks>
/// Leases are counted, not owned by a thread, so they may be awaited across. Long work (an import)
/// is counted separately, so a backup can wait for it to finish without blocking anyone.
/// </remarks>
public sealed class AppActivityGate
{
    private readonly object _sync = new();
    private int _active;
    private int _long;
    private bool _maintenance;
    private TaskCompletionSource _open = CompletedSource();
    private TaskCompletionSource? _drained;
    private TaskCompletionSource? _longIdle;

    public bool InMaintenance
    {
        get { lock (_sync) return _maintenance; }
    }

    public int ActiveCount
    {
        get { lock (_sync) return _active; }
    }

    /// <summary>Takes a lease at once, or returns false while maintenance is running.</summary>
    public bool TryEnter(out IDisposable lease, bool longRunning = false)
    {
        lock (_sync)
        {
            if (_maintenance)
            {
                lease = NoLease.Instance;
                return false;
            }

            lease = Increment(longRunning);
            return true;
        }
    }

    /// <summary>Takes a lease, waiting for maintenance to end first.</summary>
    public async ValueTask<IDisposable> EnterAsync(bool longRunning, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task open;
            lock (_sync)
            {
                if (!_maintenance) return Increment(longRunning);
                open = _open.Task;
            }

            await open.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Waits until no long work is running, without blocking new work. Returns false on timeout.
    /// </summary>
    public async Task<bool> WaitForLongWorkAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task idle;
        lock (_sync)
        {
            if (_long == 0) return true;
            _longIdle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            idle = _longIdle.Task;
        }

        try
        {
            await idle.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Starts maintenance: new leases wait from now on, and this completes once existing leases are
    /// released. Returns null, with maintenance already ended, if they are not released in time.
    /// Dispose the result to end maintenance.
    /// </summary>
    public async Task<IDisposable?> TryEnterMaintenanceAsync(TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        Task drained;
        lock (_sync)
        {
            if (_maintenance) throw new InvalidOperationException("Maintenance is already running.");
            _maintenance = true;
            _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_active == 0) return new MaintenanceWindow(this);
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drained = _drained.Task;
        }

        try
        {
            await drained.WaitAsync(drainTimeout, cancellationToken).ConfigureAwait(false);
            return new MaintenanceWindow(this);
        }
        catch (TimeoutException)
        {
            LeaveMaintenance();
            return null;
        }
        catch
        {
            LeaveMaintenance();
            throw;
        }
    }

    private Lease Increment(bool longRunning)
    {
        _active++;
        if (longRunning) _long++;
        return new Lease(this, longRunning);
    }

    private void Exit(bool longRunning)
    {
        lock (_sync)
        {
            _active--;
            if (longRunning) _long--;
            if (_active == 0 && _drained is not null)
            {
                _drained.TrySetResult();
                _drained = null;
            }

            if (_long == 0 && _longIdle is not null)
            {
                _longIdle.TrySetResult();
                _longIdle = null;
            }
        }
    }

    private void LeaveMaintenance()
    {
        lock (_sync)
        {
            if (!_maintenance) return;
            _maintenance = false;
            _drained = null;
            _open.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }

    private sealed class Lease(AppActivityGate gate, bool longRunning) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Exit(longRunning);
        }
    }

    private sealed class MaintenanceWindow(AppActivityGate gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.LeaveMaintenance();
        }
    }

    private sealed class NoLease : IDisposable
    {
        public static readonly NoLease Instance = new();
        public void Dispose() { }
    }
}
