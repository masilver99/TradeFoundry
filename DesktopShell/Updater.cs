using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using TradeFoundry.Core;

namespace TradeFoundry.Desktop;

/// <summary>
/// Checks the published <c>latest.json</c>, downloads and verifies the Windows installer, and
/// starts it in silent update mode. Only Windows installs built by CI (which bakes in the
/// manifest URL) are updatable; development builds report updates as unavailable.
/// </summary>
internal sealed class Updater : IDisposable
{
    private readonly HttpClient _httpClient;

    public Updater()
    {
        CurrentVersion = DesktopUpdates.NormalizeVersion(
            typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(Updater).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0");

        var overrideUrl = Environment.GetEnvironmentVariable(DesktopUpdates.ManifestUrlEnvironmentVariable);
        var builtInUrl = typeof(Updater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == DesktopUpdates.ManifestUrlMetadataKey)?.Value;
        var configured = string.IsNullOrWhiteSpace(overrideUrl) ? builtInUrl : overrideUrl;

        if (Uri.TryCreate(configured, UriKind.Absolute, out var manifestUrl) &&
            (manifestUrl.Scheme == Uri.UriSchemeHttps || (manifestUrl.Scheme == Uri.UriSchemeHttp && manifestUrl.IsLoopback)))
        {
            ManifestUrl = manifestUrl;
        }

        IsEnabled = OperatingSystem.IsWindows() &&
                    ManifestUrl is not null &&
                    (!DesktopUpdates.IsDevelopmentVersion(CurrentVersion) || !string.IsNullOrWhiteSpace(overrideUrl));

        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"TradeFoundry-Desktop/{CurrentVersion.Replace('+', '.')}");
    }

    public string CurrentVersion { get; }

    public Uri? ManifestUrl { get; }

    public bool IsEnabled { get; }

    /// <summary>Returns the newer release, or null when this install is up to date.</summary>
    public async Task<DesktopUpdateManifest?> CheckAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled || ManifestUrl is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        // Bypass intermediate caches so a new release is seen promptly.
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await _httpClient.SendAsync(request, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Nothing published yet (or the manifest is being replaced).
            return null;
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(timeout.Token);
        var manifest = DesktopUpdates.Parse(json, ManifestUrl);
        return DesktopUpdates.IsNewer(manifest.Version, CurrentVersion) ? manifest : null;
    }

    /// <summary>Downloads the installer, verifying its size and SHA-256 while streaming.</summary>
    public async Task<string> DownloadAsync(DesktopUpdateManifest manifest, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "TradeFoundry-Update");
        if (Directory.Exists(directory))
        {
            foreach (var stale in Directory.EnumerateFiles(directory))
            {
                TryDelete(stale);
            }
        }

        Directory.CreateDirectory(directory);
        var safeVersion = string.Concat(manifest.Version.Where(character => char.IsLetterOrDigit(character) || character is '.' or '-'));
        var path = Path.Combine(directory, $"TradeFoundry-Setup-{safeVersion}.exe");
        var partial = path + ".partial";

        try
        {
            using var response = await _httpClient.GetAsync(manifest.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long received = 0;
                var lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                    if (received > manifest.Size)
                    {
                        throw new InvalidDataException("The downloaded update is larger than the release manifest says.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

                    var percent = (int)(received * 100 / manifest.Size);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }

                if (received != manifest.Size)
                {
                    throw new InvalidDataException("The update download was incomplete.");
                }

                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(actual, manifest.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The downloaded update failed its integrity check (SHA-256 mismatch).");
                }
            }

            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>
    /// Starts the installer in fully silent update mode (no window). Setup waits for the tray
    /// and server to exit, upgrades in place (keeping the data directory), and relaunches the
    /// tray. If the update does not finish, Setup relaunches the tray from
    /// <paramref name="trayPath"/> with <c>--update-failed</c> so the menu can report it.
    /// </summary>
    public static void LaunchInstaller(string installerPath, string? trayPath)
    {
        var log = Path.Combine(Path.GetDirectoryName(installerPath)!, "install.log");
        var startInfo = new ProcessStartInfo(installerPath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installerPath)!
        };
        startInfo.ArgumentList.Add("/VERYSILENT");
        startInfo.ArgumentList.Add("/SUPPRESSMSGBOXES");
        startInfo.ArgumentList.Add("/NORESTART");
        startInfo.ArgumentList.Add("/CLOSEAPPLICATIONS");
        startInfo.ArgumentList.Add(DesktopUpdates.InstallerUpdateSwitch);
        startInfo.ArgumentList.Add($"/LOG={log}");
        if (!string.IsNullOrEmpty(trayPath))
        {
            startInfo.ArgumentList.Add($"/TFTRAY={trayPath}");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The update installer could not be started.");
    }

    public void Dispose() => _httpClient.Dispose();

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
