using System.ComponentModel;
using System.Windows;
using SpiffoCON.Core.Updates;
using SpiffoCON.Services;

namespace SpiffoCON;

/// <summary>
/// A newer version is out: what is new, and one click to download and install it (the installer of the same
/// kind as this copy), skip it, or decide later. A copy the installer didn't put there opens the releases page.
/// </summary>
public partial class UpdateWindow : Window
{
    public enum Choice { Later, Skip, Install, OpenedPage }

    readonly ReleaseAsset? _installer;
    CancellationTokenSource? _download;

    public Choice Result { get; private set; } = Choice.Later;

    /// <summary>The downloaded installer, when <see cref="Result"/> is <see cref="Choice.Install"/>.</summary>
    public string? InstallerPath { get; private set; }

    public UpdateWindow(ReleaseInfo release, ReleaseAsset? installer)
    {
        InitializeComponent();
        _installer = installer;
        TitleText.Text = $"SpiffoCON {release.Version} is available (you have {AppInstall.Version})";
        NotesBox.Text = UpdateCheck.NotesForDisplay(release.Notes);
        StatusText.Text = installer is not null
            ? $"The installer ({installer.Size / 1048576.0:0.0} MB) is downloaded from GitHub and checked; your servers and settings are kept, and SpiffoCON opens again when it is done."
            : "This copy was not installed by the installer: download the new version from the releases page.";
        InstallButton.Content = installer is not null ? "Install and restart" : "Open the download page";
        // it can appear by itself at start-up: Enter typed for something else must not install
        Loaded += (_, _) => LaterButton.Focus();
    }

    async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installer is null)
        {
            UpdateCheck.OpenReleasesPage();
            Result = Choice.OpenedPage;
            DialogResult = true;
            return;
        }
        _download = new CancellationTokenSource();
        InstallButton.IsEnabled = SkipButton.IsEnabled = false;
        LaterButton.Content = "Cancel";
        Progress.Visibility = Visibility.Visible;
        try
        {
            long size = _installer.Size;
            InstallerPath = await UpdateCheck.DownloadAsync(_installer, AppInstall.Version, new Progress<long>(bytes =>
            {
                Progress.Value = bytes / (double)Math.Max(1, size);
                StatusText.Text = $"Downloading {bytes / 1048576.0:0.0} of {size / 1048576.0:0.0} MB...";
            }), _download.Token);
            Result = Choice.Install;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Download cancelled.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText.Text = "Could not download the update: " + ex.Message;
        }
        finally
        {
            _download.Dispose();
            _download = null;
            if (IsLoaded && Result != Choice.Install)
            {
                InstallButton.IsEnabled = SkipButton.IsEnabled = true;
                LaterButton.Content = "Later";
                Progress.Visibility = Visibility.Collapsed;
            }
        }
    }

    void Skip_Click(object sender, RoutedEventArgs e)
    {
        Result = Choice.Skip;
        DialogResult = true;
    }

    void Later_Click(object sender, RoutedEventArgs e)
    {
        // "Cancel" while downloading
        if (_download is not null)
            _download.Cancel();
        else
            DialogResult = false;
    }

    // Esc is Later, or Cancel while downloading (IsCancel would close the window during the download)
    void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Later_Click(sender, e);
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // closed with the X while downloading
        _download?.Cancel();
        base.OnClosing(e);
    }
}
