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
    DateTime _restartAt;
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

    [ObservableProperty] private string updatesText = "Compares the mods the server has installed with Steam. Needs SFTP.";
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

        _restartAt = DateTime.Now.AddMinutes(RestartMinutes);
        _warnings = new Queue<int>(RestartCountdown.WarningsFor(RestartMinutes * 60));
        IsCountingDown = true;
        RestartStatus = $"Restarting at {_restartAt:HH:mm:ss}.";
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
            int left = (int)Math.Ceiling((_restartAt - DateTime.Now).TotalSeconds);
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
            IsQuitting = true;
            try
            {
                RestartStatus = "Saving...";
                var saved = await _main.RunAsync("save");
                if (_closed)
                    return;
                RestartStatus = saved is null ? "save failed (see the console); quitting anyway..." : "Quitting...";
                _main.ExpectShutdown();
                var quit = await _main.RunAsync("quit");
                if (_closed)
                    return;
                // no reply is normal when the server closes at once
                RestartStatus = (quit is null ? $"quit sent at {DateTime.Now:HH:mm:ss} (no reply: the server may already be closing). " : $"Server stopped at {DateTime.Now:HH:mm:ss}. ")
                    + "SpiffoCON checks every 15 s and tells you when it is back.";
                _waitStarted = DateTime.Now;
                _answeredPolls = 0;
                _waitBack.Start();
            }
            finally
            {
                IsQuitting = false;
            }
        }
        finally
        {
            _ticking = false;
        }
    }

    bool _closed;
    DateTime _waitStarted;

    /// <summary>How long to wait for the server to answer again before giving up.</summary>
    static readonly TimeSpan WaitBackLimit = TimeSpan.FromMinutes(15);

    async Task WaitBackTickAsync()
    {
        if (DateTime.Now - _waitStarted > WaitBackLimit)
        {
            _waitBack.Stop();
            _main.EndExpectedShutdown();
            RestartStatus = $"No answer {WaitBackLimit.TotalMinutes:0} minutes after quit: check the host's panel. SpiffoCON reconnects with the next command.";
            return;
        }
        await _main.RefreshPlayersAsync(quiet: true);
        // still answering a minute after quit, without ever dropping: it did not stop
        if (!_main.ConnectionLostSinceShutdown && ++_answeredPolls >= 4)
        {
            _waitBack.Stop();
            _main.EndExpectedShutdown();
            RestartStatus = "The server is still running: quit did not stop it (see the console).";
        }
    }

    int _answeredPolls;

    public void Close()
    {
        _closed = true;
        _tick.Stop();
        _waitBack.Stop();
    }
}
