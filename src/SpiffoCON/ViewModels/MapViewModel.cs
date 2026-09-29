using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Controls;
using SpiffoCON.Core.Commands;
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
    readonly string _modCacheFolder;
    readonly string _steamCmdWorkshop;
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
        _modCacheFolder = Path.Combine(dataFolder, "cache", "map", "mods");
        // whole mods downloaded by the Catalog tab's SteamCMD
        _steamCmdWorkshop = Path.Combine(dataFolder, "steamcmd", "steamapps", "workshop", "content", "108600");
        _main.PropertyChanged += OnMainChanged;
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

    void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        // opened before connecting: now the server's Map= (and its mod maps) can be read
        if (e.PropertyName == nameof(MainViewModel.IsSessionActive) && _main.IsSessionActive && _activated && _maps is null && !_closed)
            _ = LoadAsync();
    }

    MapFiles? FindVanilla() =>
        MapFiles.FromFolder(_main.MapFolder)
        ?? MapFiles.FromFolder(SteamLocator.FindGameFolder())
        ?? MapFiles.FromFolder(_cacheFolder);

    /// <summary>The server's Map= (null until read) and its workshop items.</summary>
    IReadOnlyList<string>? _maps;
    IReadOnlyList<string> _items = [];

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync(refreshMods: true);

    /// <summary>
    /// Reads the server's Map= (when connected), finds the base game's map and each mod map, and builds the
    /// scene. Mod maps come from SpiffoCON's copy, the workshop folders on this PC, or the server over SFTP.
    /// </summary>
    async Task LoadAsync(bool refreshMods = false)
    {
        var ct = Restart();
        IsBusy = true;
        var progress = new Progress<string>(s => StatusText = s);
        try
        {
            if (_main.IsSessionActive)
            {
                StatusText = "Reading the server's maps (showoptions)...";
                if (await _main.RunAsync("showoptions", logReply: false) is { } reply && ServerOptions.Parse(reply) is { Count: > 0 } options)
                {
                    _maps = options.Maps.Count > 0 ? options.Maps : [MapFiles.VanillaMap];
                    _items = options.WorkshopItems;
                }
                if (ct.IsCancellationRequested)
                    return;
            }
            var names = _maps ?? [MapFiles.VanillaMap];
            bool wantsVanilla = names.Contains(MapFiles.VanillaMap, StringComparer.OrdinalIgnoreCase);
            var vanilla = wantsVanilla ? FindVanilla() : null;
            CanDownload = wantsVanilla && vanilla is null;

            var modNames = names.Where(n => !n.Equals(MapFiles.VanillaMap, StringComparison.OrdinalIgnoreCase)).ToList();
            var modFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var roots = SteamLocator.FindWorkshopFolders().Append(_steamCmdWorkshop).ToList();
            foreach (var name in modNames)
            {
                if (((refreshMods ? null : ModMaps.FromCache(_modCacheFolder, name)) ?? ModMaps.FindLocal(roots, _items, name)) is { } folder)
                    modFolders[name] = folder;
            }
            // the server's own copy: the ones missing here, or all of them when reloading
            var fetch = modNames.Where(n => refreshMods || !modFolders.ContainsKey(n)).ToList();
            string? sftpProblem = null;
            if (fetch.Count > 0 && _items.Count > 0 && _main.CurrentSftpSettings() is { } sftp)
            {
                try
                {
                    var workshop = await _main.FindWorkshopFolderAsync(progress, ct);
                    sftp = _main.CurrentSftpSettings() ?? sftp;
                    if (workshop is null)
                        sftpProblem = "no workshop folder found over SFTP";
                    else
                    {
                        using var fs = await SftpRemoteFileSystem.ConnectAsync(sftp, ct);
                        foreach (var (name, folder) in await ModMaps.DownloadAsync(fs, workshop, _items, fetch, _modCacheFolder, progress, ct))
                            modFolders[name] = folder;
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    sftpProblem = ex.Message;
                }
            }
            if (ct.IsCancellationRequested)
                return;

            var sources = new List<MapSource>();
            foreach (var name in names)
            {
                if (name.Equals(MapFiles.VanillaMap, StringComparison.OrdinalIgnoreCase))
                {
                    if (vanilla is not null)
                        sources.Add(new MapSource(name, vanilla.MapFolder, IsMod: false, vanilla.LabelsJson));
                }
                else if (modFolders.TryGetValue(name, out var folder))
                    sources.Add(new MapSource(name, folder, IsMod: true));
            }
            var missing = modNames.Where(n => !modFolders.ContainsKey(n)).ToList();
            if (sources.Count == 0)
            {
                Scene = null;
                StatusText = "The map is drawn from the game's own files, and there is no Project Zomboid install on this PC: "
                    + "download them from the server over SFTP (about 17 MB, once for all servers), or choose a game folder.";
                return;
            }

            StatusText = "Reading the map...";
            var scene = await Task.Run(() => MapScene.Build(vanilla, sources), ct);
            if (ct.IsCancellationRequested || _closed)
            {
                scene.Dispose();
                return;
            }
            var old = Scene;
            Scene = scene;
            old?.Dispose();

            var mods = sources.Where(s => s.IsMod).Select(s => s.Name).ToList();
            StatusText = (vanilla is null ? "" : $"Map from {vanilla.MapFolder}")
                + (mods.Count > 0 ? (vanilla is null ? "" : " · ") + $"mod maps: {string.Join(", ", mods)}" : "")
                + (wantsVanilla && vanilla is null ? " · the base game's map is missing: download it or choose a game folder" : "")
                + (missing.Count > 0 ? $" · not found: {string.Join(", ", missing)}" + (sftpProblem is not null ? $" (SFTP: {sftpProblem})"
                    : _main.CurrentSftpSettings() is null ? " (turn on SFTP to copy them from the server)" : "") : "")
                + (_maps is null ? " · connect to draw the server's mod maps too" : "")
                + (scene.Satellite is null ? "" : " · satellite view available");
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
            if (!ct.IsCancellationRequested)
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
            await LoadAsync();
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
            if (!ct.IsCancellationRequested)
                IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (PickFolder?.Invoke() is not { } folder)
            return;
        if (MapFiles.FromFolder(folder) is null)
        {
            StatusText = $"No map there: choose the ProjectZomboid folder (the one with media\\maps\\{MapFiles.VanillaMap}).";
            return;
        }
        _main.MapFolder = folder;
        await LoadAsync();
    }

    [RelayCommand]
    private void Fit() => FitRequested?.Invoke();

    [RelayCommand]
    private void CopySquare() => SafeClipboard.SetText($"{ContextSquare.X:0},{ContextSquare.Y:0},0");

    // ---- zombies (right-click) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpawnHordeText))]
    private string hordeCount = "20";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpawnHordeText), nameof(RemoveZombiesText))]
    private string hordeRadius = "5";

    public string SpawnHordeText => $"Spawn {HordeCount.Trim()} zombies here";

    public string RemoveZombiesText => $"Remove the zombies within {HordeRadius.Trim()} squares";

    bool ReadHorde(out int count, out int radius)
    {
        radius = 0;
        if (!int.TryParse(HordeCount.Trim(), out count) || count < 1 || count > EventCommands.MaxHorde)
        {
            StatusText = $"Zombies: a whole number from 1 to {EventCommands.MaxHorde}.";
            return false;
        }
        if (!int.TryParse(HordeRadius.Trim(), out radius) || radius < 1 || radius > EventCommands.MaxRadius)
        {
            StatusText = $"Radius: a whole number of squares from 1 to {EventCommands.MaxRadius}.";
            return false;
        }
        return true;
    }

    [RelayCommand]
    private Task SpawnHordeAsync()
    {
        if (!ReadHorde(out var count, out var radius))
            return Task.CompletedTask;
        int x = (int)ContextSquare.X, y = (int)ContextSquare.Y;
        return RunZombieCommandAsync(EventCommands.HordeAt(x, y, 0, count, radius),
            $"Spawn {count} zombies within {radius} squares of {x}, {y}?",
            $"{count} zombies spawned at {x}, {y}.");
    }

    [RelayCommand]
    private Task RemoveZombiesAsync()
    {
        if (!ReadHorde(out _, out var radius))
            return Task.CompletedTask;
        int x = (int)ContextSquare.X, y = (int)ContextSquare.Y;
        return RunZombieCommandAsync(EventCommands.RemoveZombiesAt(x, y, 0, radius),
            $"Remove the zombies within {radius} squares of {x}, {y}?",
            $"Zombies within {radius} squares of {x}, {y} removed.");
    }

    async Task RunZombieCommandAsync(string command, string confirm, string done)
    {
        if (!_main.IsSessionActive)
        {
            StatusText = "Connect to the server first.";
            return;
        }
        if (_main.Confirm?.Invoke(confirm) != true)
            return;
        var reply = await _main.RunAsync(command);
        StatusText = reply is null ? "The command failed: see the console."
            : reply.Contains("invalid location", StringComparison.OrdinalIgnoreCase)
                ? "The server can only place zombies where the map is loaded, that is near a player: nobody is close enough to that spot."
            : EventCommands.Interpret(reply) == CommandOutcome.Failed ? "The server refused: " + reply.Trim()
            : done;
    }

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
        _main.PropertyChanged -= OnMainChanged;
        Scene?.Dispose();
    }
}
