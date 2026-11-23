using System.Diagnostics;
using TradeFoundry.Core;

namespace TradeFoundry.Desktop;

internal enum ServerState
{
    Stopped,
    Starting,
    Running,
    /// <summary>A server is answering on the desktop URL but this helper did not start it.</summary>
    External,
    Stopping,
    Failed
}

/// <summary>
/// Starts, watches and stops the TradeFoundry server process. The server runs with
/// <c>--desktop --embedded</c> and its standard input connected to this process, so it shuts
/// down gracefully when asked and cannot outlive the tray helper.
/// </summary>
internal sealed class ServerController : IDisposable
{
    public const string ServerPathEnvironmentVariable = "TradeFoundryServerPath";

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan GracefulStopTimeout = TimeSpan.FromSeconds(10);
    private const int LogLinesKept = 80;

    private readonly object _gate = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly Queue<string> _recentOutput = new();
    private readonly Timer _externalWatch;
    private Process? _process;
    private bool _stopRequested;
    private bool _disposed;
    private readonly object _inputGate = new();
    private string? _availableUpdate;

    public ServerController(string url)
    {
        Url = url;
        _externalWatch = new Timer(_ => _ = CheckExternalServerAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Url { get; }

    public ServerState State { get; private set; } = ServerState.Stopped;

    /// <summary>Why the last start failed or the server stopped unexpectedly.</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>Recent server console output, useful when explaining a failure.</summary>
    public string RecentOutput
    {
        get
        {
            lock (_recentOutput)
            {
                return string.Join(Environment.NewLine, _recentOutput);
            }
        }
    }

    /// <summary>Raised on a background thread whenever <see cref="State"/> changes.</summary>
    public event Action? StateChanged;

    public async Task StartAsync()
    {
        lock (_gate)
        {
            if (_disposed || State is ServerState.Starting or ServerState.Running or ServerState.External or ServerState.Stopping)
            {
                return;
            }

            SetState(ServerState.Starting);
        }

        if (await IsServerReadyAsync(CancellationToken.None))
        {
            lock (_gate)
            {
                SetState(ServerState.External);
                _externalWatch.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }

            return;
        }

        var serverPath = ResolveServerPath();
        if (!File.Exists(serverPath))
        {
            Fail($"The TradeFoundry server was not found at '{serverPath}'. Reinstall TradeFoundry, or set {ServerPathEnvironmentVariable} when running from a development checkout.");
            return;
        }

        lock (_recentOutput)
        {
            _recentOutput.Clear();
        }

        var startInfo = new ProcessStartInfo(serverPath)
        {
            WorkingDirectory = Path.GetDirectoryName(serverPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(DesktopRuntime.DesktopSwitch);
        startInfo.ArgumentList.Add(DesktopRuntime.EmbeddedSwitch);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => RecordOutput(e.Data);
        process.ErrorDataReceived += (_, e) => RecordOutput(e.Data);
        process.Exited += (_, _) => OnProcessExited(process);

        lock (_gate)
        {
            _stopRequested = false;
            _process = process;
        }

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (_availableUpdate is { } pendingUpdate)
            {
                SendCommand(process, DesktopRuntime.FormatUpdateCommand(pendingUpdate));
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lock (_gate)
            {
                _process = null;
            }

            process.Dispose();
            Fail($"The TradeFoundry server could not be started: {exception.Message}");
            return;
        }

        var deadline = DateTime.UtcNow + StartupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                // Once the process exits (or a stop was requested meanwhile), OnProcessExited
                // and StopAsync report the outcome. The process is only disposed under _gate.
                if (!ReferenceEquals(_process, process) || process.HasExited)
                {
                    return;
                }
            }

            if (await IsServerReadyAsync(CancellationToken.None))
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_process, process) && State == ServerState.Starting)
                    {
                        SetState(ServerState.Running);
                    }
                }

                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        await StopAsync();
        Fail($"TradeFoundry did not become ready within {StartupTimeout.TotalSeconds:0} seconds.");
    }

    /// <summary>Stops the server this helper started. A server started elsewhere is left alone.</summary>
    public async Task StopAsync()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            if (process is null)
            {
                if (State is not ServerState.External)
                {
                    SetState(ServerState.Stopped);
                }

                return;
            }

            _stopRequested = true;
            SetState(ServerState.Stopping);
        }

        RequestGracefulStop(process);

        using var timeout = new CancellationTokenSource(GracefulStopTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
        }
        catch (InvalidOperationException)
        {
        }

        lock (_gate)
        {
            if (ReferenceEquals(_process, process))
            {
                ReleaseProcess();
                SetState(ServerState.Stopped);
            }
        }
    }

    public async Task<bool> IsServerReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            // /health answers 200 during maintenance too, so a backup never looks like a stopped
            // server. An older external server without /health answers 404, which still counts.
            using var response = await _httpClient.GetAsync(Url.TrimEnd('/') + DesktopRuntime.HealthPath, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (int)response.StatusCode < 500;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task CheckExternalServerAsync()
    {
        if (State != ServerState.External || await IsServerReadyAsync(CancellationToken.None))
        {
            return;
        }

        lock (_gate)
        {
            if (State == ServerState.External)
            {
                _externalWatch.Change(Timeout.Infinite, Timeout.Infinite);
                SetState(ServerState.Stopped);
            }
        }
    }

    private void OnProcessExited(Process process)
    {
        int? exitCode = null;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
        }

        lock (_gate)
        {
            if (!ReferenceEquals(_process, process))
            {
                return;
            }

            var wasStarting = State == ServerState.Starting;
            var stopRequested = _stopRequested;
            ReleaseProcess();

            if (stopRequested || _disposed)
            {
                SetState(ServerState.Stopped);
                return;
            }

            var error = FirstErrorLine();
            FailureMessage = (wasStarting, error, exitCode) switch
            {
                (true, null, 0) => "The TradeFoundry server exited during startup. Another TradeFoundry server may already be running.",
                (true, null, _) => $"The TradeFoundry server stopped during startup (exit code {exitCode}). Port {new Uri(Url).Port} may be in use by another program.",
                (true, _, _) => $"The TradeFoundry server could not start:\n{error}",
                (false, null, _) => $"The TradeFoundry server stopped unexpectedly (exit code {exitCode}).",
                (false, _, _) => $"The TradeFoundry server stopped unexpectedly:\n{error}"
            };
            SetState(ServerState.Failed);
        }
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            FailureMessage = message;
            SetState(ServerState.Failed);
        }
    }

    // Callers hold _gate.
    private void SetState(ServerState state)
    {
        if (state == ServerState.Starting)
        {
            FailureMessage = null;
        }

        if (State == state)
        {
            return;
        }

        State = state;
        var handler = StateChanged;
        if (handler is not null)
        {
            ThreadPool.QueueUserWorkItem(_ => handler());
        }
    }

    // Callers hold _gate.
    private void ReleaseProcess()
    {
        _process?.Dispose();
        _process = null;
    }

    private void RecordOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_recentOutput)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > LogLinesKept)
            {
                _recentOutput.Dequeue();
            }
        }
    }

    /// <summary>The message of the first exception in the captured output, if any.</summary>
    private string? FirstErrorLine()
    {
        lock (_recentOutput)
        {
            foreach (var line in _recentOutput)
            {
                var marker = line.IndexOf("Exception: ", StringComparison.Ordinal);
                if (marker > 0 && !line.TrimStart().StartsWith("at ", StringComparison.Ordinal))
                {
                    return line[(marker + "Exception: ".Length)..].Trim();
                }
            }
        }

        return null;
    }

    private void RequestGracefulStop(Process process)
    {
        lock (_inputGate)
        {
            try
            {
                process.StandardInput.WriteLine(DesktopRuntime.ShutdownCommand);
                process.StandardInput.Close();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The server already exited or closed its input.
            }
        }
    }

    /// <summary>
    /// Tells the server which update (if any) is pending so the app can show it. The value is
    /// remembered and sent again whenever the server is restarted.
    /// </summary>
    public void SetAvailableUpdate(string? version)
    {
        Process? process;
        lock (_gate)
        {
            if (string.Equals(_availableUpdate, version, StringComparison.Ordinal))
            {
                return;
            }

            _availableUpdate = version;
            process = _process;
        }

        if (process is not null)
        {
            SendCommand(process, DesktopRuntime.FormatUpdateCommand(version));
        }
    }

    private void SendCommand(Process process, string command)
    {
        lock (_inputGate)
        {
            try
            {
                process.StandardInput.WriteLine(command);
                process.StandardInput.Flush();
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // The server is exiting; it gets the current value when it next starts.
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string ResolveServerPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable(ServerPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var executableName = OperatingSystem.IsWindows() ? "TradeFoundry.exe" : "TradeFoundry";
        return Path.Combine(AppContext.BaseDirectory, executableName);
    }

    /// <summary>Synchronous shutdown used when the application exits.</summary>
    public void Dispose()
    {
        Process? process;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopRequested = true;
            process = _process;
        }

        _externalWatch.Dispose();

        if (process is not null)
        {
            RequestGracefulStop(process);
            try
            {
                if (!process.WaitForExit((int)GracefulStopTimeout.TotalMilliseconds))
                {
                    Kill(process);
                }
            }
            catch (InvalidOperationException)
            {
            }

            lock (_gate)
            {
                ReleaseProcess();
            }
        }

        _httpClient.Dispose();
    }
}
