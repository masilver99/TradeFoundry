using System.IO.Pipes;

namespace TradeFoundry.Desktop;

/// <summary>
/// Keeps one tray helper per user and desktop URL. A second launch (for example clicking the
/// Start menu shortcut again) connects to the running helper's pipe and asks it to open the
/// journal instead of starting another server.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string ActivateMessage = "open";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _listening = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    public static SingleInstance? TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew)
        {
            return new SingleInstance(mutex, name);
        }

        mutex.Dispose();
        return null;
    }

    public static bool SignalExisting(string name)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(TimeSpan.FromSeconds(2));
            using var writer = new StreamWriter(client);
            writer.WriteLine(ActivateMessage);
            writer.Flush();
            return true;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Invokes <paramref name="onActivate"/> (on a background thread) for each signal.</summary>
    public void StartListening(Action onActivate)
    {
        var token = _listening.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    var message = await reader.ReadLineAsync(token);
                    if (string.Equals(message, ActivateMessage, StringComparison.Ordinal))
                    {
                        onActivate();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException)
                {
                    // A client disconnected mid-message; keep listening.
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _listening.Cancel();
        _listening.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Released on a different thread than the one that acquired it; disposal still frees it.
        }

        _mutex.Dispose();
    }
}
