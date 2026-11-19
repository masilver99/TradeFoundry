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

    [Theory]
    [InlineData(null, "http://127.0.0.1:5080")]
    [InlineData("", "http://127.0.0.1:5080")]
    [InlineData("http://127.0.0.1:5091", "http://127.0.0.1:5091")]
    [InlineData("http://localhost:5092/", "http://localhost:5092")]
    [InlineData("http://[::1]:5093", "http://[::1]:5093")]
    public void DesktopUrlAcceptsLoopbackOverrides(string? configured, string expected)
    {
        Assert.Equal(expected, DesktopRuntime.ResolveUrl(configured));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5091")]
    [InlineData("http://192.168.1.20:5091")]
    [InlineData("https://127.0.0.1:5091")]
    [InlineData("http://127.0.0.1")]
    [InlineData("http://127.0.0.1:5091/journal")]
    [InlineData("not a url")]
    public void DesktopUrlRejectsAnythingThatIsNotPlainLoopbackHttp(string configured)
    {
        Assert.Equal(DesktopRuntime.DefaultUrl, DesktopRuntime.ResolveUrl(configured));
    }

    [Fact]
    public void InstanceNamesKeepInstallerNamesForTheDefaultPortOnly()
    {
        Assert.Equal(DesktopRuntime.MutexName, DesktopRuntime.InstanceName(DesktopRuntime.MutexName, DesktopRuntime.DefaultUrl));
        Assert.Equal("TradeFoundry.Desktop.5091", DesktopRuntime.InstanceName(DesktopRuntime.MutexName, "http://127.0.0.1:5091"));
    }

    [Fact]
    public async Task EmbeddedServerStopsOnShutdownCommand()
    {
        using var input = new StringReader("hello\nSTOP\nnever read\n");

        await DesktopRuntime.WaitForShutdownSignalAsync(input).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("never read", input.ReadLine());
    }

    [Fact]
    public async Task ParentCommandsArePassedOnUntilStop()
    {
        using var input = new StringReader("update-available 1.3.0\n\nupdate-none\nstop\nupdate-available 9.9.9\n");
        var received = new List<string>();

        await DesktopRuntime.ReadParentCommandsAsync(input, received.Add).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["update-available 1.3.0", "update-none"], received);
    }

    [Theory]
    [InlineData("update-available 1.3.0", true, "1.3.0")]
    [InlineData("UPDATE-AVAILABLE 1.3.0-rc.1", true, "1.3.0-rc.1")]
    [InlineData("update-none", true, null)]
    [InlineData("update-available", false, null)]
    [InlineData("update-available <script>", false, null)]
    [InlineData("update-available 1.3.0 extra", false, null)]
    [InlineData("hello", false, null)]
    public void UpdateCommandsAreParsedStrictly(string command, bool parsed, string? version)
    {
        Assert.Equal(parsed, DesktopRuntime.TryParseUpdateCommand(command, out var actual));
        Assert.Equal(version, actual);
    }

    [Fact]
    public void UpdateNoticeFollowsTrayCommands()
    {
        var notice = new TradeFoundry.Services.DesktopUpdateNotice();

        Assert.True(notice.Apply(DesktopRuntime.FormatUpdateCommand("1.3.0")));
        Assert.Equal("1.3.0", notice.AvailableVersion);
        Assert.False(notice.Apply("something else"));
        Assert.Equal("1.3.0", notice.AvailableVersion);
        Assert.True(notice.Apply(DesktopRuntime.FormatUpdateCommand(null)));
        Assert.Null(notice.AvailableVersion);
    }

    [Fact]
    public async Task EmbeddedServerStopsWhenParentClosesInput()
    {
        using var input = new StringReader("unrelated\n");

        await DesktopRuntime.WaitForShutdownSignalAsync(input).WaitAsync(TimeSpan.FromSeconds(5));
    }
}
