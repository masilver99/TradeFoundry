using Avalonia;
using TradeFoundry.Core;

namespace TradeFoundry.Desktop;

internal static class Program
{
    /// <summary>Launched at sign-in: start the server without opening the browser.</summary>
    public const string BackgroundSwitch = "--background";

    [STAThread]
    public static int Main(string[] args)
    {
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
            OpenBrowserOnStart: !args.Any(argument => string.Equals(argument, BackgroundSwitch, StringComparison.OrdinalIgnoreCase)),
            Instance: instance);

        return BuildAvaloniaApp(options).StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Used by the Avalonia designer/tooling; the real entry point passes options.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(null);

    private static AppBuilder BuildAvaloniaApp(TrayOptions? options) =>
        AppBuilder.Configure(() => new App(options))
            .UsePlatformDetect()
            .LogToTrace();
}

internal sealed record TrayOptions(string Url, bool OpenBrowserOnStart, SingleInstance Instance);
