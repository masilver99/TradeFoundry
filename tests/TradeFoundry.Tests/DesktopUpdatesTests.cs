using TradeFoundry.Core;
using Xunit;

namespace TradeFoundry.Tests;

public sealed class DesktopUpdatesTests
{
    private static readonly Uri ManifestUrl = new("https://git.example.test/api/packages/owner/generic/tradefoundry-windows/latest/latest.json");
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("1.2.1", "1.2.0")]
    [InlineData("1.10.0", "1.9.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.2.0", "1.2.0-rc.1")]
    [InlineData("1.2.0-rc.2", "1.2.0-rc.1")]
    [InlineData("1.2.0-rc.10", "1.2.0-rc.9")]
    [InlineData("1.2.0-rc.1", "1.2.0-beta.3")]
    [InlineData("0.1.0", "0.0.0-dev.abc123")]
    [InlineData("1.2.1", "1.2.0+0123abcd")]
    public void NewerVersionsAreDetected(string candidate, string current)
    {
        Assert.True(DesktopUpdates.IsNewer(candidate, current));
        Assert.False(DesktopUpdates.IsNewer(current, candidate));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0")]
    [InlineData("1.2.0", "1.2.0+abc")]
    [InlineData("not-a-version", "1.0.0")]
    [InlineData("1.2", "1.0.0")]
    public void SameOrInvalidVersionsAreNotNewer(string candidate, string current)
    {
        Assert.False(DesktopUpdates.IsNewer(candidate, current));
    }

    [Fact]
    public void DevelopmentBuildsAreRecognized()
    {
        Assert.True(DesktopUpdates.IsDevelopmentVersion("0.0.0-dev"));
        Assert.True(DesktopUpdates.IsDevelopmentVersion("0.0.0-dev.0123456789ab+0123456789abcdef"));
        Assert.False(DesktopUpdates.IsDevelopmentVersion("0.1.0"));
        Assert.Equal("1.2.0-rc.1", DesktopUpdates.NormalizeVersion(" 1.2.0-rc.1+abcdef "));
    }

    [Fact]
    public void ValidManifestIsParsedAndHashNormalized()
    {
        var manifest = DesktopUpdates.Parse(Manifest(sha256: Hash.ToUpperInvariant()), ManifestUrl);

        Assert.Equal("1.3.0", manifest.Version);
        Assert.Equal(Hash, manifest.Sha256);
        Assert.Equal(75_000_000, manifest.Size);
    }

    [Theory]
    [InlineData("http://git.example.test/setup.exe")]
    [InlineData("https://elsewhere.example.test/setup.exe")]
    [InlineData("file:///C:/setup.exe")]
    [InlineData("setup.exe")]
    public void InstallerMustComeFromTheManifestHostOverHttps(string installerUrl)
    {
        Assert.Throws<FormatException>(() => DesktopUpdates.Parse(Manifest(installerUrl: installerUrl), ManifestUrl));
    }

    [Fact]
    public void LoopbackHttpIsAllowedForLocalTesting()
    {
        var manifest = DesktopUpdates.Parse(
            Manifest(installerUrl: "http://127.0.0.1:5300/setup.exe"),
            new Uri("http://127.0.0.1:5300/latest.json"));

        Assert.Equal("http://127.0.0.1:5300/setup.exe", manifest.InstallerUrl);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("""{"version":"1.3","installerUrl":"https://git.example.test/a.exe","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","size":1}""")]
    [InlineData("""{"version":"1.3.0","installerUrl":"https://git.example.test/a.exe","sha256":"abc","size":1}""")]
    [InlineData("""{"version":"1.3.0","installerUrl":"https://git.example.test/a.exe","sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","size":0}""")]
    public void IncompleteOrInvalidManifestsAreRejected(string json)
    {
        Assert.Throws<FormatException>(() => DesktopUpdates.Parse(json, ManifestUrl));
    }

    private static string Manifest(
        string version = "1.3.0",
        string installerUrl = "https://git.example.test/api/packages/owner/generic/tradefoundry-windows/1.3.0/TradeFoundry-Setup-1.3.0.exe",
        string sha256 = Hash,
        long size = 75_000_000) =>
        $$"""
        {
          "version": "{{version}}",
          "installerUrl": "{{installerUrl}}",
          "sha256": "{{sha256}}",
          "size": {{size}},
          "publishedAt": "2026-11-18T12:00:00Z"
        }
        """;
}
