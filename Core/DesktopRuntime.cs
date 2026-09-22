using System.Diagnostics;
using System.IO;

namespace TradeFoundry.Core;

/// <summary>
/// Runtime conventions used by the installed Windows desktop launch mode.
/// The application remains an ASP.NET Core server; this class only defines
/// the local launch contract and user-scoped configuration locations.
/// </summary>
public static class DesktopRuntime
{
    public const string DesktopSwitch = "--desktop";
    public const string EmbeddedSwitch = "--embedded";
    public const string MutexName = "TradeFoundry.Desktop";
    public const string ShellMutexName = "TradeFoundry.DesktopShell";
    public const string DefaultUrl = "http://127.0.0.1:5080";
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
