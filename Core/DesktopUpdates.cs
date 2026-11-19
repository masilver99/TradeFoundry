using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeFoundry.Core;

/// <summary>
/// The release manifest (<c>latest.json</c>) that CI publishes next to each stable Windows
/// installer, and that the tray helper reads to offer updates.
/// </summary>
public sealed record DesktopUpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("installerUrl")] string InstallerUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset? PublishedAt = null);

public static class DesktopUpdates
{
    /// <summary>Overrides the build-time manifest URL (development and testing).</summary>
    public const string ManifestUrlEnvironmentVariable = "TradeFoundryUpdateManifestUrl";

    /// <summary>Assembly metadata key that carries the manifest URL baked in by CI.</summary>
    public const string ManifestUrlMetadataKey = "TradeFoundryUpdateManifestUrl";

    /// <summary>Custom installer switch telling Setup it was started by the tray's updater.</summary>
    public const string InstallerUpdateSwitch = "/TFUPDATE=1";

    /// <summary>
    /// Parses and validates a manifest. The installer must be served over HTTPS (or plain HTTP
    /// on loopback, for testing) from the same host as the manifest, and must name a SHA-256.
    /// </summary>
    public static DesktopUpdateManifest Parse(string json, Uri manifestUrl)
    {
        DesktopUpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<DesktopUpdateManifest>(json);
        }
        catch (JsonException exception)
        {
            throw new FormatException("The update manifest is not valid JSON.", exception);
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version) || !TryParseVersion(manifest.Version, out _))
        {
            throw new FormatException("The update manifest has no valid version.");
        }

        if (!Uri.TryCreate(manifest.InstallerUrl, UriKind.Absolute, out var installerUrl) ||
            !string.Equals(installerUrl.Host, manifestUrl.Host, StringComparison.OrdinalIgnoreCase) ||
            !(installerUrl.Scheme == Uri.UriSchemeHttps || (installerUrl.Scheme == Uri.UriSchemeHttp && installerUrl.IsLoopback)))
        {
            throw new FormatException("The update manifest's installer URL must be HTTPS on the same host as the manifest.");
        }

        if (manifest.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit))
        {
            throw new FormatException("The update manifest has no valid SHA-256.");
        }

        if (manifest.Size <= 0)
        {
            throw new FormatException("The update manifest has no valid installer size.");
        }

        return manifest with { Sha256 = hash.ToLowerInvariant() };
    }

    /// <summary>
    /// Normalizes an informational version such as <c>1.2.3-rc.1+abc123</c> to
    /// <c>1.2.3-rc.1</c> by dropping build metadata.
    /// </summary>
    public static string NormalizeVersion(string version)
    {
        var trimmed = version.Trim();
        var plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }

    /// <summary>Development builds (0.0.0-*) are never offered updates unless testing.</summary>
    public static bool IsDevelopmentVersion(string version) =>
        TryParseVersion(version, out var parsed) && parsed.Core == new Version(0, 0, 0);

    /// <summary>True when <paramref name="candidate"/> is a newer release than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current) =>
        TryParseVersion(candidate, out var a) && TryParseVersion(current, out var b) && Compare(a, b) > 0;

    private static int Compare(ParsedVersion a, ParsedVersion b)
    {
        var core = a.Core.CompareTo(b.Core);
        if (core != 0)
        {
            return core;
        }

        // A release sorts after its prereleases (1.2.0-rc.1 < 1.2.0).
        if (a.Prerelease.Length == 0 && b.Prerelease.Length == 0)
        {
            return 0;
        }

        if (a.Prerelease.Length == 0)
        {
            return 1;
        }

        if (b.Prerelease.Length == 0)
        {
            return -1;
        }

        var left = a.Prerelease.Split('.');
        var right = b.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = int.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            var result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i])
            };

            if (result != 0)
            {
                return result;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static bool TryParseVersion(string version, out ParsedVersion parsed)
    {
        parsed = default;
        var normalized = NormalizeVersion(version);
        var dash = normalized.IndexOf('-');
        var core = dash >= 0 ? normalized[..dash] : normalized;
        var prerelease = dash >= 0 ? normalized[(dash + 1)..] : string.Empty;

        var parts = core.Split('.');
        if (parts.Length != 3 || (dash >= 0 && prerelease.Length == 0))
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        parsed = new ParsedVersion(new Version(numbers[0], numbers[1], numbers[2]), prerelease);
        return true;
    }

    private readonly record struct ParsedVersion(Version Core, string Prerelease);
}
