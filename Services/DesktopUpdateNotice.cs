using TradeFoundry.Core;

namespace TradeFoundry.Services;

/// <summary>
/// The update the tray helper has found, if any. The tray tells the embedded server over its
/// standard input (see <see cref="DesktopRuntime.ReadParentCommandsAsync"/>), and the layout
/// shows it as an in-app notification. Without the tray (Docker, development) it stays empty.
/// </summary>
public sealed class DesktopUpdateNotice
{
    private volatile string? _availableVersion;

    public string? AvailableVersion => _availableVersion;

    /// <summary>Applies a tray command; returns false if it was not an update command.</summary>
    public bool Apply(string command)
    {
        if (!DesktopRuntime.TryParseUpdateCommand(command, out var version))
        {
            return false;
        }

        _availableVersion = version;
        return true;
    }
}
