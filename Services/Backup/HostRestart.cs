using TradeFoundry.Data;

namespace TradeFoundry.Services.Backup;

/// <summary>
/// Asks Program to stop the web host and build a new one in the same process, so a pending restore,
/// start clean or put back runs before anything opens the database again.
/// </summary>
public sealed class HostRestart
{
    private int _requested;

    public bool IsRequested => Volatile.Read(ref _requested) == 1;

    public void Request(IHostApplicationLifetime lifetime)
    {
        Volatile.Write(ref _requested, 1);
        lifetime.StopApplication();
    }

    /// <summary>Returns true once per request and resets.</summary>
    public bool TryConsume() => Interlocked.Exchange(ref _requested, 0) == 1;
}

/// <summary>How this process was launched, for rules that differ between the desktop app and a server.</summary>
public sealed record HostRuntimeInfo(bool IsDesktop);

/// <summary>
/// The current sign-in epoch, read once per host. A restore or start clean writes a new epoch into the
/// database, and the rebuilt host then rejects cookies issued before it.
/// </summary>
public sealed class SessionEpoch(TradeFoundryDb database)
{
    public const string ClaimType = "tradefoundry:epoch";
    private readonly Lazy<string?> _current = new(database.GetSessionEpoch);

    public string? Current => _current.Value;

    /// <summary>A cookie is valid when it carries the current epoch, or when no epoch has been set yet.</summary>
    public bool Accepts(string? claim) => Current is null || string.Equals(claim, Current, StringComparison.Ordinal);
}

/// <summary>Identifies one web host instance, so a page can tell when the restart it asked for has finished.</summary>
public sealed record HostInstance(string Id);
