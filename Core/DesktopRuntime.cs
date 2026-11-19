using System.Diagnostics;
using System.IO;

namespace TradeFoundry.Core;

/// <summary>
/// Runtime conventions shared by the server's desktop launch mode and the tray helper.
/// The application remains an ASP.NET Core server; this class only defines the local
/// launch contract and user-scoped configuration locations.
/// </summary>
/// <remarks>
/// <para><c>--desktop</c> binds the server to the loopback desktop URL and loads the per-user
/// configuration file.</para>
/// <para><c>--embedded</c> means a parent process (the tray helper) owns the server's lifetime.
/// The parent keeps the server's standard input open; the server shuts down gracefully when it
/// reads <see cref="ShutdownCommand"/> or reaches end of input, which also happens when the
/// parent exits or crashes. Only start the server with <c>--embedded</c> from a process that
/// redirects standard input.</para>
/// </remarks>
public static class DesktopRuntime
{
    public const string DesktopSwitch = "--desktop";
    public const string EmbeddedSwitch = "--embedded";
    public const string MutexName = "TradeFoundry.Desktop";
    public const string ShellMutexName = "TradeFoundry.DesktopShell";
    public const string DefaultUrl = "http://127.0.0.1:5080";
    public const int DefaultPort = 5080;
    public const string UrlEnvironmentVariable = "TradeFoundryDesktopUrl";
    public const string ShutdownCommand = "stop";
    public const string UserConfigurationFileName = "appsettings.user.json";

    public static bool IsDesktopMode(IEnumerable<string> args) =>
        args.Any(argument => string.Equals(argument, DesktopSwitch, StringComparison.OrdinalIgnoreCase));

    public static bool IsEmbeddedMode(IEnumerable<string> args) =>
        args.Any(argument => string.Equals(argument, EmbeddedSwitch, StringComparison.OrdinalIgnoreCase));

    public static string[] RemoveDesktopSwitch(IEnumerable<string> args) =>
        RemoveLaunchSwitches(args);

    public static string[] RemoveLaunchSwitches(IEnumerable<string> args) =>
        args.Where(argument =>
            !string.Equals(argument, DesktopSwitch, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(argument, EmbeddedSwitch, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// The URL used by desktop mode. Defaults to <see cref="DefaultUrl"/>; development and
    /// disposable test runs can override it with the <see cref="UrlEnvironmentVariable"/>
    /// environment variable.
    /// </summary>
    public static string Url => ResolveUrl(Environment.GetEnvironmentVariable(UrlEnvironmentVariable));

    /// <summary>
    /// Validates a configured desktop URL. Only plain-HTTP loopback addresses are accepted so
    /// the journal is never exposed to the network; anything else falls back to the default.
    /// </summary>
    public static string ResolveUrl(string? configuredUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl) ||
            !Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsLoopback ||
            uri.IsDefaultPort ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query))
        {
            return DefaultUrl;
        }

        return $"http://{uri.Host}:{uri.Port}";
    }

    /// <summary>
    /// Names a single-instance primitive (mutex or pipe). The default URL keeps the historical
    /// names the installer relies on; other ports get their own names so a development or test
    /// instance never collides with the installed one.
    /// </summary>
    public static string InstanceName(string baseName, string url)
    {
        var port = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Port : DefaultPort;
        return port == DefaultPort ? baseName : $"{baseName}.{port}";
    }

    /// <summary>
    /// Completes when the parent process asks the embedded server to stop: either by writing
    /// <see cref="ShutdownCommand"/> or by closing the input stream (including when it exits).
    /// </summary>
    public static Task WaitForShutdownSignalAsync(TextReader input) =>
        Task.Run(() =>
        {
            try
            {
                while (input.ReadLine() is { } line)
                {
                    if (string.Equals(line.Trim(), ShutdownCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        });

    public static string UserConfigurationDirectory =>
        Path.Combine(GetLocalApplicationData(), "TradeFoundry");

    public static string UserConfigurationPath =>
        Path.Combine(UserConfigurationDirectory, UserConfigurationFileName);

    public static string DefaultDataDirectory =>
        Path.Combine(UserConfigurationDirectory, "Data");

    public static bool TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string GetLocalApplicationData()
    {
        var directory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory;
    }
}
