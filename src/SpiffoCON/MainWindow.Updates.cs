using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using SpiffoCON.Core.Updates;
using SpiffoCON.Services;

namespace SpiffoCON;

public partial class MainWindow
{
    /// <summary>The installer is running: it closes SpiffoCON when it installs, without the usual questions.</summary>
    bool _updating;

    bool _checkingUpdates;

    /// <summary>
    /// The automatic check runs at most once a day (quiet unless there is a new version the user has not
    /// skipped); the Maintenance tab's Check now runs it on request. Only the version is sent to GitHub.
    /// </summary>
    async Task CheckForUpdatesAsync(bool interactive)
    {
        var book = _vm.Book;
        // by itself only in a copy the installer put there (a build or a copied folder has no installer to run)
        if (_checkingUpdates || (!interactive && (AppInstall.Kind == InstallKind.Other || !book.CheckForUpdates
                || DateTime.UtcNow - book.LastUpdateCheck < TimeSpan.FromDays(1))))
            return;
        _checkingUpdates = true;
        try
        {
            if (interactive)
                _vm.UpdateStatus = "Looking for a new version...";
            var current = AppInstall.Version;
            ReleaseInfo? release = null;
            try
            {
                release = await UpdateCheck.LatestAsync(current);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // offline
            }
            bool newer = release is not null && release.Version > current;
            var installer = release is null ? null : UpdateCheck.InstallerFor(release, AppInstall.Kind, AppInstall.SelfContained);
            if (!interactive)
            {
                // not over a question or another dialog: at the next start instead
                if (release is null || ComponentDispatcher.IsThreadModal || _closing)
                    return;
                // just published: its installers may still be uploading; still missing hours later, the page is offered
                if (newer && installer is null && AppInstall.Kind != InstallKind.Other
                    && release.Published is { } published && DateTime.UtcNow - published < TimeSpan.FromHours(6))
                    return;
                book.LastUpdateCheck = DateTime.UtcNow;
                _vm.SaveProfile();
                if (!newer || release.Version.ToString() == book.SkippedVersion)
                    return;
            }
            _vm.UpdateStatus = release is null
                ? "Could not reach GitHub (no internet connection?): " + UpdateCheck.ReleasesPage
                : newer ? $"SpiffoCON {release.Version} is available." : $"You have the latest version ({current}).";
            if (newer)
                await OfferUpdateAsync(release!, installer);
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    /// <summary>What is new, and a one-click update: the installer is downloaded, run quietly, and opens SpiffoCON again.</summary>
    async Task OfferUpdateAsync(ReleaseInfo release, ReleaseAsset? installer)
    {
        var dialog = new UpdateWindow(release, installer) { Owner = this };
        dialog.ShowDialog();
        switch (dialog.Result)
        {
            case UpdateWindow.Choice.Skip:
                // not offered again by the daily check (Check now still finds it)
                _vm.Book.SkippedVersion = release.Version.ToString();
                _vm.SaveProfile();
                _vm.UpdateStatus = $"SpiffoCON {release.Version} skipped.";
                return;
            case UpdateWindow.Choice.Install when dialog.InstallerPath is { } path:
                // a restart countdown, a copy of the mods...: the user says whether they can go
                if (!_vm.ConfirmLeaving("Install the update"))
                    return;
                _vm.SaveProfile();
                try
                {
                    // the installer says when it installs, and SpiffoCON closes then (by itself: closed by Restart
                    // Manager instead, WPF crashes on its way out); while the installer waits for an administrator
                    // prompt SpiffoCON stays open, so a declined prompt can be told. It is opened again afterwards.
                    using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateCheck.ReadyEventName(Environment.ProcessId));
                    using var setup = UpdateCheck.StartInstaller(path, AppInstall.Kind, Environment.ProcessId);
                    _updating = true;
                    _vm.UpdateStatus = "Installing the update: SpiffoCON closes and opens again when it is done...";
                    var exited = setup.WaitForExitAsync();
                    if (await Task.WhenAny(exited, SignaledAsync(ready)) != exited)
                    {
                        Close();
                        return;
                    }
                    ready.Set(); // ends the wait
                    _updating = false;
                    if (_closing)
                        return;
                    // still here: nothing was installed
                    _vm.UpdateStatus = setup.ExitCode == 0 ? "The update was installed: restart SpiffoCON to use it." : "The update was not installed.";
                    MessageBox.Show(this, setup.ExitCode == 0
                            ? "The update was installed: restart SpiffoCON to use it."
                            : $"The update was not installed (the administrator prompt was declined, or the installer stopped; code {setup.ExitCode}).\n\n"
                              + $"You can run it yourself: \"{path}\".",
                        "SpiffoCON", MessageBoxButton.OK, setup.ExitCode == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    _updating = false;
                    MessageBox.Show(this, $"Could not start the installer:\n{ex.Message}\n\nIt was saved as \"{path}\".",
                        "SpiffoCON", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                return;
        }
    }

    static Task SignaledAsync(WaitHandle handle)
    {
        var signaled = new TaskCompletionSource();
        var wait = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => signaled.TrySetResult(), null, Timeout.Infinite, executeOnlyOnce: true);
        signaled.Task.ContinueWith(_ => wait.Unregister(null), TaskScheduler.Default);
        return signaled.Task;
    }
}
