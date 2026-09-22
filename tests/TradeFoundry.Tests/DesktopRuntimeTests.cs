using TradeFoundry.Core;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class DesktopRuntimeTests
{
    [Fact]
    public void DesktopSwitchIsDetectedCaseInsensitively()
    {
        Assert.True(DesktopRuntime.IsDesktopMode(["--desktop"]));
        Assert.True(DesktopRuntime.IsDesktopMode(["--DESKTOP"]));
        Assert.False(DesktopRuntime.IsDesktopMode(["--urls", "http://127.0.0.1:5080"]));
    }

    [Fact]
    public void EmbeddedSwitchIsDetectedCaseInsensitively()
    {
        Assert.True(DesktopRuntime.IsEmbeddedMode(["--embedded"]));
        Assert.True(DesktopRuntime.IsEmbeddedMode(["--EMBEDDED"]));
        Assert.False(DesktopRuntime.IsEmbeddedMode(["--desktop"]));
    }

    [Fact]
    public void DesktopSwitchIsRemovedBeforeAspNetConfigurationParsing()
    {
        var arguments = DesktopRuntime.RemoveLaunchSwitches(["--desktop", "--embedded", "--urls", "http://127.0.0.1:5080"]);

        Assert.Equal(["--urls", "http://127.0.0.1:5080"], arguments);
    }

    [Fact]
    public void InstalledRuntimeUsesAStableLoopbackContract()
    {
        Assert.Equal("http://127.0.0.1:5080", DesktopRuntime.DefaultUrl);
        Assert.EndsWith("TradeFoundry", DesktopRuntime.UserConfigurationDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Data", DesktopRuntime.DefaultDataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("appsettings.user.json", DesktopRuntime.UserConfigurationPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("TradeFoundry.DesktopShell", DesktopRuntime.ShellMutexName);
    }
}
