using System.Security;
using Microsoft.Win32;

namespace TradeFoundry.Desktop;

/// <summary>
/// Per-user "start at sign-in" registration for the tray helper. Windows uses the HKCU Run key,
/// Linux an XDG autostart entry, and macOS a LaunchAgent. None of these need admin rights.
/// </summary>
internal static class StartupRegistration
{
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string WindowsValueName = "TradeFoundry";
    private const string MacAgentLabel = "com.tradefoundry.desktop";

    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public static bool IsEnabled()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey);
                return key?.GetValue(WindowsValueName) is string;
            }

            var path = UnixEntryPath();
            return path is not null && File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The tray helper's executable path is unknown.");

        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(WindowsRunKey, writable: true);
            if (enabled)
            {
                key.SetValue(WindowsValueName, $"\"{executable}\" {Program.BackgroundSwitch}");
            }
            else
            {
                key.DeleteValue(WindowsValueName, throwOnMissingValue: false);
            }

            return;
        }

        var path = UnixEntryPath() ?? throw new PlatformNotSupportedException("Start at sign-in is not supported on this platform.");
        if (!enabled)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, OperatingSystem.IsMacOS() ? MacLaunchAgent(executable) : LinuxDesktopEntry(executable));
    }

    private static string? UnixEntryPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(home, "Library", "LaunchAgents", $"{MacAgentLabel}.plist");
        }

        if (OperatingSystem.IsLinux())
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var root = string.IsNullOrWhiteSpace(configHome) ? Path.Combine(home, ".config") : configHome;
            return Path.Combine(root, "autostart", "tradefoundry.desktop");
        }

        return null;
    }

    private static string LinuxDesktopEntry(string executable) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name=TradeFoundry
        Comment=Start the TradeFoundry journal server
        Exec="{executable.Replace("\"", "\\\"")}" {Program.BackgroundSwitch}
        Terminal=false
        X-GNOME-Autostart-enabled=true

        """;

    private static string MacLaunchAgent(string executable) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{MacAgentLabel}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{SecurityElement.Escape(executable)}</string>
            <string>{Program.BackgroundSwitch}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
        </dict>
        </plist>

        """;
}
