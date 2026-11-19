using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using TradeFoundry.Core;

namespace TradeFoundry.Desktop;

/// <summary>
/// The tray helper: a notification-area icon whose menu starts and stops the local server and
/// opens the journal in the default browser. There is no main window.
/// </summary>
internal sealed class App : Application
{
    private readonly TrayOptions? _options;
    private ServerController? _server;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _statusItem;
    private NativeMenuItem? _toggleServerItem;
    private NativeMenuItem? _startAtSignInItem;
    private NativeMenuItem? _updateItem;
    private MessageWindow? _messageWindow;
    private Updater? _updater;
    private DispatcherTimer? _updateTimer;
    private DesktopUpdateManifest? _availableUpdate;
    private UpdatePhase _updatePhase;
    private int _downloadPercent;
    private string? _updateStatus;
    private WindowIcon? _normalIcon;
    private WindowIcon? _updateIcon;
    private bool _openBrowserWhenReady;
    private bool _quitting;

    private enum UpdatePhase
    {
        Idle,
        Checking,
        Downloading,
        Installing
    }

    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);

    public App(TrayOptions? options)
    {
        _options = options;
    }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
        Name = "TradeFoundry";
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (_options is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _server = new ServerController(_options.Url);
            _server.StateChanged += () => Dispatcher.UIThread.Post(OnServerStateChanged);
            _updater = new Updater();
            desktop.Exit += (_, _) =>
            {
                _updateTimer?.Stop();
                _server.Dispose();
                _updater.Dispose();
            };

            // Setup relaunches the tray with --update-failed when a silent update did not finish.
            _updateStatus = _options.UpdateFailed ? "Update failed. Try again" : null;
            CreateTrayIcon();
            StartUpdateChecks();
            _options.Instance.StartListening(() => Dispatcher.UIThread.Post(OpenJournal));

            _openBrowserWhenReady = _options.OpenBrowserOnStart;
            _ = _server.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon()
    {
        var open = new NativeMenuItem("Open TradeFoundry");
        open.Click += (_, _) => OpenJournal();

        _statusItem = new NativeMenuItem("Starting…") { IsEnabled = false };

        _toggleServerItem = new NativeMenuItem("Stop server");
        _toggleServerItem.Click += (_, _) => ToggleServer();

        var openData = new NativeMenuItem("Open data folder");
        openData.Click += (_, _) => OpenDataFolder();

        var menu = new NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_statusItem);
        menu.Items.Add(_toggleServerItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        if (StartupRegistration.IsSupported)
        {
            _startAtSignInItem = new NativeMenuItem("Start at sign-in")
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = StartupRegistration.IsEnabled()
            };
            _startAtSignInItem.Click += (_, _) => ToggleStartAtSignIn();
            menu.Items.Add(_startAtSignInItem);
        }

        menu.Items.Add(openData);
        menu.Items.Add(new NativeMenuItemSeparator());

        _updateItem = new NativeMenuItem("Check for updates");
        _updateItem.Click += (_, _) => OnUpdateItemClicked();
        menu.Items.Add(_updateItem);
        menu.Items.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem("Quit TradeFoundry");
        quit.Click += async (_, _) => await QuitAsync();
        menu.Items.Add(quit);

        _trayIcon = new TrayIcon
        {
            Icon = _normalIcon = LoadIcon("TradeFoundry.ico"),
            ToolTipText = "TradeFoundry",
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => OpenJournal();

        _updateIcon = LoadIcon("TradeFoundry-update.ico");
        TrayIcon.SetIcons(this, [_trayIcon]);
        OnServerStateChanged();
    }

    private void OnServerStateChanged()
    {
        if (_server is null || _statusItem is null || _toggleServerItem is null || _trayIcon is null)
        {
            return;
        }

        var state = _server.State;
        var (status, toggle, canToggle) = state switch
        {
            ServerState.Starting => ("Server starting…", "Stop server", false),
            ServerState.Running => ($"Running at {_server.Url}", "Stop server", true),
            ServerState.External => ($"Running at {_server.Url} (started elsewhere)", "Stop server", false),
            ServerState.Stopping => ("Server stopping…", "Start server", false),
            ServerState.Failed => ("Server failed to start", "Start server", true),
            _ => ("Server stopped", "Start server", true)
        };

        _statusItem.Header = status;
        _toggleServerItem.Header = toggle;
        _toggleServerItem.IsEnabled = canToggle && !_quitting;
        RefreshToolTip(status);

        if (state is ServerState.Running or ServerState.External && _openBrowserWhenReady)
        {
            _openBrowserWhenReady = false;
            DesktopRuntime.TryOpenBrowser(_server.Url);
        }
        else if (state == ServerState.Failed)
        {
            _openBrowserWhenReady = false;
            ShowMessage(_server.FailureMessage ?? "The TradeFoundry server stopped.", _server.RecentOutput);
        }
    }

    private void RefreshToolTip(string? status = null)
    {
        if (_trayIcon is null)
        {
            return;
        }

        status ??= _statusItem?.Header ?? string.Empty;
        // A status such as "Update failed" wins over "Update X available", so failures show on hover.
        var updateLine = _updateStatus ?? (_availableUpdate is { } update ? $"Update {update.Version} available" : null);
        _trayIcon.ToolTipText = updateLine is null
            ? $"TradeFoundry: {status}"
            : $"TradeFoundry: {status}\n{updateLine}";
    }

    private void StartUpdateChecks()
    {
        if (_updater is null || _updateItem is null)
        {
            return;
        }

        if (!_updater.IsEnabled)
        {
            _updateItem.Header = DesktopUpdates.IsDevelopmentVersion(_updater.CurrentVersion)
                ? $"Version {_updater.CurrentVersion} (development build)"
                : $"Version {_updater.CurrentVersion}";
            _updateItem.IsEnabled = false;
            return;
        }

        RefreshUpdateItem();
        // After a failed update, look up the release again promptly so retrying is one click.
        var firstCheckDelay = _options?.UpdateFailed == true ? TimeSpan.FromSeconds(3) : FirstUpdateCheckDelay;
        DispatcherTimer.RunOnce(() => _ = CheckForUpdatesAsync(userInitiated: false), firstCheckDelay);
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(userInitiated: false);
        _updateTimer.Start();
    }

    private void OnUpdateItemClicked()
    {
        if (_availableUpdate is not null)
        {
            _ = InstallUpdateAsync();
        }
        else
        {
            _ = CheckForUpdatesAsync(userInitiated: true);
        }
    }

    // Update notices never open windows: the tray icon gains a badge, the menu item changes,
    // and the server shows a notification inside the app.
    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updater is null || !_updater.IsEnabled || _updatePhase != UpdatePhase.Idle || _quitting)
        {
            return;
        }

        _updatePhase = UpdatePhase.Checking;
        if (userInitiated)
        {
            // Automatic checks keep a "last update failed" status until the user acts on it.
            _updateStatus = null;
        }

        RefreshUpdateItem();
        try
        {
            _availableUpdate = await _updater.CheckAsync(CancellationToken.None);
            if (_availableUpdate is null && userInitiated)
            {
                _updateStatus = $"Up to date (version {_updater.CurrentVersion})";
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or FormatException or IOException)
        {
            // Keep any update found earlier; only report the failure when the user asked.
            if (userInitiated)
            {
                _updateStatus = "Update check failed. Try again";
            }
        }
        finally
        {
            _updatePhase = UpdatePhase.Idle;
            ApplyUpdateState();
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_updater is null || _server is null || _availableUpdate is not { } update || _updatePhase != UpdatePhase.Idle || _quitting)
        {
            return;
        }

        _updatePhase = UpdatePhase.Downloading;
        _updateStatus = null;
        _downloadPercent = 0;
        RefreshUpdateItem();

        string installerPath;
        try
        {
            installerPath = await _updater.DownloadAsync(
                update,
                new Progress<int>(percent =>
                {
                    _downloadPercent = percent;
                    RefreshUpdateItem();
                }),
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _updatePhase = UpdatePhase.Idle;
            _updateStatus = exception is InvalidDataException
                ? $"Update {update.Version} failed verification. Try again"
                : $"Update {update.Version} download failed. Try again";
            RefreshUpdateItem();
            return;
        }

        _updatePhase = UpdatePhase.Installing;
        _quitting = true;
        RefreshUpdateItem();
        OnServerStateChanged();

        // Stop the server cleanly so the database is closed before files are replaced.
        await _server.StopAsync();

        try
        {
            Updater.LaunchInstaller(installerPath, Environment.ProcessPath);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _quitting = false;
            _updatePhase = UpdatePhase.Idle;
            _updateStatus = $"Update {update.Version} could not start. Try again";
            RefreshUpdateItem();
            _ = _server.StartAsync();
            return;
        }

        // Setup waits for this process to exit, upgrades in place, and starts the tray again.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>Reflects the pending update in the tray icon, tooltip, menu, and the app.</summary>
    private void ApplyUpdateState()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Icon = _availableUpdate is null ? _normalIcon : _updateIcon;
        }

        _server?.SetAvailableUpdate(_availableUpdate?.Version);
        RefreshUpdateItem();
        RefreshToolTip();
    }

    private void RefreshUpdateItem()
    {
        if (_updateItem is null || _updater is null || !_updater.IsEnabled)
        {
            return;
        }

        _updateItem.Header = _updatePhase switch
        {
            UpdatePhase.Checking => "Checking for updates…",
            UpdatePhase.Downloading => $"Downloading update… {_downloadPercent}%",
            UpdatePhase.Installing => "Installing update…",
            _ when _updateStatus is not null => $"{_updateStatus}…",
            _ => _availableUpdate is { } update
                ? $"Install update {update.Version} and restart"
                : $"Check for updates (version {_updater.CurrentVersion})"
        };
        _updateItem.IsEnabled = _updatePhase == UpdatePhase.Idle && !_quitting;
        RefreshToolTip();
    }

    private void OpenJournal()
    {
        if (_server is null || _quitting)
        {
            return;
        }

        switch (_server.State)
        {
            case ServerState.Running or ServerState.External:
                DesktopRuntime.TryOpenBrowser(_server.Url);
                break;
            case ServerState.Starting:
                _openBrowserWhenReady = true;
                break;
            case ServerState.Stopped or ServerState.Failed:
                _openBrowserWhenReady = true;
                _ = _server.StartAsync();
                break;
        }
    }

    private void ToggleServer()
    {
        if (_server is null)
        {
            return;
        }

        if (_server.State == ServerState.Running)
        {
            _ = _server.StopAsync();
        }
        else if (_server.State is ServerState.Stopped or ServerState.Failed)
        {
            _ = _server.StartAsync();
        }
    }

    private void ToggleStartAtSignIn()
    {
        if (_startAtSignInItem is null)
        {
            return;
        }

        var enable = !StartupRegistration.IsEnabled();
        try
        {
            StartupRegistration.SetEnabled(enable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            ShowMessage($"TradeFoundry could not change the start-at-sign-in setting: {exception.Message}", null);
        }

        _startAtSignInItem.IsChecked = StartupRegistration.IsEnabled();
    }

    private void OpenDataFolder()
    {
        var directory = ResolveDataDirectory();
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowMessage($"The data folder '{directory}' could not be opened: {exception.Message}", null);
            return;
        }

        DesktopRuntime.TryOpenBrowser(directory);
    }

    private async Task QuitAsync()
    {
        if (_quitting || _server is null)
        {
            return;
        }

        _quitting = true;
        if (_trayIcon is not null)
        {
            _trayIcon.ToolTipText = "TradeFoundry: stopping…";
        }

        await _server.StopAsync();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void ShowMessage(string message, string? details)
    {
        _messageWindow?.Close();
        _messageWindow = new MessageWindow(message, details, _normalIcon);
        _messageWindow.Closed += (sender, _) =>
        {
            if (ReferenceEquals(sender, _messageWindow))
            {
                _messageWindow = null;
            }
        };
        _messageWindow.Show();
        _messageWindow.Activate();
    }

    /// <summary>
    /// Mirrors the server's desktop configuration order closely enough to find the journal:
    /// environment variable, then the per-user configuration file, then the default location.
    /// </summary>
    private static string ResolveDataDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("Storage__DataDirectory");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        try
        {
            if (File.Exists(DesktopRuntime.UserConfigurationPath))
            {
                using var stream = File.OpenRead(DesktopRuntime.UserConfigurationPath);
                using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (document.RootElement.TryGetProperty("Storage", out var storage) &&
                    storage.TryGetProperty("DataDirectory", out var dataDirectory) &&
                    dataDirectory.GetString() is { Length: > 0 } configured)
                {
                    return Path.GetFullPath(configured);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
        }

        return DesktopRuntime.DefaultDataDirectory;
    }

    private static WindowIcon LoadIcon(string resourceName)
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("The tray icon resource is missing.");
        return new WindowIcon(stream);
    }
}
