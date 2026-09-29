using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Controls;
using SpiffoCON.Core.Bridge;
using SpiffoCON.Core.Files;
using SpiffoCON.Core.Map;
using SpiffoCON.Core.Steam;
using SpiffoCON.Services;

namespace SpiffoCON.ViewModels;

/// <summary>
/// The game's world map with the players and vehicles the bridge reports. The map files come from a
/// game or dedicated server install on this PC, or are copied once from the server over SFTP.
/// </summary>
public sealed partial class MapViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly string _cacheFolder;
    CancellationTokenSource? _work;
    bool _activated;
    bool _closed;

    public MapViewModel(MainViewModel main)
    {
        _main = main;
        var dataFolder = Environment.GetEnvironmentVariable("SPIFFOCON_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpiffoCON");
        _cacheFolder = Path.Combine(dataFolder, "cache", "map", MapFiles.VanillaMap);
        Bridge.Players.CollectionChanged += OnPlayersChanged;
        Bridge.PropertyChanged += OnBridgeChanged;
    }

    public BridgeViewModel Bridge => _main.Bridge;

    /// <summary>Asks for a game folder; set by the view.</summary>
    public Func<string?>? PickFolder { get; set; }

    /// <summary>Center the view on a square; handled by the view.</summary>
    public event Action<double, double>? CenterRequested;

    /// <summary>Show the whole map; handled by the view.</summary>
    public event Action? FitRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMap))]
    private MapScene? scene;

    public bool HasMap => Scene is not null;

    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string playersText = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool canDownload;
    [ObservableProperty] private bool showVehicles = true;
    [ObservableProperty] private bool follow;
    [ObservableProperty] private string pointerText = "";
    [ObservableProperty] private Point contextSquare;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownloadSatellite))]
    private bool satellite;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TeleportHereText))]
    private string? selectedPlayer;

    public bool BridgeHint => !Bridge.IsConnected;

    /// <summary>The satellite view was asked for, but its images (pyramid.zip) are not here yet.</summary>
    public bool CanDownloadSatellite => Satellite && Scene is not null && Scene.Satellite is null;

    public string TeleportHereText => SelectedPlayer is { } p ? $"Teleport {p} here" : "Teleport here (select a player first)";

    partial void OnSatelliteChanged(bool value)
    {
        if (value && Scene is { Satellite: null })
            StatusText = "The satellite view needs the game's pyramid.zip (about 50 MB): download it from the server, or choose a game folder.";
    }

    partial void OnSelectedPlayerChanged(string? value)
    {
        if (Find(value) is { X: { } x, Y: { } y })
            CenterRequested?.Invoke(x + 0.5, y + 0.5);
    }

    partial void OnFollowChanged(bool value)
    {
        if (value && Find(SelectedPlayer) is { X: { } x, Y: { } y })
            CenterRequested?.Invoke(x + 0.5, y + 0.5);
    }

    BridgePlayer? Find(string? username) => username is null ? null : Bridge.Players.FirstOrDefault(p => p.Username == username);

    void OnBridgeChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BridgeViewModel.IsConnected))
            OnPropertyChanged(nameof(BridgeHint));
    }

    bool _playersQueued;

    // the bridge clears and refills the list on each refresh: react once, after it
    void OnPlayersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_playersQueued || _closed)
            return;
        _playersQueued = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _playersQueued = false;
            var players = Bridge.Players;
            int placed = players.Count(p => p.X is not null);
            PlayersText = players.Count == 0 ? "Nobody online." : $"{players.Count} online" + (placed < players.Count ? $", {placed} with a position" : "")
                + $" · {Bridge.Vehicles.Count} vehicles loaded · updated {DateTime.Now:HH:mm:ss}";
            if (Follow && Find(SelectedPlayer) is { X: { } x, Y: { } y })
                CenterRequested?.Invoke(x + 0.5, y + 0.5);
        });
    }

    /// <summary>Loads the map the first time the tab is shown.</summary>
    public void Activate()
    {
        if (_activated || _closed)
            return;
        _activated = true;
        _ = LoadAsync();
    }

    MapFiles? FindLocal() =>
        MapFiles.FromFolder(_main.MapFolder)
        ?? MapFiles.FromFolder(SteamLocator.FindGameFolder())
        ?? MapFiles.FromFolder(_cacheFolder);

    async Task LoadAsync()
    {
        if (FindLocal() is { } files)
        {
            await BuildAsync(files);
            return;
        }
        CanDownload = true;
        StatusText = "The map is drawn from the game's own files, and there is no Project Zomboid install on this PC: "
            + "download them from the server over SFTP (about 17 MB, once for all servers), or choose a game folder.";
    }

    async Task BuildAsync(MapFiles files)
    {
        var ct = Restart();
        IsBusy = true;
        StatusText = "Reading the map...";
        try
        {
            var scene = await Task.Run(() => MapScene.Build(files), ct);
            if (ct.IsCancellationRequested || _closed)
            {
                scene.Dispose();
                return;
            }
            var old = Scene;
            Scene = scene;
            old?.Dispose();
            CanDownload = false;
            StatusText = $"Map from {files.MapFolder}" + (scene.Satellite is null ? "" : " · satellite view available");
            OnSatelliteChanged(Satellite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidDataException)
        {
            StatusText = "The map files can't be read: " + ex.Message;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    CancellationToken Restart()
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();
        return _work.Token;
    }

    [RelayCommand]
    private Task DownloadAsync() => DownloadFromServerAsync(satellite: false);

    [RelayCommand]
    private Task DownloadSatelliteAsync() => DownloadFromServerAsync(satellite: true);

    async Task DownloadFromServerAsync(bool satellite)
    {
        if (_main.CurrentSftpSettings() is not { } sftp)
        {
            StatusText = "Turn on SFTP on the left (user and password) to copy the map from the server.";
            return;
        }
        var ct = Restart();
        IsBusy = true;
        var progress = new Progress<string>(s => StatusText = s);
        try
        {
            var root = MapFiles.RemoteRootFromWorkshop(await _main.FindWorkshopFolderAsync(progress, ct));
            if (root is null)
            {
                StatusText = "The server's game folder was not found over SFTP (it is looked for next to steamapps).";
                return;
            }
            sftp = _main.CurrentSftpSettings() ?? sftp;
            using (var fs = await SftpRemoteFileSystem.ConnectAsync(sftp, ct))
            {
                if (!await MapFiles.DownloadAsync(fs, root, _cacheFolder, satellite, progress, ct))
                {
                    StatusText = $"No map files in {root}/media/maps/{MapFiles.VanillaMap} on the server.";
                    return;
                }
            }
            IsBusy = false;
            if (MapFiles.FromFolder(_cacheFolder) is { } files)
                await BuildAsync(files);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "Download failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (PickFolder?.Invoke() is not { } folder)
            return;
        if (MapFiles.FromFolder(folder) is not { } files)
        {
            StatusText = $"No map there: choose the ProjectZomboid folder (the one with media\\maps\\{MapFiles.VanillaMap}).";
            return;
        }
        _main.MapFolder = folder;
        await BuildAsync(files);
    }

    [RelayCommand]
    private void Fit() => FitRequested?.Invoke();

    [RelayCommand]
    private void CopySquare() => SafeClipboard.SetText($"{ContextSquare.X:0},{ContextSquare.Y:0},0");

    [RelayCommand]
    private async Task TeleportHereAsync()
    {
        if (SelectedPlayer is not { } player)
        {
            StatusText = "Click a player on the map or in the list first.";
            return;
        }
        var players = _main.Players;
        players.Target = player;
        players.CoordX = ContextSquare.X.ToString("0");
        players.CoordY = ContextSquare.Y.ToString("0");
        players.CoordZ = "0";
        if (_main.Confirm?.Invoke($"Teleport {player} to {players.CoordX}, {players.CoordY} (ground floor)?") != true)
            return;
        await players.TeleportToCoordinatesCommand.ExecuteAsync(null);
        StatusText = players.Result;
        // show where they went without waiting for the next bridge refresh
        await Bridge.RefreshCommand.ExecuteAsync(null);
    }

    public void Close()
    {
        _closed = true;
        _work?.Cancel();
        Bridge.Players.CollectionChanged -= OnPlayersChanged;
        Bridge.PropertyChanged -= OnBridgeChanged;
        Scene?.Dispose();
    }
}
