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
    private MessageWindow? _messageWindow;
    private bool _openBrowserWhenReady;
    private bool _quitting;

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
            desktop.Exit += (_, _) => _server.Dispose();

            CreateTrayIcon();
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

        var quit = new NativeMenuItem("Quit TradeFoundry");
        quit.Click += async (_, _) => await QuitAsync();
        menu.Items.Add(quit);

        _trayIcon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "TradeFoundry",
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => OpenJournal();

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
        _trayIcon.ToolTipText = $"TradeFoundry: {status}";

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
        _messageWindow = new MessageWindow(message, details, _trayIcon?.Icon);
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

    private static WindowIcon LoadIcon()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream("TradeFoundry.ico")
            ?? throw new InvalidOperationException("The tray icon resource is missing.");
        return new WindowIcon(stream);
    }
}
