using System.Windows;
using System.Windows.Threading;

namespace SpiffoCON;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "SpiffoCON", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
