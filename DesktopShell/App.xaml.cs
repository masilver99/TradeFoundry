using System.Threading;
using System.Windows;
using TradeFoundry.Core;

namespace TradeFoundry.Desktop;

public partial class App : Application
{
    private Mutex? _shellMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _shellMutex = new Mutex(initiallyOwned: true, DesktopRuntime.ShellMutexName, out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shellMutex?.Dispose();
        base.OnExit(e);
    }
}
