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
    public ObservableCollection<BridgeVehicle> Vehicles { get; } = [];

    [ObservableProperty] private string source = "Not connected to the bridge.";
    [ObservableProperty] private string statusText = "";
    [ObservableProperty] private string worldText = "";

    /// <summary>The last world state the bridge reported (weather included).</summary>
    [ObservableProperty] private BridgeWorld? world;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool autoRefresh = true;
    [ObservableProperty] private string? workshopId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayer))]
    private BridgePlayer? selectedPlayer;

    public bool HasPlayer => SelectedPlayer is not null;

    partial void OnAutoRefreshChanged(bool value) => UpdateTimer();


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
        // what the previous folder or server said is not this one's: not its version (a refresh in flight
        // would ask an older bridge for what it does not know), not its safehouses
        BridgeVersion = 0;
        Safehouses.Clear();
        _allDeaths = [];
        Deaths.Clear();
        ZombieCells.Clear();
        _lastDeath = null;
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
            var snapshot = await client.SnapshotAsync(timeout.Token, safehouses: BridgeVersion >= 7, deathsAndZombies: BridgeVersion >= 8);
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
            SelectedPlayer = Players.FirstOrDefault(p => p.Username == keep);
            Vehicles.Clear();
            foreach (var v in snapshot.Vehicles.OrderBy(v => v.Script))
                Vehicles.Add(v);
            if (snapshot.Safehouses is { } safehouses)
            {
                Safehouses.Clear();
                foreach (var s in safehouses.OrderBy(s => s.Owner, StringComparer.CurrentCultureIgnoreCase))
                    Safehouses.Add(s);
            }
            if (snapshot.ZombieCells is { } cells)
            {
                ZombieCells.Clear();
                foreach (var cell in cells)
                    ZombieCells.Add(cell);
            }
            if (snapshot.Deaths is { } deaths)
                ShowDeaths(deaths);
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
            ZombieCells.Clear();
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
        {
            Vehicles.Clear();
            ZombieCells.Clear();
        }
        else
            _ = WakeAsync();
    }

    int _failures;
    DateTime _retryAt;

    /// <summary>The Character details window of a player (the button and the double click of the Bridge tab).</summary>
    [RelayCommand]
    private void ShowDetails(BridgePlayer? player)
    {
        if (player is not null)
            _main.PlayerActions.ShowDetails(player.Username);
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

    async Task ActAsync(string confirm, Func<BridgeClient, Task<string>> action)
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
    }

    Task SetStatus(string text)
    {
        StatusText = text;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task HealAsync(BridgePlayer? player) => player is null ? Task.CompletedTask
        : player.Dead == true ? SetStatus($"{player.Username} is dead: nothing to heal.")
        : ActAsync(
        $"Heal {player.Username} completely (all wounds, fractures and bites)?\n\n"
            + "A zombie infection stays, as with the game's own heal: \"Cure zombie infection\" in the player's menu ends it"
            + (BridgeVersion < 10 ? " (with bridge v10)." : "."),
        async c => $"{player.Username} healed" + (await c.HealAsync(player.Username) is { } h ? $": health {h}." : "."));

    /// <summary>
    /// For the character window: the inventory and (bridge v7) the sheet of an online player in one round
    /// trip, or why they can't be read.
    /// </summary>
    internal async Task<(IReadOnlyList<BridgeItem>? Items, BridgePlayerDetails? Details, string? Problem)> ReadCharacterAsync(string username)
    {
        if (_client is not { } client || !IsConnected)
            return (null, null, "Health, skills, equipment and inventory come from the SpiffoCON Bridge: connect it in the Bridge tab.");
        try
        {
            var (items, details) = await client.CharacterAsync(username, BridgeVersion >= 7);
            return (items, details, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, null, ex.Message);
        }
    }

    /// <summary>Removes items of a player (from the character window), after asking.</summary>
    internal Task RemoveItemAsync(string owner, BridgeItem item, int count)
    {
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
            });
    }

    [RelayCommand]
    private Task RepairVehicleAsync(BridgeVehicle? v) => v?.Id is not int id ? Task.CompletedTask : ActAsync(
        $"Repair {v.Script} #{id} completely?\n\n" + RepairDoes,
        async c => $"{v.Script} #{id} repaired." + (await c.RepairVehicleAsync(id, v.Script)).Describe());

    string RepairDoes => "Every part goes back to 100%, tyres are inflated and the tank filled"
        + (BridgeVersion >= 13 ? "; parts that are gone (a wheel, a window, the battery...) are put back, and the result names them."
            : ". Bridge v13 also checks that the parts that are gone (a wheel, a window...) are back, and names them.");

    /// <summary>The listed vehicle a player drives: how a bridge before v13 finds "their vehicle".</summary>
    BridgeVehicle? DrivenBy(string username) =>
        Vehicles.FirstOrDefault(v => v.Id is not null && string.Equals(v.Driver, username, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Why the vehicle a player is in can't be repaired from their menu (null: it can). Bridge v13 finds
    /// it through the player, driver or passenger; an older one only when the player drives it.
    /// </summary>
    public string? VehicleOfProblem(BridgePlayer player) =>
        _client is null || !IsConnected ? "This is done through the SpiffoCON Bridge: connect it in the Bridge tab."
        : !CanAct ? "Needs bridge v2 (Bridge tab)"
        : player.Dead == true ? "This player is dead."
        : string.IsNullOrEmpty(player.Vehicle) ? "This player is not in a vehicle."
        : BridgeVersion >= 13 || DrivenBy(player.Username) is not null ? null
        : $"{player.Username} is a passenger: bridge v{BridgeVersion} finds only the vehicle a player drives (v13 finds this one too).";

    /// <summary>
    /// Repairs completely the vehicle an online player is in, after asking. Returns what happened, for the
    /// status bar.
    /// </summary>
    internal async Task<string?> RepairVehicleOfAsync(BridgePlayer player)
    {
        if (VehicleOfProblem(player) is { } problem)
            return problem;
        var client = _client!;
        string username = player.Username;
        // before v13 the bridge has no "the vehicle of": the one the player drives, by its id
        var driven = BridgeVersion >= 13 ? null : DrivenBy(username);
        string what = driven is null ? player.Vehicle! : $"{driven.Script} #{driven.Id}";
        if (_main.Confirm?.Invoke($"Repair completely the vehicle {username} is in ({what})?\n\n" + RepairDoes) != true)
            return null;
        if (!ReferenceEquals(client, _client))
            return ConnectionChanged;
        try
        {
            var done = driven is null
                ? await client.RepairVehicleOfAsync(username)
                : await client.RepairVehicleAsync(driven.Id!.Value, driven.Script) with { Vehicle = driven.Script, Id = driven.Id };
            _retryAt = default;
            await RefreshAsync();
            return $"{done.Vehicle ?? "The vehicle"}{(done.Id is { } id ? $" #{id}" : "")} of {username} repaired." + done.Describe();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not repair the vehicle: " + ex.Message;
        }
    }

    /// <summary>Why a vehicle can't be shown part by part (null: it can): bridge v13.</summary>
    public string? VehicleDetailsProblem =>
        _client is null || !IsConnected ? "A vehicle's parts come from the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 13 ? $"A vehicle's parts need bridge v13 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>Opens the window of a vehicle of the list (its parts need bridge v13: the window says so).</summary>
    [RelayCommand]
    private void ShowVehicle(BridgeVehicle? v)
    {
        if (v?.Id is int id)
            _main.ShowVehicle?.Invoke(id, v.Script);
    }

    /// <summary>For the vehicle window: one vehicle part by part (bridge v13), or why not.</summary>
    internal async Task<(BridgeVehicleDetails? Details, string? Problem)> ReadVehicleAsync(int id, string? script)
    {
        if (VehicleDetailsProblem is { } problem)
            return (null, problem);
        try
        {
            return (await _client!.VehicleDetailsAsync(id, script), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// One part of a vehicle made whole, or put back when it is gone (from the vehicle window, bridge v13).
    /// Not asked first: one part, and the click says which. Returns what happened.
    /// </summary>
    internal async Task<string> RepairPartAsync(int id, string? script, BridgeVehiclePart part)
    {
        if (VehicleDetailsProblem is { } problem)
            return problem;
        try
        {
            var done = await _client!.RepairPartAsync(id, script, part.Id);
            string name = done.Part.Length > 0 ? done.Part : part.Name;
            return done.Missing ? $"{name} could not be put back: the game made no item for it."
                : done.WasMissing ? $"{name} put back."
                : done.Before is { } before and < 100 ? $"{name} repaired: {before}% to 100%."
                : $"{name} repaired.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not repair the part: " + ex.Message;
        }
    }

    /// <summary>A key of a vehicle for a player (from the vehicle window), after asking.</summary>
    internal Task GiveVehicleKeyAsync(BridgeVehicle v, string username)
    {
        if (v.Id is not int id)
            return Task.CompletedTask;
        if (V7Problem is { } problem)
            return SetStatus(problem);
        return ActAsync($"Give {username} a key of {v.Script} #{id}?",
            async c => $"{username} got {await c.GiveVehicleKeyAsync(id, v.Script, username) ?? "the key"} ({v.Script} #{id}).");
    }

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

    /// <summary>The time of day arrived with bridge v7.</summary>
    public bool CanSetTime => IsConnected && BridgeVersion >= 7;

    partial void OnIsConnectedChanged(bool value) => OnBridgeAbilitiesChanged();

    partial void OnBridgeVersionChanged(int value) => OnBridgeAbilitiesChanged();

    void OnBridgeAbilitiesChanged()
    {
        OnPropertyChanged(nameof(CanSetWeather));
        OnPropertyChanged(nameof(CanSetTime));
    }

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

    // ---- bridge v6 (corpses) and v7: areas (for the map and the player menu), safehouses, time, keys ----

    /// <summary>Why something that came with bridge v7 can't be done now (null: it can).</summary>
    public string? V7Problem =>
        _client is null || !IsConnected ? "This is done through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 7 ? $"This needs bridge v7 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>The last deaths of players the bridge keeps (v8), oldest first.</summary>
    public ObservableCollection<BridgeDeath> Deaths { get; } = [];

    /// <summary>The zombies of the loaded areas, per 10 x 10 squares (v8).</summary>
    public ObservableCollection<BridgeZombieCell> ZombieCells { get; } = [];

    /// <summary>The newest death seen (null: none read yet, so the first list is the past, not news).</summary>
    long? _lastDeath;

    // every death the bridge has; Deaths shows those after the marks the user cleared
    List<BridgeDeath> _allDeaths = [];

    void FilterDeaths()
    {
        long hiddenBefore = _main.DeathsHiddenBefore;
        Deaths.Clear();
        foreach (var d in _allDeaths.Where(d => (d.Time ?? long.MaxValue) > hiddenBefore))
            Deaths.Add(d);
    }

    /// <summary>Death marks the user cleared from the map that the bridge still has.</summary>
    public int ClearedDeaths => _allDeaths.Count - Deaths.Count;

    /// <summary>
    /// Clears the death marks from the map of this PC: the bridge keeps its list (the last 100), and deaths
    /// from now on show as usual. Returns what happened, for the status bar.
    /// </summary>
    internal string ClearDeathMarks()
    {
        int n = Deaths.Count;
        if (n == 0)
            return "No death marks to clear.";
        // the bridge's own clock, not this PC's: the newest death it has
        _main.DeathsHiddenBefore = _allDeaths.Max(d => d.Time) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        FilterDeaths();
        return $"{n} death mark{(n == 1 ? "" : "s")} cleared from the map on this PC; new deaths show as usual.";
    }

    /// <summary>Shows the cleared death marks again.</summary>
    internal string RestoreDeathMarks()
    {
        _main.DeathsHiddenBefore = 0;
        FilterDeaths();
        return $"The map shows every death the bridge has again ({Deaths.Count}).";
    }

    void ShowDeaths(IReadOnlyList<BridgeDeath> deaths)
    {
        _allDeaths = deaths.ToList();
        FilterDeaths();
        long newest = deaths.Max(d => d.Time) ?? 0;
        if (_lastDeath is { } last)
            foreach (var d in deaths.Where(d => d.Time > last))
            {
                // the character's name in the text: the title is cut short in the notification area
                var at = $"at {d.X}, {d.Y}" + (d.Z is { } z and not 0 ? $", floor {z}" : "");
                _main.Notify(NotificationKind.Deaths, $"{d.Username} died",
                    (d.CharacterName is { Length: > 0 } name && !name.Equals(d.Username, StringComparison.OrdinalIgnoreCase) ? $"{name}, {at}" : char.ToUpperInvariant(at[0]) + at[1..])
                    + DescribeKiller(d.Killer)
                    // English text: "12.3 hours", whatever the PC's culture
                    + (d.HoursSurvived is { } h ? ", after " + h.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " hours" : "") + ".");
            }
        _lastDeath = Math.Max(_lastDeath ?? 0, newest);
    }

    /// <summary>"rj (Ray Jones)": the account and, when the bridge gives it, the character's own name.</summary>
    internal static string WithCharacter(string username, string? character) =>
        character is { Length: > 0 } && !character.Equals(username, StringComparison.OrdinalIgnoreCase) ? $"{username} ({character})" : username;

    internal static string DescribeKiller(string? killer) => killer switch
    {
        null or "" => "",
        "zombie" => ", killed by zombies",
        "animal" => ", killed by an animal",
        var animal when animal.StartsWith("animal:", StringComparison.Ordinal) =>
            animal.Length > "animal:".Length ? $", killed by an animal ({animal["animal:".Length..]})" : ", killed by an animal",
        var player => $", killed by {player}",
    };

    /// <summary>Removes the wrecks (burnt and smashed vehicles) within a radius, after counting them and asking.</summary>
    internal async Task<string?> RemoveWrecksAsync(int x, int y, int radius, string where, string? aroundPlayer = null)
    {
        if (V8Problem is { } problem)
            return problem;
        try
        {
            var count = await _client!.RemoveWrecksAsync(x, y, radius, apply: false, aroundPlayer);
            if (count.Found == 0)
                return $"No wrecks within {radius} squares of {where} (only the areas near players are loaded on the server).";
            if (_main.Confirm?.Invoke($"Remove the {count.Found} wreck{(count.Found == 1 ? "" : "s")} (burnt or smashed vehicles) within {radius} squares of {where}?\n\n"
                    + "Vehicles that can still be driven stay, and so does a wreck with someone inside. It can't be undone.") != true)
                return null;
            var done = await _client!.RemoveWrecksAsync(x, y, radius, apply: true, aroundPlayer);
            _retryAt = default;
            await RefreshAsync();
            return $"{done.Removed} wreck{(done.Removed == 1 ? "" : "s")} removed within {radius} squares of {where}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not remove the wrecks: " + ex.Message;
        }
    }

    /// <summary>Why the zombie infection can't be cured, nor beards and colours set, now (null: they can): bridge v10.</summary>
    public string? V10Problem =>
        _client is null || !IsConnected ? "This is done through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 10 ? $"This needs bridge v10 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>Cures an online player's zombie infection, after asking. Returns what happened, for the status bar.</summary>
    internal async Task<string?> CureInfectionAsync(string username)
    {
        if (V10Problem is { } problem)
            return problem;
        // the bridge it was asked of: "Find on server" may connect another one while this waits
        var client = _client!;
        if (_main.Confirm?.Invoke($"Cure {username}'s zombie infection?\n\nThe infection, its fever and its countdown end; "
                + "wounds and bites stay as they are (Heal completely heals them).") != true)
            return null;
        if (!ReferenceEquals(client, _client))
            return ConnectionChanged;
        try
        {
            var (was, still) = await client.CureInfectionAsync(username);
            _retryAt = default;
            await RefreshAsync();
            return still ? $"The server still says {username} is infected: the cure did not take."
                : was ? $"{username} is no longer infected."
                : $"{username} was not infected (nothing to cure; any fake infection of a hypochondriac is gone too).";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not cure the infection: " + ex.Message;
        }
    }

    /// <summary>Why a hair style can't be set now (null: it can): that came with bridge v9.</summary>
    public string? V9Problem =>
        _client is null || !IsConnected ? "This is done through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 9 ? $"This needs bridge v9 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>
    /// Lists the hair styles (and with bridge v10 beards and colours) for an online player, lets the user pick
    /// (<paramref name="choose"/>, null when cancelled) and gives them what changed. Returns what happened,
    /// for the status bar.
    /// </summary>
    internal async Task<string?> SetHairAsync(string username, Func<BridgeHairStyles, HairChoice?> choose)
    {
        if (V9Problem is { } problem)
            return problem;
        // the bridge it was asked of, for every step: "Find on server" may connect another one meanwhile
        var client = _client!;
        BridgeHairStyles styles;
        try
        {
            styles = await client.HairStylesAsync(username);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not list the hair styles: " + ex.Message;
        }
        if (!ReferenceEquals(client, _client))
            return ConnectionChanged;
        if (choose(styles) is not { IsEmpty: false } choice)
            return null;
        if (!ReferenceEquals(client, _client))
            return ConnectionChanged;
        var done = new List<string>();
        try
        {
            if (choice.Hair is { } hair)
            {
                await client.SetHairAsync(username, hair);
                done.Add("hair style " + Label(styles.Styles, hair));
            }
            if (choice.Beard is { } beard)
            {
                await client.SetBeardAsync(username, beard);
                done.Add(beard == "" ? "no beard" : "beard " + Label(styles.Beards ?? [], beard));
            }
            if (choice.Color is { } color)
            {
                await client.SetHairColorAsync(username, color);
                done.Add("a new hair colour");
            }
            return $"{username} now has " + string.Join(", ", done) + ".";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return done.Count > 0
                ? $"{username} now has " + string.Join(", ", done) + ", but the bridge could not change the rest: " + ex.Message
                : "The bridge could not change the look: " + ex.Message;
        }
    }

    const string ConnectionChanged = "The bridge connection changed meanwhile (another server?): nothing was sent. Try again.";

    static string Label(IEnumerable<BridgeHairStyle> styles, string name) =>
        styles.FirstOrDefault(s => s.Name == name) is { Label: { Length: > 0 } label } && label != name ? $"{label} ({name})" : name;

    /// <summary>Why what came with bridge v8 (items on the ground, wrecks) can't be done now (null: it can).</summary>
    public string? V8Problem =>
        _client is null || !IsConnected ? "This is done through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 8 ? $"This needs bridge v8 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>
    /// Puts items on the floor of a square (ground floor), after asking; <paramref name="what"/> says it in
    /// the question ("5 × Axe", "the kit Starter").
    /// </summary>
    internal async Task<string?> SpawnItemsAsync(int x, int y, IReadOnlyList<(string FullType, int Count)> items, string what)
    {
        if (V8Problem is { } problem)
            return problem;
        int total = items.Sum(i => i.Count);
        if (total == 0)
            return "Nothing to put down.";
        if (total > BridgeClient.MaxSpawn)
            return $"{total} items at once is too many: at most {BridgeClient.MaxSpawn} (each one is an object of its own on the ground).";
        if (_main.Confirm?.Invoke($"Put {what} on the ground at {x}, {y}?"
                + (total > 50 ? $"\n\nThat is {total} objects of their own on the ground: many in one place slow the game down for the players nearby." : "")) != true)
            return null;
        try
        {
            var done = await _client!.SpawnItemsAsync(x, y, 0, items);
            return $"{done.Spawned} item{(done.Spawned == 1 ? "" : "s")} put on the ground at {x}, {y}."
                + (done.Unknown.Count > 0 ? $" Not known to the server (a mod it does not have?): {string.Join(", ", done.Unknown)}." : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not put the items down: " + ex.Message;
        }
    }

    /// <summary>Why corpses can't be removed now (null: they can): that came with bridge v6.</summary>
    public string? CorpsesProblem =>
        _client is null || !IsConnected ? "Zombie corpses are removed through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 6 ? $"Removing corpses needs bridge v6 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    const string NotLoaded = "is not loaded on the server (it only keeps the surroundings of players)";

    /// <summary>
    /// Removes the items lying on the ground within a radius, on every floor, after counting them and
    /// asking with the count. Items inside a safehouse are never touched.
    /// </summary>
    internal async Task<string?> RemoveGroundItemsAsync(int x, int y, int radius, string where, string? aroundPlayer = null)
    {
        if (V7Problem is { } problem)
            return problem;
        try
        {
            var count = await _client!.RemoveGroundItemsAsync(x, y, radius, apply: false, safehouses: false, aroundPlayer);
            static string Left(BridgeGroundItems r, string verb)
            {
                var parts = new List<string>();
                if (r.InSafehouses > 0)
                    parts.Add($"{r.InSafehouses} inside safehouses");
                if (r.OnFurniture > 0)
                    parts.Add($"{r.OnFurniture} placed on tables or shelves");
                return parts.Count > 0 ? $" {string.Join(" and ", parts)} {verb}." : "";
            }
            string kept = Left(count, "stay");
            if (count.Loaded == 0)
                return $"No items removed: the area around {where} {NotLoaded}.";
            if (count.Found == 0)
                return $"No items on the ground within {radius} squares of {where}." + kept;
            if (_main.Confirm?.Invoke($"Remove the {count.Found} item{(count.Found == 1 ? "" : "s")} lying on the ground within {radius} squares of {where}?\n\n"
                    + "On every floor. Furniture and what is inside containers stay." + kept + " It can't be undone.") != true)
                return null;
            var done = await _client!.RemoveGroundItemsAsync(x, y, radius, apply: true, safehouses: false, aroundPlayer);
            return $"{done.Removed} item{(done.Removed == 1 ? "" : "s")} on the ground removed within {radius} squares of {where}." + Left(done, "left");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not remove the items: " + ex.Message;
        }
    }

    /// <summary>Why the containers that lost their liquid part can't be looked for now (null: they can): bridge v14.</summary>
    public string? V14Problem =>
        _client is null || !IsConnected ? "This is done through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 14 ? $"This needs bridge v14 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>How far around each player the containers put down or stored are looked at.</summary>
    public const int FluidsRadius = 50;

    /// <summary>What Fix liquid containers is for, for its tooltips.</summary>
    public const string FixFluidsTip =
        "Buckets, bottles and pots that lost the part that holds liquids: after a server crash one that was put down shows as a plain "
        + "\"Bucket\" (not \"Empty Bucket\") and can't be filled any more. Counted first, then replaced with new, empty ones where they are";

    /// <summary>
    /// Counts the containers that lost their liquid part, asks, and replaces them with whole ones where they
    /// are: what the players online carry (one player, with <paramref name="username"/>), and what is put down
    /// or stored within <see cref="FluidsRadius"/> squares of each. Returns what happened, to show where it was asked.
    /// </summary>
    internal async Task<string?> FixFluidsAsync(string? username = null)
    {
        if (V14Problem is { } problem)
            return problem;
        var client = _client!;
        string who = username is null ? "the players online" : username;
        string around = username is null ? "each of them" : "them";
        string carry = username is null ? "carry" : "carries";
        static string S(int n) => n == 1 ? "" : "s";
        try
        {
            var count = await client.FixFluidsAsync(apply: false, FluidsRadius, username);
            if (count.Players == 0)
                return "Nobody is online: the server only keeps the surroundings of players, and what they carry.";
            if (count.Found == 0)
                return $"No container has lost its liquid part: looked at what {who} {carry} and at {FluidsRadius} squares around {around}.";
            var where = new List<string>();
            if (count.InInventories > 0)
            {
                var carriers = count.Items.Where(i => i.Where.Length > 0).GroupBy(i => i.Where).Select(g => $"{g.Key}: {g.Sum(i => i.Count)}");
                where.Add($"{count.InInventories} carried ({string.Join(", ", carriers)})");
            }
            if (count.OnSquares > 0)
                where.Add($"{count.OnSquares} put down on the ground or on furniture");
            if (count.InContainers > 0)
                where.Add($"{count.InContainers} in crates, shelves or fridges");
            string question = $"{count.Found} container{S(count.Found)} lost the part that holds liquids, and can't be filled: {count.Describe()}.\n\n"
                + $"{string.Join(", ", where)}; looked at what {who} {carry} and at {FluidsRadius} squares around {around}.\n\n"
                + (count.Found == 1 ? "Replace it with a new, empty one of the same type, in the same place?"
                    : "Replace each with a new, empty one of the same type, in the same place?")
                + (count.Skipped > 0 ? $"\n\n{count.Skipped} worn or attached (a bottle on a belt) will be left: the player has to take it off first." : "");
            if (_main.Confirm?.Invoke(question) != true)
                return null;
            if (!ReferenceEquals(client, _client))
                return ConnectionChanged;
            var done = await client.FixFluidsAsync(apply: true, FluidsRadius, username);
            return (done.Fixed == 0 ? "No container replaced." : $"{done.Fixed} container{S(done.Fixed)} replaced with {(done.Fixed == 1 ? "a whole one" : "whole ones")}.")
                + (done.Skipped > 0 ? $" {done.Skipped} worn or attached left as {(done.Skipped == 1 ? "it is" : "they are")}." : "")
                + (done.Failed > 0 ? $" {done.Failed} could not be replaced: the game made no whole item of that type." : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not look at the containers: " + ex.Message;
        }
    }

    /// <summary>Fix liquid containers, for everybody online (the Bridge tab's button).</summary>
    [RelayCommand]
    private async Task FixFluidsOfAllAsync()
    {
        if (await FixFluidsAsync() is { } result)
            StatusText = result;
    }

    /// <summary>Puts out the fires within a radius, on every floor (no question: nothing is lost).</summary>
    internal async Task<string?> StopFiresAsync(int x, int y, int radius, string where, string? aroundPlayer = null)
    {
        if (V7Problem is { } problem)
            return problem;
        try
        {
            var (stopped, loaded) = await _client!.StopFiresAsync(x, y, radius, aroundPlayer);
            return loaded == 0 ? $"No fires put out: the area around {where} {NotLoaded}."
                : stopped == 0 ? $"Nothing is burning within {radius} squares of {where}."
                : $"Fire put out on {stopped} square{(stopped == 1 ? "" : "s")} within {radius} squares of {where}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not put out the fires: " + ex.Message;
        }
    }

    /// <summary>The safehouses the bridge last reported (kept while it does not answer: they don't move).</summary>
    public ObservableCollection<BridgeSafehouse> Safehouses { get; } = [];

    internal static string Describe(BridgeSafehouse s)
    {
        int people = s.Members.Count + 1;
        return $"{(string.IsNullOrWhiteSpace(s.Title) ? "Safehouse" : s.Title)} of {s.Owner}"
            + (people > 1 ? $" ({people} people)" : "")
            + (s.LastVisited is > 0 and var ms ? $", last visited {DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:d MMM}" : "");
    }

    /// <summary>Removes a safehouse after asking; what is built or stored in it stays, anyone can enter it again.</summary>
    internal async Task<string?> RemoveSafehouseAsync(BridgeSafehouse s)
    {
        if (V7Problem is { } problem)
            return problem;
        if (_main.Confirm?.Invoke($"Remove the safehouse \"{s.Title}\" of {s.Owner} at {s.X}, {s.Y}?\n\n"
                + (s.Members.Count > 0 ? $"Members: {string.Join(", ", s.Members)}.\n" : "")
                + "The building and everything in it stay, but it is no longer protected: anyone can enter, loot and claim it. It can't be undone.") != true)
            return null;
        try
        {
            bool synced = await _client!.RemoveSafehouseAsync(s.Id, s.Owner);
            // by id: a refresh meanwhile refilled the list with other instances
            foreach (var gone in Safehouses.Where(x => x.Id == s.Id && x.Owner == s.Owner).ToList())
                Safehouses.Remove(gone);
            return $"Safehouse of {s.Owner} removed." + (synced ? "" : " The server could not tell the players online: they see it gone at their next login.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not remove the safehouse: " + ex.Message;
        }
    }

    /// <summary>The world as it is now (the time of day, for one), or null when the bridge can't say.</summary>
    internal async Task<BridgeWorld?> ReadWorldAsync()
    {
        if (_client is not { } client || !IsConnected)
            return null;
        try
        {
            var world = await client.WorldAsync();
            if (ReferenceEquals(client, _client))
                World = world;
            return world;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Skips the clock forward to an hour of the day, for everyone; null when it worked, else why not.</summary>
    internal async Task<string?> SetTimeAsync(double hour)
    {
        if (V7Problem is { } problem)
            return problem;
        try
        {
            World = await _client!.SetTimeAsync(hour);
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not set the time: " + ex.Message;
        }
    }

    /// <summary>A key of the vehicle for the player selected in the list above.</summary>
    [RelayCommand]
    private Task GiveVehicleKeyAsync(BridgeVehicle? v)
    {
        if (v?.Id is not int id)
            return Task.CompletedTask;
        if (V7Problem is { } problem)
        {
            StatusText = problem;
            return Task.CompletedTask;
        }
        if (SelectedPlayer is not { } player)
        {
            StatusText = "Select in the players list who gets the key, then press Key again.";
            return Task.CompletedTask;
        }
        return ActAsync($"Give {player.Username} a key of {v.Script} #{id}?",
            async c => $"{player.Username} got {await c.GiveVehicleKeyAsync(id, v.Script, player.Username) ?? "the key"} ({v.Script} #{id}).");
    }

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

/// <summary>What was picked in the hair window: a null field stays as it is ("" beard: none).</summary>
public sealed record HairChoice(string? Hair, string? Beard, BridgeColor? Color)
{
    public bool IsEmpty => Hair is null && Beard is null && Color is null;
}
