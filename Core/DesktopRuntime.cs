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

    /// <summary>
    /// Anonymous readiness probe used by the tray helper. It reports only whether the server is
    /// ready or in maintenance, and keeps answering 200 while a backup holds the site.
    /// </summary>
    public const string HealthPath = "/health";

    /// <summary>
    /// Host names desktop mode accepts. Desktop mode binds only to loopback, but a browser sends
    /// whatever name the page was loaded from, so a malicious site could otherwise point its own
    /// name at 127.0.0.1 (DNS rebinding) and talk to the journal as same-origin.
    /// </summary>
    public const string DesktopAllowedHosts = "localhost;127.0.0.1;[::1]";

    /// <summary>
    /// <see cref="DesktopAllowedHosts"/> plus the host of <paramref name="url"/>, which can be another
    /// loopback address when a test or development run overrides the desktop URL.
    /// </summary>
    public static string AllowedHostsFor(string url)
    {
        var hosts = DesktopAllowedHosts.Split(';').ToList();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsLoopback &&
            !hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            hosts.Add(uri.Host);
        }

        return string.Join(';', hosts);
    }

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
        ReadParentCommandsAsync(input, onCommand: null);

    /// <summary>
    /// Reads commands from the tray helper until it asks the server to stop or goes away.
    /// Lines other than <see cref="ShutdownCommand"/> are passed to <paramref name="onCommand"/>
    /// (for example <c>update-available 1.2.0</c>, see <see cref="TryParseUpdateCommand"/>).
    /// </summary>
    public static Task ReadParentCommandsAsync(TextReader input, Action<string>? onCommand) =>
        Task.Run(() =>
        {
            try
            {
                while (input.ReadLine() is { } line)
                {
                    var command = line.Trim();
                    if (string.Equals(command, ShutdownCommand, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    if (command.Length > 0)
                    {
                        onCommand?.Invoke(command);
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

    /// <summary>Tray → server: a newer release is available (followed by its version).</summary>
    public const string UpdateAvailableCommand = "update-available";

    /// <summary>Tray → server: start a manual backup now.</summary>
    public const string BackupNowCommand = "backup-now";

    /// <summary>Signed-in page that opens Settings &gt; Backups for the first journal.</summary>
    public const string BackupsPath = "/backups";

    /// <summary>Tray → server: no update is pending.</summary>
    public const string NoUpdateCommand = "update-none";

    public static string FormatUpdateCommand(string? availableVersion) =>
        availableVersion is null ? NoUpdateCommand : $"{UpdateAvailableCommand} {availableVersion}";

    /// <summary>
    /// Parses an update command. Returns false for anything else; on success
    /// <paramref name="availableVersion"/> is the pending version, or null when none is pending.
    /// Versions are limited to semantic-version characters.
    /// </summary>
    public static bool TryParseUpdateCommand(string command, out string? availableVersion)
    {
        availableVersion = null;
        if (string.Equals(command, NoUpdateCommand, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = UpdateAvailableCommand + " ";
        if (!command.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var version = command[prefix.Length..].Trim();
        if (version.Length is 0 or > 64 || !version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-'))
        {
            return false;
        }

        availableVersion = version;
        return true;
    }

    public static string UserConfigurationDirectory =>
        Path.Combine(GetLocalApplicationData(), "TradeFoundry");

    public static string UserConfigurationPath =>
        Path.Combine(UserConfigurationDirectory, UserConfigurationFileName);

    public static string DefaultDataDirectory =>
        Path.Combine(UserConfigurationDirectory, "Data");

    public static string DefaultBackupDirectory =>
        Path.Combine(UserConfigurationDirectory, "Backups");

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
