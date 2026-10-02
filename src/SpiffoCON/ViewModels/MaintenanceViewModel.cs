using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Commands;
using SpiffoCON.Core.Files;
using SpiffoCON.Core.Steam;

namespace SpiffoCON.ViewModels;

public sealed record ModUpdateRow(ModUpdateStatus Status)
{
    public string Title => Status.Title;
    public string WorkshopId => Status.WorkshopId;
    public string OnServer => Status.OnServer?.LocalDateTime.ToString("dd/MM/yy HH:mm") ?? "—";
    public string OnSteam => Status.OnSteam?.LocalDateTime.ToString("dd/MM/yy HH:mm") ?? "—";
    public bool NeedsAttention => Status.State is ModUpdateState.UpdateAvailable or ModUpdateState.NotOnServer;

    public string StateText => Status.State switch
    {
        ModUpdateState.UpdateAvailable => "Update on Steam",
        ModUpdateState.NotOnServer => "Not installed yet",
        ModUpdateState.Unknown => "Not public",
        _ => "Up to date",
    };
}

/// <summary>Mod updates (the server's copies against Steam), restarts with a countdown, notifications.</summary>
public sealed partial class MaintenanceViewModel : ObservableObject
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    readonly MainViewModel _main;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _waitBack = new() { Interval = TimeSpan.FromSeconds(15) };
    /// <summary>Restart time on a monotonic clock (Environment.TickCount64, ms): a clock change can't move it.</summary>
    long _restartAtTicks;

    /// <summary>When the countdown last ran; a long gap means the PC slept.</summary>
    long _lastTickTicks;
    Queue<int> _warnings = new();

    public MaintenanceViewModel(MainViewModel main)
    {
        _main = main;
        _tick.Tick += async (_, _) => await TickAsync();
        _waitBack.Tick += async (_, _) => await WaitBackTickAsync();
        _main.ServerBack += (_, _) =>
        {
            _waitBack.Stop();
            RestartStatus = $"The server is back ({DateTime.Now:HH:mm}).";
        };
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSessionActive) && !_main.IsSessionActive)
            {
                _waitBack.Stop();
                if (IsCountingDown)
                {
                    StopCountdown();
                    RestartStatus = "Restart called off: disconnected.";
                }
            }
        };
    }

    // ---- mod updates ----

    public ObservableCollection<ModUpdateRow> Mods { get; } = [];

    [ObservableProperty] private string updatesText = "Compares the mods the server has installed with Steam. Needs the RCON connection and SFTP.";
    [ObservableProperty] private bool isChecking;
    [ObservableProperty] private bool updatesFound;

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (!_main.IsSessionActive)
        {
            UpdatesText = "Connect first: the mod list comes from the server's options.";
            return;
        }
        if (_main.CurrentSftpSettings() is not { } sftp)
        {
            UpdatesText = "Needs SFTP (left panel): the server's copies are read from its workshop manifest.";
            return;
        }

        IsChecking = true;
        UpdatesFound = false;
        try
        {
            UpdatesText = "Reading the server's mod list...";
            var reply = await _main.RunAsync("showoptions", logReply: false);
            if (reply is null)
            {
                UpdatesText = "showoptions failed: see the console.";
                return;
            }
            var ids = ServerOptions.Parse(reply).WorkshopItems;
            if (ids.Count == 0)
            {
                UpdatesText = "The server has no workshop mods (WorkshopItems is empty).";
                Mods.Clear();
                return;
            }

            var progress = new Progress<string>(s => UpdatesText = s);
            var folder = await _main.FindWorkshopFolderAsync(progress);
            sftp = _main.CurrentSftpSettings() ?? sftp; // with the key the probe saw
            if (folder is null)
            {
                UpdatesText = "No workshop folder found over SFTP (steamapps/workshop/content/108600).";
                return;
            }

            UpdatesText = "Reading the server's workshop manifest...";
            IReadOnlyDictionary<string, InstalledWorkshopItem>? installed;
            using (var fs = await SftpRemoteFileSystem.ConnectAsync(sftp, CancellationToken.None))
                installed = await WorkshopManifest.ReadAsync(fs, folder);
            if (installed is null)
            {
                UpdatesText = $"The server has no readable {WorkshopManifest.FileName} next to {folder}, so its mod versions are unknown.";
                return;
            }

            UpdatesText = "Asking Steam...";
            var steam = await new WorkshopApi(Http).GetDetailsAsync(ids);
            var rows = ModUpdates.Compare(ids, installed, steam);
            Mods.Clear();
            foreach (var r in rows)
                Mods.Add(new ModUpdateRow(r));

            int updates = rows.Count(r => r.State == ModUpdateState.UpdateAvailable);
            int missing = rows.Count(r => r.State == ModUpdateState.NotOnServer);
            int unknown = rows.Count(r => r.State == ModUpdateState.Unknown);
            UpdatesFound = updates + missing > 0;
            var text = updates == 0 && missing == 0
                ? $"All {rows.Count} mods are up to date."
                : (updates > 0 ? $"{updates} mod{(updates == 1 ? " has" : "s have")} a newer version on Steam. " : "")
                  + (missing > 0 ? $"{missing} not installed yet. " : "")
                  + "A restart makes the server download them.";
            if (unknown > 0)
                text += $" {unknown} can't be checked (not public on Steam).";
            UpdatesText = text + $" Checked at {DateTime.Now:HH:mm}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            UpdatesText = "Check failed: " + ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>The mod's Steam Workshop page in the default browser.</summary>
    [RelayCommand]
    private void OpenWorkshopPage(ModUpdateRow? row)
    {
        // ids come from the server's WorkshopItems, digits only: nothing else reaches the shell
        if (row is null || !row.WorkshopId.All(char.IsAsciiDigit) || row.WorkshopId.Length == 0)
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                $"https://steamcommunity.com/sharedfiles/filedetails/?id={row.WorkshopId}") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            UpdatesText = "Could not open the browser: " + ex.Message;
        }
    }

    // ---- restart ----

    public IReadOnlyList<int> MinuteChoices { get; } = [1, 2, 5, 10, 15, 30, 60];

    [ObservableProperty] private int restartMinutes = 5;
    [ObservableProperty] private string restartMessage = RestartCountdown.DefaultMessage;
    [ObservableProperty] private string restartStatus = "";
    [ObservableProperty] private string countdownText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool isCountingDown;

    /// <summary>Saving and quitting, after the countdown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool isQuitting;

    public bool IsIdle => !IsCountingDown && !IsQuitting;

    [RelayCommand]
    private async Task StartRestartAsync()
    {
        if (!_main.IsSessionActive)
        {
            RestartStatus = "Not connected.";
            return;
        }
        if (_main.Confirm?.Invoke(
                $"Restart the server in {RestartCountdown.Describe(RestartMinutes * 60)}?\n\n"
                + "Players are warned in chat as the time runs out; then SpiffoCON sends save and quit. "
                + "Starting it again is up to your host (most hosts restart a server that quits; check yours once).") != true)
            return;
        // the session may have dropped while the question was open
        if (!_main.IsSessionActive || IsCountingDown || IsQuitting)
            return;

        _restartAtTicks = Environment.TickCount64 + RestartMinutes * 60_000L;
        _lastTickTicks = Environment.TickCount64;
        _warnings = new Queue<int>(RestartCountdown.WarningsFor(RestartMinutes * 60));
        IsCountingDown = true;
        RestartStatus = $"Restarting at {DateTime.Now.AddMinutes(RestartMinutes):HH:mm:ss}.";
        _tick.Start();
        await TickAsync();
    }

    [RelayCommand]
    private async Task CancelRestartAsync()
    {
        if (!IsCountingDown)
            return;
        StopCountdown();
        RestartStatus = $"Restart called off at {DateTime.Now:HH:mm:ss}.";
        await _main.RunAsync(ServerMessage.BuildCommand("<RGB:0.3,0.9,0.3>The restart has been called off."));
    }

    void StopCountdown()
    {
        _tick.Stop();
        IsCountingDown = false;
        CountdownText = "";
    }

    bool _ticking;

    async Task TickAsync()
    {
        if (_ticking || !IsCountingDown)
            return;
        _ticking = true;
        try
        {
            long now = Environment.TickCount64;
            // the PC slept (or stalled) through part of the countdown: the players' last warning may be
            // long gone, so don't quit on them now
            if (now - _lastTickTicks > 20_000)
            {
                StopCountdown();
                RestartStatus = $"Restart called off at {DateTime.Now:HH:mm:ss}: SpiffoCON was paused (the PC slept?) during the countdown. Start it again if you still want it.";
                await _main.RunAsync(ServerMessage.BuildCommand("<RGB:0.3,0.9,0.3>The restart has been called off."));
                return;
            }
            _lastTickTicks = now;
            int left = (int)Math.Ceiling((_restartAtTicks - now) / 1000.0);
            CountdownText = left > 0 ? $"{left / 60}:{left % 60:00}" : "0:00";
            if (left > 0)
            {
                // the latest warning point reached (several at once after a stall: only the last)
                int? due = null;
                while (_warnings.Count > 0 && _warnings.Peek() >= left)
                    due = _warnings.Dequeue();
                if (due is { } point)
                    await _main.RunAsync(ServerMessage.BuildCommand(RestartCountdown.Message(RestartMessage, Math.Min(point, left + 1))));
                return;
            }

            StopCountdown();
            await SaveAndQuitAsync();
        }
        finally
        {
            // a warning that took long to send (a server stalled by a save) is not the PC sleeping
            _lastTickTicks = Environment.TickCount64;
            _ticking = false;
        }
    }

    /// <summary>Restart at once: no countdown, one line in chat, then save and quit.</summary>
    [RelayCommand]
    private async Task RestartNowAsync()
    {
        if (!_main.IsSessionActive)
        {
            RestartStatus = "Not connected.";
            return;
        }
        int online = _main.OnlinePlayers.Count;
        if (_main.Confirm?.Invoke(
                "Restart the server now, without a countdown?\n\n"
                + (online > 0 ? $"{online} player{(online == 1 ? " is" : "s are")} online and will be disconnected at once. " : "Nobody is online. ")
                + "SpiffoCON sends save and quit; starting it again is up to your host.") != true)
            return;
        if (!_main.IsSessionActive || IsCountingDown || IsQuitting)
            return;
        if (online > 0)
            await _main.RunAsync(ServerMessage.BuildCommand("<RGB:1,0.4,0.3>The server is restarting now."));
        await SaveAndQuitAsync();
    }

    /// <summary>save, quit, then wait for the server to answer again.</summary>
    async Task SaveAndQuitAsync()
    {
        IsQuitting = true;
        try
        {
            RestartStatus = "Saving...";
            var saved = await _main.RunAsync("save");
            if (_closed)
                return;
            if (!_main.IsSessionActive)
            {
                RestartStatus = "Disconnected before quit was sent: the server was not restarted.";
                return;
            }
            RestartStatus = saved is null ? "save failed (see the console); quitting anyway..." : "Quitting...";
            _main.ExpectShutdown();
            var quit = await _main.RunAsync("quit");
            if (_closed || !_main.IsSessionActive)
                return;
            if (quit is null && _main.LastCommandNotSent)
            {
                // the link went down first: nothing reached the server, so its return is not waited for
                _main.EndExpectedShutdown();
                RestartStatus = $"quit could not be sent at {DateTime.Now:HH:mm:ss} (no connection): the server was not restarted.";
                return;
            }
            // no reply is normal when the server closes at once
            RestartStatus = (quit is null ? $"quit sent at {DateTime.Now:HH:mm:ss} (no reply: the server may already be closing). " : $"Server stopped at {DateTime.Now:HH:mm:ss}. ")
                + "SpiffoCON checks every 15 s and tells you when it is back.";
            _waitStarted = Environment.TickCount64;
            _answeredPolls = 0;
            _waitBack.Start();
        }
        finally
        {
            IsQuitting = false;
        }
    }

    bool _closed;
    long _waitStarted;

    /// <summary>How long to wait for the server to answer again before giving up.</summary>
    static readonly TimeSpan WaitBackLimit = TimeSpan.FromMinutes(15);

    async Task WaitBackTickAsync()
    {
        // not the wall clock: a time sync or a changed clock must not end (or stretch) the wait
        if (Environment.TickCount64 - _waitStarted > WaitBackLimit.TotalMilliseconds)
        {
            _waitBack.Stop();
            _main.EndExpectedShutdown();
            RestartStatus = $"No answer {WaitBackLimit.TotalMinutes:0} minutes after quit: check the host's panel. SpiffoCON reconnects with the next command.";
            return;
        }
        if (_waitTicking)
            return;
        _waitTicking = true;
        try
        {
            await _main.RefreshPlayersAsync(quiet: true);
        }
        finally
        {
            _waitTicking = false;
        }
        // "back" may have been announced during that poll, which stopped the wait
        if (!_waitBack.IsEnabled)
            return;
        if (_main.ConnectionLostSinceShutdown)
            _answeredPolls = 0;
        // still answering 3 minutes after quit, without ever dropping: it did not stop (a big world
        // may keep RCON up for a while as it saves)
        if (!_main.ConnectionLostSinceShutdown && ++_answeredPolls >= 12)
        {
            _waitBack.Stop();
            _main.EndExpectedShutdown();
            RestartStatus = "The server is still answering 3 minutes after quit: it did not stop (see the console).";
        }
    }

    int _answeredPolls;
    bool _waitTicking;

    public void Close()
    {
        _closed = true;
        _tick.Stop();
        _waitBack.Stop();
    }
}
