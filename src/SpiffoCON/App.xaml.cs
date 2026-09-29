using System.Windows;
using System.Windows.Threading;

namespace SpiffoCON;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        RemoveOldTempCopies();
        base.OnStartup(e);
    }

    /// <summary>
    /// Database copies the Accounts tab reads are deleted right after; one left by a crash still
    /// holds the server's accounts (password hashes included), so it goes at the next start.
    /// </summary>
    static void RemoveOldTempCopies()
    {
        try
        {
            var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SpiffoCON");
            if (!System.IO.Directory.Exists(temp))
                return;
            foreach (var dir in System.IO.Directory.EnumerateDirectories(temp, "db-*"))
                if (DateTime.UtcNow - System.IO.Directory.GetCreationTimeUtc(dir) > TimeSpan.FromHours(1))
                    System.IO.Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
        }
    }

    static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "SpiffoCON", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
