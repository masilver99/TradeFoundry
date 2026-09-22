using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using TradeFoundry.Core;

namespace TradeFoundry.Desktop;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(1)
    };
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _serverProcess;
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        KeepWindowInsideWorkArea();

        try
        {
            await InitializeBrowserAsync(_lifetime.Token);
            await EnsureServerAsync(_lifetime.Token);

            Browser.Visibility = Visibility.Visible;
            LoadingPanel.Visibility = Visibility.Collapsed;
            Browser.CoreWebView2.Navigate(DesktopRuntime.DefaultUrl);
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (Exception exception)
        {
            if (_closing)
            {
                return;
            }

            var message = exception is FileNotFoundException
                ? exception.Message
                : $"TradeFoundry could not start.\n\n{exception.Message}\n\nThe Microsoft Edge WebView2 Runtime is required for the desktop shell.";

            MessageBox.Show(this, message, "TradeFoundry", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private void KeepWindowInsideWorkArea()
    {
        const double margin = 8;
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, workArea.Width - (margin * 2));
        var availableHeight = Math.Max(1, workArea.Height - (margin * 2));

        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        MaxWidth = availableWidth;
        MaxHeight = availableHeight;
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);

        var minimumLeft = workArea.Left + margin;
        var maximumLeft = Math.Max(minimumLeft, workArea.Right - Width - margin);
        var minimumTop = workArea.Top + margin;
        var maximumTop = Math.Max(minimumTop, workArea.Bottom - Height - margin);

        Left = Math.Clamp(Left, minimumLeft, maximumLeft);
        Top = Math.Clamp(Top, minimumTop, maximumTop);
    }

    private async Task InitializeBrowserAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configuredUserDataFolder = Environment.GetEnvironmentVariable("TradeFoundryWebView2DataDirectory");
        var userDataFolder = string.IsNullOrWhiteSpace(configuredUserDataFolder)
            ? Path.Combine(DesktopRuntime.UserConfigurationDirectory, "WebView2")
            : Path.GetFullPath(configuredUserDataFolder);
        Directory.CreateDirectory(userDataFolder);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await Browser.EnsureCoreWebView2Async(environment);

        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Browser.CoreWebView2.NavigationStarting += Browser_NavigationStarting;
        Browser.CoreWebView2.NewWindowRequested += Browser_NewWindowRequested;
        Browser.CoreWebView2.DocumentTitleChanged += Browser_DocumentTitleChanged;
    }

    private async Task EnsureServerAsync(CancellationToken cancellationToken)
    {
        if (await IsServerReadyAsync(cancellationToken))
        {
            return;
        }

        var serverPath = ResolveServerPath();
        if (!File.Exists(serverPath))
        {
            throw new FileNotFoundException(
                $"The TradeFoundry server was not found at '{serverPath}'. Set the TradeFoundryServerPath environment variable when launching the shell from a development checkout.",
                serverPath);
        }

        _serverProcess = Process.Start(new ProcessStartInfo
        {
            FileName = serverPath,
            Arguments = $"{DesktopRuntime.DesktopSwitch} {DesktopRuntime.EmbeddedSwitch}",
            WorkingDirectory = Path.GetDirectoryName(serverPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        if (_serverProcess is null)
        {
            throw new InvalidOperationException("The TradeFoundry server process could not be started.");
        }

        try
        {
            await WaitForServerAsync(cancellationToken);
        }
        catch
        {
            StopServer();
            throw;
        }
    }

    private async Task WaitForServerAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            while (!await IsServerReadyAsync(timeout.Token))
            {
                if (_serverProcess?.HasExited == true)
                {
                    throw new InvalidOperationException("The TradeFoundry server stopped before it became ready.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("TradeFoundry did not become ready within 20 seconds.");
        }
    }

    private async Task<bool> IsServerReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                DesktopRuntime.DefaultUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return (int)response.StatusCode < 500;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static string ResolveServerPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("TradeFoundryServerPath");
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "TradeFoundry.exe")
            : Path.GetFullPath(configuredPath);
    }

    private void Browser_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsLocalNavigation(e.Uri))
        {
            return;
        }

        e.Cancel = true;
        DesktopRuntime.TryOpenBrowser(e.Uri);
    }

    private void Browser_NewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (IsLocalNavigation(e.Uri))
        {
            Browser.CoreWebView2.Navigate(e.Uri);
        }
        else
        {
            DesktopRuntime.TryOpenBrowser(e.Uri);
        }
    }

    private void Browser_DocumentTitleChanged(object? sender, object e)
    {
        var documentTitle = Browser.CoreWebView2.DocumentTitle;
        Title = string.IsNullOrWhiteSpace(documentTitle) ||
                string.Equals(documentTitle, "TradeFoundry", StringComparison.OrdinalIgnoreCase)
            ? "TradeFoundry"
            : $"{documentTitle} - TradeFoundry";
    }

    private static bool IsLocalNavigation(string? address)
    {
        if (string.Equals(address, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
               string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
               uri.Port == 5080;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closing = true;
        _lifetime.Cancel();
        _httpClient.Dispose();
        StopServer();
        _lifetime.Dispose();
    }

    private void StopServer()
    {
        if (_serverProcess is null)
        {
            return;
        }

        try
        {
            if (!_serverProcess.HasExited)
            {
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            _serverProcess.Dispose();
            _serverProcess = null;
        }
    }
}
