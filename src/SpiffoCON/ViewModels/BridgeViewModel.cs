using SpiffoCON.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Bridge;
using SpiffoCON.Core.Files;

namespace SpiffoCON.ViewModels;

/// <summary>
/// The SpiffoCON Bridge mod: what RCON can't see (positions, inventories, vehicles, world),
/// read through files in the server's Zomboid/Lua folder over SFTP.
/// </summary>
public sealed partial class BridgeViewModel : ObservableObject
{
    readonly MainViewModel _main;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    BridgeClient? _client;
    /// <summary>The client a refresh is waiting on (a newer connection is not held up by it).</summary>
    BridgeClient? _refreshing;

    public BridgeViewModel(MainViewModel main)
    {
        _main = main;
        _timer.Tick += async (_, _) => await RefreshAsync();
        _main.OnlinePlayersChanged += (_, _) => DropOfflinePlayers();
        WorkshopId = BridgeMod.ReadWorkshopId(BridgeMod.DefaultWorkshopFolder) ?? BridgeMod.PublishedWorkshopId;
    }

    /// <summary>Asks for a local Zomboid/Lua folder; set by the view.</summary>
    public Func<string?>? PickFolder { get; set; }

    public ObservableCollection<BridgePlayer> Players { get; } = [];
    public ObservableCollection<BridgeItem> Inventory { get; } = [];
    public ObservableCollection<BridgeVehicle> Vehicles { get; } = [];

    [ObservableProperty] private string source = "Not connected to the bridge.";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string worldText = "";

    /// <summary>The last world state the bridge reported (weather included).</summary>
    [ObservableProperty] private BridgeWorld? world;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool autoRefresh = true;
    [ObservableProperty] private string? workshopId;
    [ObservableProperty] private string inventoryTitle = "Select a player to see their inventory.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayer))]
    private BridgePlayer? selectedPlayer;

    public bool HasPlayer => SelectedPlayer is not null;

    partial void OnAutoRefreshChanged(bool value) => UpdateTimer();

    bool _keepInventory;

    partial void OnSelectedPlayerChanged(BridgePlayer? value)
    {
        if (value is not null && !_keepInventory)
            _ = LoadInventoryAsync(value.Username);
    }

    void UpdateTimer()
    {
        if (AutoRefresh && IsConnected && !_closed)
            _timer.Start();
        else
            _timer.Stop();
    }

    // ---- setup ----

    [RelayCommand]
    private void PrepareUpload()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "bridge", BridgeMod.FolderName);
        if (!Directory.Exists(source))
        {
            StatusText = "The bridge files are missing from SpiffoCON's folder.";
            return;
        }
        try
        {
            var target = BridgeMod.ExportForUpload(source, BridgeMod.DefaultWorkshopFolder);
            WorkshopId = BridgeMod.ReadWorkshopId(BridgeMod.DefaultWorkshopFolder) ?? BridgeMod.PublishedWorkshopId;
            StatusText = $"Copied to {target}. Start Project Zomboid, open Workshop from the main menu, choose SpiffoCON Bridge and upload it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "Copy failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void CopyServerSettings()
    {
        var text = $"Mods={BridgeMod.ModId}" + (WorkshopId is null ? "" : $"\nWorkshopItems={WorkshopId}");
        SafeClipboard.SetText(text);
        StatusText = "Copied: add each to the end of the server's Mods= and WorkshopItems= lists (after a ';', "
            + "or in the host panel's mod fields; the Options tab edits them too), then restart the server.";
    }

    [RelayCommand]
    private async Task FindOnServerAsync()
    {
        var sftp = _main.CurrentSftpSettings();
        if (sftp is null)
        {
            StatusText = "Enable SFTP on the left (user and password): the bridge talks through files on the server.";
            return;
        }
        var lua = _main.Profile.SftpLuaFolder;
        if (lua is null)
        {
            StatusText = "Looking for the Zomboid folder over SFTP...";
            try
            {
                var probe = await _main.ProbeAsync(sftp);
                // from now on the key this probe saw is required
                sftp = _main.CurrentSftpSettings() ?? sftp;
                // Zomboid/Logs is found by the probe; the bridge's folder is its sibling Zomboid/Lua
                var logs = probe.LogFolders.OrderBy(p => p.Contains("/Zomboid/", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(p => p.Length).FirstOrDefault();
                if (logs is null)
                {
                    StatusText = "No Zomboid folder found over SFTP.";
                    return;
                }
                lua = logs[..logs.TrimEnd('/').LastIndexOf('/')] + "/Lua";
                _main.Profile.SftpLuaFolder = lua;
                _main.SaveProfile();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                StatusText = "SFTP: " + ex.Message;
                return;
            }
        }
        await ConnectAsync(new SftpBridgeFiles(sftp, lua));
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        if (PickFolder?.Invoke() is { } path)
            await ConnectAsync(new LocalBridgeFiles(path));
    }

    async Task ConnectAsync(IBridgeFiles files)
    {
        if (_closed)
        {
            files.Dispose();
            return;
        }
        _client?.Files.Dispose();
        _failures = 0;
        _retryAt = default;
        _unanswered = false;
        _client = new BridgeClient(files);
        Source = files.Description;
        StatusText = "Asking the bridge...";
        await PingAsync(_client);
        // someone is listed already (connecting while the first player loads in): no change of the list will come
        if (_unanswered && _main.OnlinePlayers.Count > 0)
            _ = WakeAsync(justAsked: true);
    }

    /// <summary>The bridge's files were reached but it did not answer (a paused, empty server): asked again when someone joins.</summary>
    bool _unanswered;

    async Task PingAsync(BridgeClient client)
    {
        try
        {
            var started = DateTime.Now;
            int version = await client.PingAsync();
            // connected to another folder or server meanwhile
            if (!ReferenceEquals(client, _client))
                return;
            _unanswered = false;
            BridgeVersion = version;
            IsConnected = true;
            StatusText = $"Bridge v{version} answered in {(DateTime.Now - started).TotalSeconds:0.0} s."
                + (CanAct ? "" : " Admin actions need bridge v2: upload the new version to the Workshop and restart the server.");
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!ReferenceEquals(client, _client))
                return;
            IsConnected = false;
            // no reply is what a paused server gives; a refused password or a missing folder is not asked again
            _unanswered = ex is BridgeException;
            // the online list is RCON's: without a session nothing tells when someone joins
            StatusText = ex.Message + (_unanswered && _main.IsSessionActive && _main.OnlinePlayers.Count == 0
                ? " Nobody is online: SpiffoCON asks again by itself when someone joins." : "");
        }
        UpdateTimer();
    }

    /// <summary>The connection a wake is retrying for (a newer connection gets its own).</summary>
    BridgeClient? _waking;

    /// <summary>
    /// Someone is online, so the server runs again: a bridge that did not answer (or whose refreshes were
    /// failing) is asked now. A player still loading keeps the server paused: tries 20 s apart, then every
    /// two minutes for a while (a server without the mod is not asked for ever).
    /// </summary>
    async Task WakeAsync(bool justAsked = false)
    {
        if (_closed || _client is not { } client || ReferenceEquals(_waking, client))
            return;
        if (IsConnected)
        {
            // not waiting out the back-off of the refreshes that failed while it was paused
            if (_failures > 0)
            {
                _failures = 0;
                _retryAt = default;
                await RefreshAsync();
            }
            return;
        }
        if (!_unanswered)
            return;
        _waking = client;
        try
        {
            for (int attempt = justAsked ? 1 : 0; attempt < 18; attempt++)
            {
                if (attempt > 0)
                    await Task.Delay(TimeSpan.FromSeconds(attempt <= 8 ? 20 : 120));
                if (_closed || !ReferenceEquals(client, _client) || IsConnected || !_unanswered || _main.OnlinePlayers.Count == 0)
                    return;
                StatusText = "Someone is online: asking the bridge again...";
                await PingAsync(client);
                if (IsConnected)
                    return;
            }
        }
        finally
        {
            if (ReferenceEquals(_waking, client))
                _waking = null;
        }
    }

    // ---- data ----

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var client = _client;
        if (client is null || ReferenceEquals(_refreshing, client) || _closed || DateTime.UtcNow < _retryAt)
            return;
        _refreshing = client;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var snapshot = await client.SnapshotAsync(timeout.Token);
            // connected to another folder or server meanwhile: this answer is not for the lists any more
            if (!ReferenceEquals(client, _client))
                return;
            _failures = 0;
            var w = snapshot.World;
            World = w;
            WorldText = $"{w.Year}-{w.Month:00}-{w.Day:00} {w.Hour:00}:{w.Minute:00} in game · {w.Temperature:0.#} °C"
                + (w.Rain > 0 ? $" · rain {w.Rain:P0}" : "") + (w.Fog > 0 ? $" · fog {w.Fog:P0}" : "")
                + $" · {w.Players} online · {w.ZombiesLoaded} zombies loaded · world age {w.WorldAgeHours} h";

            var keep = SelectedPlayer?.Username;
            Players.Clear();
            foreach (var p in snapshot.Players.OrderBy(p => p.Username, StringComparer.CurrentCultureIgnoreCase))
                Players.Add(p);
            // keep the selection without reloading the inventory each time
            _keepInventory = true;
            SelectedPlayer = Players.FirstOrDefault(p => p.Username == keep);
            _keepInventory = false;
            Vehicles.Clear();
            foreach (var v in snapshot.Vehicles.OrderBy(v => v.Script))
                Vehicles.Add(v);
            StatusText = $"Updated at {DateTime.Now:HH:mm:ss}" + (AutoRefresh ? " · every 15 s" : "")
                + (snapshot.Problem is { } problem ? $" · not available: {problem}" + (BridgeVersion < 4 && problem.Contains("vehicles") ? " (fixed in bridge v4: upload it to the Workshop)" : "") : "");
        }
        catch (Exception ex) when (!ReferenceEquals(client, _client) && ex is not OutOfMemoryException)
        {
            // the connection it used was replaced (and closed): not the new one's failure
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshAuthenticationException or SftpHostKeyMismatchException)
        {
            // retrying a rejected password every 15 s can get the PC banned by the host
            _timer.Stop();
            IsConnected = false;
            StatusText = $"Stopped: {ex.Message} Fix it on the left, then Find on server again.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a host that is down is not asked every 15 s
            _failures++;
            var wait = TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, _failures - 1)));
            _retryAt = DateTime.UtcNow + wait;
            StatusText = ex.Message + (_failures > 1 ? $" (trying again in {wait.TotalSeconds:0} s)" : "")
                + (_main.OnlinePlayers.Count == 0 ? " Nobody is online: SpiffoCON asks again by itself when someone joins." : "");
            // the last answer is not the present any more: nothing, rather than players where they were
            SelectedPlayer = null;
            Players.Clear();
            Vehicles.Clear();
        }
        finally
        {
            if (ReferenceEquals(_refreshing, client))
                _refreshing = null;
        }
    }

    /// <summary>
    /// The server's own list (RCON) says who is online: a player who left goes from the bridge's list right
    /// away, also when the bridge can't say so (it does not answer on a server paused because it is empty).
    /// </summary>
    void DropOfflinePlayers()
    {
        // the list is emptied also when the RCON session ends: that says nothing about who is online
        if (!_main.IsSessionActive)
            return;
        var online = _main.OnlinePlayers.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in Players.Where(p => !online.Contains(p.Username)).ToList())
        {
            if (SelectedPlayer == gone)
                SelectedPlayer = null;
            Players.Remove(gone);
        }
        // nobody online: no area is loaded, so no vehicle is either
        if (online.Count == 0)
            Vehicles.Clear();
        else
            _ = WakeAsync();
    }

    int _failures;
    DateTime _retryAt;

    int _inventoryLoad;

    /// <summary>Whose items <see cref="Inventory"/> shows (null while loading): removals act on them.</summary>
    string? _inventoryOwner;

    async Task LoadInventoryAsync(string username)
    {
        if (_client is null)
            return;
        int load = ++_inventoryLoad;
        InventoryTitle = $"Inventory of {username}: loading...";
        Inventory.Clear();
        _inventoryOwner = null;
        try
        {
            var items = await _client.InventoryAsync(username);
            // another player was picked meanwhile: this answer is not for the list any more
            if (load != _inventoryLoad)
                return;
            Inventory.Clear();
            foreach (var item in items)
                Inventory.Add(item);
            _inventoryOwner = username;
            InventoryTitle = $"Inventory of {username}: {items.Sum(i => i.Count)} items";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (load == _inventoryLoad)
                InventoryTitle = $"Inventory of {username}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyPosition(BridgePlayer? player)
    {
        if (player?.X is null)
            return;
        SafeClipboard.SetText($"{player.X},{player.Y},{player.Z}");
    }

    /// <summary>Fills the Players tab with this player and their position (for teleporting others there).</summary>
    [RelayCommand]
    private void UseInPlayersTab(BridgePlayer? player)
    {
        if (player is null)
            return;
        _main.Players.Target = player.Username;
        if (player.X is not null)
        {
            _main.Players.CoordX = player.X.ToString()!;
            _main.Players.CoordY = player.Y.ToString()!;
            _main.Players.CoordZ = player.Z.ToString()!;
        }
        StatusText = $"{player.Username} and their position are now in the Players tab.";
    }

    // ---- admin actions (bridge v2) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct))]
    private int bridgeVersion;

    /// <summary>Write actions arrived with bridge v2.</summary>
    public bool CanAct => BridgeVersion >= 2;

    async Task ActAsync(string confirm, Func<BridgeClient, Task<string>> action, bool reloadInventory = false)
    {
        if (_client is null)
            return;
        if (!CanAct)
        {
            StatusText = "This needs bridge v2 on the server: upload the new version to the Workshop and restart the server.";
            return;
        }
        if (_main.Confirm?.Invoke(confirm) != true)
            return;
        string result;
        try
        {
            result = await action(_client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = "Failed: " + ex.Message;
        }
        _retryAt = default;
        await RefreshAsync();
        // the action's outcome, not the refresh's "Updated at", is what the user needs to see
        StatusText = result;
        if (reloadInventory && SelectedPlayer is { } p)
            await LoadInventoryAsync(p.Username);
    }

    [RelayCommand]
    private Task HealAsync(BridgePlayer? player) => player is null ? Task.CompletedTask : ActAsync(
        $"Heal {player.Username} completely (all wounds, fractures and bites)?",
        async c => $"{player.Username} healed" + (await c.HealAsync(player.Username) is { } h ? $": health {h}." : "."));

    [RelayCommand]
    private Task RemoveOneAsync(BridgeItem? item) => RemoveAsync(item, 1);

    [RelayCommand]
    private Task RemoveAllAsync(BridgeItem? item) => RemoveAsync(item, item?.Count ?? 0);

    Task RemoveAsync(BridgeItem? item, int count)
    {
        if (item is null || _inventoryOwner is not { } owner)
            return Task.CompletedTask;
        // bridge v3 removes from the row's container; v2 from anywhere, main inventory first
        bool byContainer = BridgeVersion >= 3 && !string.IsNullOrEmpty(item.Container);
        var where = byContainer
            ? $"from {owner}'s {item.Container}"
            : $"from anywhere in {owner}'s inventory (bridge v{BridgeVersion} doesn't tell bags apart: v3 does)";
        return ActAsync(
            $"Remove {count} × {item.Name} ({item.FullType}) {where}?\n\nWorn clothes and attached items are not touched.",
            async c =>
            {
                var (removed, skipped) = await c.RemoveItemAsync(owner, item.FullType, count, byContainer ? item.Container : null);
                return $"Removed {removed} × {item.Name} from {owner}." + (skipped > 0 ? $" {skipped} worn or attached left in place." : "");
            },
            reloadInventory: true);
    }

    [RelayCommand]
    private Task RepairVehicleAsync(BridgeVehicle? v) => v?.Id is not int id ? Task.CompletedTask : ActAsync(
        $"Repair {v.Script} #{id} completely?",
        async c => { await c.RepairVehicleAsync(id, v.Script); return $"{v.Script} #{id} repaired."; });

    [RelayCommand]
    private Task RefuelVehicleAsync(BridgeVehicle? v) => v?.Id is not int id ? Task.CompletedTask : ActAsync(
        $"Fill the tank of {v.Script} #{id}?",
        async c => { await c.RefuelVehicleAsync(id, v.Script); return $"{v.Script} #{id} refuelled."; });

    [RelayCommand]
    private Task RemoveVehicleAsync(BridgeVehicle? v) => v?.Id is not int id ? Task.CompletedTask : ActAsync(
        $"Remove {v.Script} #{id} from the world for good?" + (v.Driver is { } d ? $"\n\n{d} is driving it." : ""),
        async c => { await c.RemoveVehicleAsync(id, v.Script); return $"{v.Script} #{id} removed."; });

    // ---- weather (bridge v5), for the Events tab ----

    /// <summary>Weather overrides arrived with bridge v5.</summary>
    public bool CanSetWeather => IsConnected && BridgeVersion >= 5;

    partial void OnIsConnectedChanged(bool value) => OnPropertyChanged(nameof(CanSetWeather));

    partial void OnBridgeVersionChanged(int value) => OnPropertyChanged(nameof(CanSetWeather));

    /// <summary>Sets (or with <paramref name="reset"/> gives back to the game) the weather; null when it worked, else why not.</summary>
    internal async Task<string?> SetWeatherAsync(IReadOnlyList<(BridgeClient.ClimateSetting Setting, double? Value)> settings, bool reset)
    {
        if (_client is null || !IsConnected)
            return "The weather is set through the SpiffoCON Bridge: connect it in the Bridge tab.";
        if (BridgeVersion < 5)
            return $"The weather needs bridge v5 (the server runs v{BridgeVersion}): upload the new version to the Workshop and restart the server.";
        try
        {
            World = reset ? await _client.ResetClimateAsync() : await _client.SetClimateAsync(settings);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not set the weather: " + ex.Message;
        }
    }

    // ---- zombie corpses (bridge v6), for the map and the player menu ----

    /// <summary>Why corpses can't be removed now (null: they can).</summary>
    public string? CorpsesProblem =>
        _client is null || !IsConnected ? "Zombie corpses are removed through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 6 ? $"Removing corpses needs bridge v6 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>
    /// Removes the zombie corpses within <paramref name="radius"/> squares of a spot, on every floor (players'
    /// and animals' corpses stay), after asking. Returns what happened, to show where it was asked.
    /// </summary>
    internal async Task<string?> RemoveCorpsesAsync(int x, int y, int radius, string where, string? aroundPlayer = null)
    {
        if (CorpsesProblem is { } problem)
            return problem;
        if (_main.Confirm?.Invoke($"Remove the zombie corpses within {radius} squares of {where}?\n\n"
                + "On every floor. The corpses of players and animals are left where they are. It can't be undone.") != true)
            return null;
        try
        {
            var (removed, loaded) = await _client!.RemoveCorpsesAsync(x, y, radius, aroundPlayer);
            // the server only has the squares around players in memory
            return loaded == 0 ? $"No corpses removed: the area around {where} is not loaded on the server (it only keeps the surroundings of players)."
                : removed == 0 ? $"No zombie corpses within {radius} squares of {where}."
                : $"{removed} zombie corpse{(removed == 1 ? "" : "s")} removed within {radius} squares of {where}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not remove the corpses: " + ex.Message;
        }
    }

    bool _closed;

    public void Close()
    {
        _closed = true;
        _timer.Stop();
        _client?.Files.Dispose();
    }
}
