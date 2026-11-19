using Avalonia;
using TradeFoundry.Core;

namespace TradeFoundry.Desktop;

internal static class Program
{
    /// <summary>Launched at sign-in: start the server without opening the browser.</summary>
    public const string BackgroundSwitch = "--background";

    /// <summary>Launched by Setup after a silent update: wait for Setup to exit, then start.</summary>
    public const string AfterUpdateSwitch = "--after-update";

    /// <summary>Added by Setup when a silent update did not finish, so the menu can say so.</summary>
    public const string UpdateFailedSwitch = "--update-failed";

    /// <summary>Held by the installer while it runs (Inno Setup's SetupMutex directive).</summary>
    private const string SetupMutexName = "TradeFoundry.Setup";

    private static readonly TimeSpan AfterUpdateWait = TimeSpan.FromMinutes(3);

    [STAThread]
    public static int Main(string[] args)
    {
        var afterUpdate = HasSwitch(args, AfterUpdateSwitch);
        if (IsSetupRunning())
        {
            if (!afterUpdate)
            {
                // A silent update is replacing these files. Starting now could lock them, and
                // Setup starts TradeFoundry again itself when it finishes.
                return 0;
            }

            WaitForSetupToExit(AfterUpdateWait);
        }

        var url = DesktopRuntime.Url;
        var instanceName = DesktopRuntime.InstanceName(DesktopRuntime.ShellMutexName, url);

        using var instance = SingleInstance.TryAcquire(instanceName);
        if (instance is null)
        {
            // Another tray helper is already running: ask it to open the journal and exit.
            if (!SingleInstance.SignalExisting(instanceName))
            {
                DesktopRuntime.TryOpenBrowser(url);
            }

            return 0;
        }

        var options = new TrayOptions(
            url,
            OpenBrowserOnStart: !HasSwitch(args, BackgroundSwitch),
            UpdateFailed: HasSwitch(args, UpdateFailedSwitch),
            Instance: instance);

        return BuildAvaloniaApp(options).StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Used by the Avalonia designer/tooling; the real entry point passes options.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(TrayOptions? options) =>
        AppBuilder.Configure(() => new App(options))
            .UsePlatformDetect()
            .LogToTrace();

    private static bool HasSwitch(string[] args, string name) =>
        args.Any(argument => string.Equals(argument, name, StringComparison.OrdinalIgnoreCase));

    private static bool IsSetupRunning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (Mutex.TryOpenExisting(SetupMutexName, out var mutex))
        {
            mutex.Dispose();
            return true;
        }

        return false;
    }

    private static void WaitForSetupToExit(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsSetupRunning() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(250);
        }
    }
}

internal sealed record TrayOptions(string Url, bool OpenBrowserOnStart, bool UpdateFailed, SingleInstance Instance);
