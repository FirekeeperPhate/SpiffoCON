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
        // a new connection: the containers are looked at again, the animals read again
        _fluidsCheckedAt = default;
        _birthsLookAt = default;
        _birthsTold.Clear();
        FluidsNotice = "";
        Animals.Clear();
        AnimalsText = "";
        Generators.Clear();
        GeneratorsText = "";
        PerfBars.Clear();
        PerfText = "";
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
            if (BridgeVersion >= 20)
                ShowAutoFix(w.AutoFix);
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
            CheckFluidsWhenDue(client);
            if (BridgeVersion >= 21 && w.Players > 0 && _main.NotifyBirths && DateTime.UtcNow >= _birthsLookAt)
                await LookForBirthsAsync(client);
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
        OnPropertyChanged(nameof(CanAutoFix));
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

    /// <summary>
    /// Does something to the items of a row of a player's inventory (from the character window, bridge v14):
    /// repair, clean, fill, empty, recharge. Only emptying is asked first: the rest takes nothing away. Returns
    /// what happened.
    /// </summary>
    internal async Task<string?> ItemActionAsync(string owner, BridgeItem item, BridgeClient.ItemAction action)
    {
        if (V14Problem is { } problem)
            return problem;
        var client = _client!;
        string what = item.Count == 1 ? item.Name : $"{item.Count} × {item.Name}";
        if (action == BridgeClient.ItemAction.Empty
            && _main.Confirm?.Invoke($"Empty {what} of {owner}?\n\nThe liquid inside is lost.") != true)
            return null;
        if (!ReferenceEquals(client, _client))
            return ConnectionChanged;
        try
        {
            var (done, skipped) = await client.ItemActionAsync(owner, action, item.FullType, string.IsNullOrEmpty(item.Container) ? null : item.Container);
            string verb = action switch
            {
                BridgeClient.ItemAction.Repair => "repaired",
                BridgeClient.ItemAction.Clean => "cleaned",
                BridgeClient.ItemAction.Fill => "filled with water",
                BridgeClient.ItemAction.Empty => "emptied",
                _ => "recharged",
            };
            if (done == 0)
                return $"{item.Name} of {owner} can't be {verb}: it does not apply to this item.";
            return $"{(done == 1 ? item.Name : $"{done} × {item.Name}")} of {owner} {verb}."
                + (skipped > 0 ? $" {skipped} left: it does not apply to {(skipped == 1 ? "it" : "them")}." : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not do it: " + ex.Message;
        }
    }

    // ---- the containers that lost their liquid part, looked for without being asked ----

    /// <summary>"7 containers have lost their liquid part (5 × Bucket, 2 × Water Bottle)": what the last look found, or nothing.</summary>
    [ObservableProperty] private string fluidsNotice = "";

    DateTime _fluidsCheckedAt;
    int _playersAtLastCheck;

    // After a restart the server has nobody on it, and so nothing loaded: the look that counts is the one when
    // the first player is back. So: once when the bridge connects with someone online, and again each time
    // the players go from none to some, but not more often than every ten minutes.
    void CheckFluidsWhenDue(BridgeClient client)
    {
        int players = Players.Count;
        bool back = players > 0 && _playersAtLastCheck == 0;
        _playersAtLastCheck = players;
        if (BridgeVersion < 14 || players == 0)
            return;
        // the bridge looks by itself and fixes what it finds: no alarm for what is about to be fixed
        if (AutoFixOn)
            return;
        if (_fluidsCheckedAt != default && (!back || DateTime.UtcNow - _fluidsCheckedAt < TimeSpan.FromMinutes(10)))
            return;
        _fluidsCheckedAt = DateTime.UtcNow;
        _ = CheckFluidsAsync(client);
    }

    async Task CheckFluidsAsync(BridgeClient client)
    {
        try
        {
            var count = await client.FixFluidsAsync(apply: false, FluidsRadius);
            if (!ReferenceEquals(client, _client))
                return;
            // what the player wears or has on the belt can't be fixed from here: not worth an alarm
            int fixable = count.Found - count.Skipped;
            if (fixable <= 0)
            {
                FluidsNotice = "";
                return;
            }
            FluidsNotice = $"{BrokenSummary(count, fixable)} ({count.Describe()}). Fix containers and stations… puts them right.";
            _main.Notify(NotificationKind.Containers, "Broken containers or stations",
                $"{BrokenSummary(count, fixable)} ({count.Describe()}). Bridge tab: Fix containers and stations…");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a look nobody asked for: its failure is not news
        }
    }

    // ---- the bridge fixing them by itself (bridge v20) ----

    /// <summary>The bridge fixes the broken containers and built things by itself, on the server, SpiffoCON open or not.</summary>
    [ObservableProperty] private bool autoFixOn;

    /// <summary>The switch can be used: bridge v20 connected.</summary>
    public bool CanAutoFix => IsConnected && BridgeVersion >= 20;

    // set from what the bridge says, not by the person: not sent back to it
    bool _autoFixFromBridge;

    // the last look of the bridge that was told, so each is told once
    string? _autoFixToldAt;

    partial void OnAutoFixOnChanged(bool value)
    {
        if (!_autoFixFromBridge)
            _ = SetAutoFixAsync(value);
    }

    async Task SetAutoFixAsync(bool on)
    {
        if (!CanAutoFix || _client is not { } client)
        {
            StatusText = BridgeVersion < 20 && IsConnected
                ? $"Fixing them by itself needs bridge v20 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
                : "Connect the bridge first.";
            ShowAutoFix(null);
            return;
        }
        try
        {
            var state = await client.AutoFixAsync(on);
            if (!ReferenceEquals(client, _client))
                return;
            ShowAutoFix(state);
            StatusText = state.Enabled
                ? $"The bridge now looks every {state.Minutes} minutes with players online, and fixes the broken containers and built things it finds by itself. The first look is a minute from now."
                : "The bridge no longer fixes them by itself.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "The bridge could not be switched: " + ex.Message;
            ShowAutoFix(null);
        }
    }

    /// <summary>What the bridge says about its own look: the switch as it is there, and its last fixes told once.</summary>
    void ShowAutoFix(BridgeAutoFix? state)
    {
        _autoFixFromBridge = true;
        try
        {
            AutoFixOn = state?.Enabled ?? false;
        }
        finally
        {
            _autoFixFromBridge = false;
        }
        if (state?.Last is not { At: { } at } last || at == _autoFixToldAt)
            return;
        _autoFixToldAt = at;
        if (last.Fixed <= 0)
            return;
        FluidsNotice = "";
        string when = long.TryParse(at, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().ToString("HH:mm") : "";
        string text = $"The bridge fixed by itself{(when.Length > 0 ? " at " + when : "")}: {last.Describe()}"
            + (last.Failed > 0 ? $"; {last.Failed} could not be put right" : "")
            + (last.Skipped > 0 ? $"; {last.Skipped} worn or attached left" : "") + ".";
        _main.Log(ConsoleKind.Info, text);
        _main.Notify(NotificationKind.Containers, "Broken containers or stations fixed", text);
    }

    // ---- what a player carries, repaired in one go (bridge v15) ----

    /// <summary>Why what came with bridge v15 (repair a player's gear, the animals) can't be done now (null: it can).</summary>
    public string? V15Problem =>
        _client is null || !IsConnected ? "This is done through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 15 ? $"This needs bridge v15 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>Repairs what a player holds, wears and has attached, after asking. Returns what happened.</summary>
    internal async Task<string?> RepairGearAsync(string username)
    {
        if (V15Problem is { } problem)
            return problem;
        var client = _client!;
        if (_main.Confirm?.Invoke($"Repair everything {username} holds, wears and has attached?\n\n"
                + "Each goes back to new: the condition, the head of a tool, the edge of a blade; clothes are mended, their patches taken off, and washed.") != true)
            return null;
        if (!ReferenceEquals(client, _client))
            return ConnectionChanged;
        try
        {
            var done = await client.RepairGearAsync(username);
            return done.Items == 0 ? $"{username} holds and wears nothing."
                : done.Repaired == 0 ? $"Nothing to repair: the {done.Items} thing{(done.Items == 1 ? "" : "s")} {username} holds and wears {(done.Items == 1 ? "is" : "are")} as new."
                : $"{done.Repaired} of the {done.Items} things {username} holds and wears repaired: {string.Join(", ", done.Names)}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not repair them: " + ex.Message;
        }
    }

    // ---- a player's needs (bridge v16) ----

    /// <summary>Why what came with bridge v16 (a player's needs) can't be done now (null: it can).</summary>
    public string? V16Problem =>
        _client is null || !IsConnected ? "This is done through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 16 ? $"This needs bridge v16 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>Takes a player's hunger, thirst, tiredness and the like away, after asking. Returns what happened.</summary>
    internal async Task<string?> RestoreNeedsAsync(string username)
    {
        if (V16Problem is { } problem)
            return problem;
        var client = _client!;
        if (_main.Confirm?.Invoke($"Take the needs of {username} away?\n\n"
                + "Hunger, thirst, tiredness, exertion, stress, panic, boredom, unhappiness, pain, drunkenness, sickness and a cold go to nothing. "
                + "Wounds stay: that is Heal completely.") != true)
            return null;
        if (!ReferenceEquals(client, _client))
            return ConnectionChanged;
        try
        {
            var (_, cold) = await client.RestoreNeedsAsync(username);
            return $"{username} is fed, rested and calm." + (cold ? " Their cold is gone too." : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not do it: " + ex.Message;
        }
    }

    // ---- bridge v18: objects of the world, generators, how the server is doing, zombies in a box, a line for one player ----

    /// <summary>Why what came with bridge v18 can't be done now (null: it can).</summary>
    public string? V18Problem =>
        _client is null || !IsConnected ? "This is done through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 18 ? $"This needs bridge v18 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>The objects around a player, for their window; null and why not.</summary>
    internal async Task<(BridgeObjects? Objects, string? Problem)> ReadObjectsAsync(string username, int radius, bool floors)
    {
        if (V18Problem is { } problem)
            return (null, problem);
        try
        {
            return (await _client!.ObjectsAsync(username, radius, floors), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, "The bridge could not list the objects: " + ex.Message);
        }
    }

    /// <summary>Removes an object of the world, after asking. Returns what happened (null: not asked for after all).</summary>
    internal async Task<string?> RemoveObjectAsync(BridgeObject o)
    {
        if (V18Problem is { } problem)
            return problem;
        var client = _client!;
        if (_main.Confirm?.Invoke($"Remove {o.Label} at {o.Position}?\n\n"
                + (o.Kind == "floor" ? "It is the floor of that square: without it there is a hole.\n" : "")
                + (o.Items > 0 ? $"The {o.Items} item{(o.Items == 1 ? "" : "s")} in it {(o.Items == 1 ? "is" : "are")} left on the ground.\n" : "")
                + "It goes out of the world for every player, as with the game's own Destroy tile, and can't be brought back.") != true)
            return null;
        if (!ReferenceEquals(client, _client))
            return ConnectionChanged;
        try
        {
            var (removed, dumped) = await client.RemoveObjectAsync(o);
            return $"{removed} removed at {o.Position}" + (dumped > 0 ? $": its {dumped} item{(dumped == 1 ? "" : "s")} left on the ground." : ".");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not remove it: " + ex.Message;
        }
    }

    /// <summary>How far around each player the generators are looked for.</summary>
    public const int GeneratorsRadius = 50;

    public ObservableCollection<BridgeGenerator> Generators { get; } = [];

    /// <summary>"3 generators, 1 low on fuel", or why there are none to show.</summary>
    [ObservableProperty] private string generatorsText = "";

    [RelayCommand]
    private async Task ReadGeneratorsAsync()
    {
        if (V18Problem is { } problem)
        {
            Generators.Clear();
            GeneratorsText = problem;
            return;
        }
        var client = _client!;
        try
        {
            var read = await client.GeneratorsAsync(GeneratorsRadius);
            if (!ReferenceEquals(client, _client))
                return;
            Generators.Clear();
            // the ones that need something first
            foreach (var g in read.Generators.OrderBy(g => g.Fuel ?? 0).ThenBy(g => g.Condition ?? 0))
                Generators.Add(g);
            int low = read.Generators.Count(g => g.IsLowOnFuel), worn = read.Generators.Count(g => g.IsWorn), on = read.Generators.Count(g => g.On);
            GeneratorsText = read.Loaded == 0 ? "Nobody is online: the server only keeps the surroundings of players."
                : read.Generators.Count == 0 ? $"No generators within {read.Radius} squares of the players online."
                : $"{read.Generators.Count} generator{(read.Generators.Count == 1 ? "" : "s")} within {read.Radius} squares of the players online: {on} on"
                    + (low > 0 ? $", {low} low on fuel" : "") + (worn > 0 ? $", {worn} worn out" : "")
                    + $" · read at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            GeneratorsText = "The bridge could not list the generators: " + ex.Message;
        }
    }

    async Task GeneratorsActionAsync(string? confirm, Func<BridgeClient, Task<string>> action)
    {
        if (V18Problem is { } problem)
        {
            StatusText = problem;
            return;
        }
        var client = _client!;
        if (confirm is not null && _main.Confirm?.Invoke(confirm) != true)
            return;
        if (!ReferenceEquals(client, _client))
        {
            StatusText = ConnectionChanged;
            return;
        }
        string result;
        try
        {
            result = await action(client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = "The bridge could not do it: " + ex.Message;
        }
        await ReadGeneratorsAsync();
        StatusText = result;
    }

    // one generator, and the click says which: no question
    Task OneGeneratorAsync(BridgeGenerator? g, BridgeClient.GeneratorAction action, Func<BridgeGenerator, string> done) =>
        g is null ? Task.CompletedTask : GeneratorsActionAsync(null, async c => done(await c.GeneratorAsync(g, action)));

    [RelayCommand]
    private Task RefuelGeneratorAsync(BridgeGenerator? g) =>
        OneGeneratorAsync(g, BridgeClient.GeneratorAction.Refuel, after => $"Generator at {after.Position} refuelled: {after.FuelText}.");

    [RelayCommand]
    private Task RepairGeneratorAsync(BridgeGenerator? g) =>
        OneGeneratorAsync(g, BridgeClient.GeneratorAction.Repair, after => $"Generator at {after.Position} repaired: {after.ConditionText}.");

    [RelayCommand]
    private Task SwitchGeneratorAsync(BridgeGenerator? g) =>
        OneGeneratorAsync(g, g is { On: true } ? BridgeClient.GeneratorAction.Off : BridgeClient.GeneratorAction.On,
            after => $"Generator at {after.Position} turned {(after.On ? "on" : "off")}.");

    [RelayCommand]
    private Task ConnectGeneratorAsync(BridgeGenerator? g) =>
        OneGeneratorAsync(g, g is { Connected: true } ? BridgeClient.GeneratorAction.Disconnect : BridgeClient.GeneratorAction.Connect,
            after => $"Generator at {after.Position} {(after.Connected ? "connected to the building" : "disconnected")}.");

    [RelayCommand]
    private Task RefuelAllGeneratorsAsync() => GeneratorsActionAsync(
        $"Fill the tank of every generator within {GeneratorsRadius} squares of the players online?",
        async c =>
        {
            int filled = await c.RefuelGeneratorsAsync(GeneratorsRadius);
            return filled == 0 ? "No generator needed fuel." : $"{filled} generator{(filled == 1 ? "" : "s")} refuelled.";
        });

    /// <summary>A second of the server: the longest wait between two updates in it, as a bar.</summary>
    public sealed record PerfBar(double Height, string Color, string Tip);

    public ObservableCollection<PerfBar> PerfBars { get; } = [];

    /// <summary>"9.8 updates a second, longest wait 180 ms…", or why there is nothing to show.</summary>
    [ObservableProperty] private string perfText = "";

    readonly DispatcherTimer _perfTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    bool _perfReading;

    /// <summary>Read again every few seconds while its section is open.</summary>
    internal void WatchPerf(bool watch)
    {
        _perfTimer.Tick -= PerfTimer_Tick;
        _perfTimer.Stop();
        if (!watch)
            return;
        _perfTimer.Tick += PerfTimer_Tick;
        _perfTimer.Start();
        _ = ReadPerfAsync();
    }

    void PerfTimer_Tick(object? sender, EventArgs e) => _ = ReadPerfAsync();

    [RelayCommand]
    private async Task ReadPerfAsync()
    {
        if (V18Problem is { } problem)
        {
            PerfBars.Clear();
            PerfText = problem;
            return;
        }
        if (_perfReading)
            return;
        _perfReading = true;
        var client = _client!;
        try
        {
            var perf = await client.PerfAsync();
            if (!ReferenceEquals(client, _client))
                return;
            PerfBars.Clear();
            for (int i = 0; i < perf.Seconds.Count; i++)
            {
                var second = perf.Seconds[i];
                // nothing at all in a second: it stood still, whatever the wait noted after it
                bool still = second.Ticks == 0;
                double height = still ? PerfBarHeight : Math.Max(2, Math.Min(1, second.Worst / 1000.0) * PerfBarHeight);
                string color = still || second.Worst > 500 ? "#E5533D" : second.Worst > 200 ? "#F2C14E" : "#4CAF50";
                string tip = $"{perf.Seconds.Count - i} s ago: " + (still ? "no update at all" : $"{second.Ticks} update{(second.Ticks == 1 ? "" : "s")}, longest wait {second.Worst} ms");
                PerfBars.Add(new PerfBar(height, color, tip));
            }
            PerfText = PerfSummary(perf);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            PerfText = "The bridge could not say how the server is doing: " + ex.Message;
        }
        finally
        {
            _perfReading = false;
        }
    }

    /// <summary>The height of the bars, in pixels: a wait of a second or more fills it.</summary>
    public const double PerfBarHeight = 44;

    static string PerfSummary(BridgePerf perf)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (perf.Seconds.Count == 0)
            return "The bridge has only just begun to take notes: read again in a few seconds.";
        var parts = new List<string>
        {
            perf.AverageTicks.ToString("0.0", inv) + " updates a second" + (perf.Target is { } target ? $" (the game aims at {target})" : ""),
            "longest wait " + (perf.WorstWait >= 1000 ? (perf.WorstWait / 1000.0).ToString("0.0", inv) + " s" : perf.WorstWait + " ms"),
        };
        int slow = perf.WaitsOver(500) + perf.Seconds.Count(s => s.Ticks == 0);
        parts.Add(slow == 0 ? "no second with a wait over half a second" : $"{slow} second{(slow == 1 ? "" : "s")} with a wait over half a second");
        parts.Add($"{perf.Zombies ?? 0} zombies, {perf.Vehicles ?? 0} vehicles, {perf.Players ?? 0} player{(perf.Players == 1 ? "" : "s")}");
        if (perf.MemoryUsed is > 0 && perf.MemoryMax is > 0)
        {
            // the game gives megabytes
            parts.Add($"memory {(perf.MemoryUsed.Value / 1000).ToString("0.0", inv)} of {(perf.MemoryMax.Value / 1000).ToString("0.0", inv)} GB");
        }
        return string.Join(" · ", parts) + $" · the last {perf.Seconds.Count} s, read at {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>
    /// Removes the zombies of a box around a square, after asking. The removal is RCON's own removezombies (it
    /// tells the players' games), one floor at a time; with bridge v18 they are counted first and every floor
    /// that has some is done, without it only the floor given. Returns what happened.
    /// </summary>
    internal async Task<string?> RemoveZombiesAsync(int x, int y, int floor, int radius, string where)
    {
        if (!_main.IsSessionActive)
            return "Removing zombies is an RCON command: connect to the server first.";
        radius = Math.Clamp(radius, 1, SpiffoCON.Core.Commands.EventCommands.MaxRadius);
        IReadOnlyList<int> floors = [floor];
        int? counted = null;
        if (V18Problem is null)
        {
            try
            {
                var found = await _client!.ZombiesInAsync(x, y, radius);
                if (found.Total == 0)
                    return $"No zombies within {radius} squares of {where}.";
                counted = found.Total;
                floors = found.Floors.Select(f => f.Z).Distinct().OrderBy(z => z).ToList();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // not counted: done as without the bridge
            }
        }
        string onFloors = floors.Count == 1 ? $"floor {floors[0]}" : "floors " + string.Join(", ", floors);
        if (_main.Confirm?.Invoke(counted is { } n
                ? $"Remove the {n} zombie{(n == 1 ? "" : "s")} within {radius} squares of {where}?\n\nOn {onFloors}. They are gone for good."
                : $"Remove the zombies within {radius} squares of {where}?\n\nOn {onFloors} only, and not counted: with bridge v18 every floor is done and they are counted first.") != true)
            return null;
        foreach (int z in floors)
        {
            var reply = await _main.RunAsync(SpiffoCON.Core.Commands.EventCommands.RemoveZombiesAt(x, y, z, radius));
            if (reply is null)
                return "The command failed: see the console.";
            if (SpiffoCON.Core.Commands.EventCommands.Interpret(reply) == SpiffoCON.Core.Commands.CommandOutcome.Failed)
                return "The server refused: " + reply.Trim();
        }
        return counted is { } removed
            ? $"{removed} zombie{(removed == 1 ? "" : "s")} removed within {radius} squares of {where} ({onFloors})."
            : $"Zombies within {radius} squares of {where} removed ({onFloors}).";
    }

    /// <summary>Writes a line to one player alone. Returns the line as it went (null: it did not), and what to tell.</summary>
    internal async Task<(BridgeChat? Sent, string Status)> SendTellAsync(string username, string author, string text)
    {
        if (V18Problem is { } problem)
            return (null, "Writing to one player needs bridge v18: " + problem);
        try
        {
            var chat = await _client!.TellAsync(username, author, text);
            return (chat, $"Written to {username} alone as {chat.Author}{(chat.Cut ? ": too long, the end was cut" : "")}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, "The bridge could not write to them: " + ex.Message);
        }
    }

    // ---- chat (bridge v17) ----

    /// <summary>Why a line can't be written in the chat now (null: it can).</summary>
    public string? V17Problem =>
        _client is null || !IsConnected ? "Writing in the chat is done through the SpiffoCON Bridge: connect it in the Bridge tab, or choose Broadcast."
        : BridgeVersion < 17 ? $"Writing in the chat needs bridge v17 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted. Broadcast works without it."
        : null;

    /// <summary>Writes a line in the General chat under a name. Returns the line as it went (null: it did not), and what to tell.</summary>
    internal async Task<(BridgeChat? Sent, string Status)> SendChatAsync(string author, string text)
    {
        if (V17Problem is { } problem)
            return (null, problem);
        try
        {
            var chat = await _client!.ChatAsync(author, text);
            return (chat, chat.Players == 0 ? $"Written as {chat.Author}, but nobody is online to read it."
                : $"Written in the chat as {chat.Author}{(chat.Cut ? ": too long, the end was cut" : "")}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, "The bridge could not write in the chat: " + ex.Message);
        }
    }

    // ---- traits (bridge v16) ----

    /// <summary>Every trait of the game with whether the player has it, for the character window; null and why not.</summary>
    internal async Task<(IReadOnlyList<BridgeTrait>? Traits, string? Problem)> ReadTraitsAsync(string username)
    {
        if (V16Problem is { } problem)
            return (null, problem);
        try
        {
            return (await _client!.TraitsAsync(username), null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, "The bridge could not list the traits: " + ex.Message);
        }
    }

    /// <summary>Gives a trait to a player or takes it away, after asking. Returns what happened.</summary>
    internal async Task<string?> SetTraitAsync(string username, BridgeTrait trait, bool add)
    {
        if (V16Problem is { } problem)
            return problem;
        var client = _client!;
        if (_main.Confirm?.Invoke((add ? $"Give {username} the trait {trait.Name}?" : $"Take the trait {trait.Name} away from {username}?")
                + "\n\nAs the game's own admin window does it: the trait and the experience boost that goes with it. "
                + "Traits that exclude each other (Strong and Weak) are not checked.") != true)
            return null;
        if (!ReferenceEquals(client, _client))
            return ConnectionChanged;
        try
        {
            string name = await client.SetTraitAsync(username, trait.Id, add);
            return add ? $"{username} has the trait {name} now." : $"{username} has lost the trait {name}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not change the trait: " + ex.Message;
        }
    }

    // ---- animals (bridge v15) ----

    /// <summary>How far around each player the animals and the troughs are looked for: as far as the bridge goes.</summary>
    public const int AnimalsRadius = 100;

    public ObservableCollection<BridgeAnimal> Animals { get; } = [];

    /// <summary>"12 animals, 3 hungry, 1 thirsty", or why there are none to show.</summary>
    [ObservableProperty] private string animalsText = "";

    [RelayCommand]
    private async Task ReadAnimalsAsync()
    {
        if (V15Problem is { } problem)
        {
            Animals.Clear();
            AnimalsText = problem;
            return;
        }
        var client = _client!;
        try
        {
            var read = await client.AnimalsAsync(AnimalsRadius);
            if (!ReferenceEquals(client, _client))
                return;
            Animals.Clear();
            // the ones that need something first
            foreach (var a in read.Animals.OrderByDescending(a => Math.Max(a.Hunger ?? 0, a.Thirst ?? 0)).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                Animals.Add(a);
            int hungry = read.Animals.Count(a => a.IsHungry), thirsty = read.Animals.Count(a => a.IsThirsty), pregnant = read.Animals.Count(a => a.Pregnant == true),
                produce = read.Animals.Count(a => a.HasProduce);
            AnimalsText = read.Loaded == 0 ? "Nobody is online: the server only keeps the surroundings of players."
                : read.Animals.Count == 0 ? $"No animals of the players within {read.Radius} squares of the players online (wild ones, and those in a hutch or a trailer, are not listed)."
                : $"{read.Animals.Count} animal{(read.Animals.Count == 1 ? "" : "s")} within {read.Radius} squares of the players online"
                    + (hungry + thirsty == 0 ? ", none hungry or thirsty" : $": {hungry} hungry, {thirsty} thirsty")
                    + (pregnant > 0 ? $", {pregnant} pregnant" : "")
                    + (produce > 0 ? $", {produce} to milk or shear" : "")
                    + $" · read at {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AnimalsText = "The bridge could not list the animals: " + ex.Message;
        }
    }

    async Task AnimalsActionAsync(string? confirm, Func<BridgeClient, Task<string>> action)
    {
        if (V15Problem is { } problem)
        {
            StatusText = problem;
            return;
        }
        var client = _client!;
        if (confirm is not null && _main.Confirm?.Invoke(confirm) != true)
            return;
        if (!ReferenceEquals(client, _client))
        {
            StatusText = ConnectionChanged;
            return;
        }
        string result;
        try
        {
            result = await action(client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            result = "The bridge could not do it: " + ex.Message;
        }
        await ReadAnimalsAsync();
        StatusText = result;
    }

    // bridge v21: an animal close to giving birth told once. The list goes through a lot of squares: every quarter of an hour,
    // and only with the notification on
    static readonly TimeSpan BirthsEvery = TimeSpan.FromMinutes(15);
    DateTime _birthsLookAt;
    readonly HashSet<int> _birthsTold = [];

    async Task LookForBirthsAsync(BridgeClient client)
    {
        _birthsLookAt = DateTime.UtcNow + BirthsEvery;
        try
        {
            var read = await client.AnimalsAsync(AnimalsRadius);
            if (!ReferenceEquals(client, _client))
                return;
            var due = read.Animals.Where(a => a.IsDue && a.Id is not null).ToList();
            // (no longer due: its next one is told too)
            _birthsTold.IntersectWith(due.Select(a => a.Id!.Value));
            foreach (var a in due.Where(a => _birthsTold.Add(a.Id!.Value)))
                _main.Notify(NotificationKind.Births, "An animal is close to giving birth",
                    $"{a.Name ?? a.KindText} ({a.KindText}), {a.ExpectingText}, at {a.X}, {a.Y}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a look nobody asked for; the next refresh tells what is wrong
        }
    }

    // ---- black box (bridge v21) ----

    /// <summary>What the bridge wrote of the run of the server before this one, to show; empty when it has nothing.</summary>
    internal async Task<List<string>> BlackBoxOfRunBeforeAsync()
    {
        if (_client is not { } client)
            return [];
        try { return BlackBox.Describe(BlackBox.RunBefore(await client.BlackBoxAsync())); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return []; }
    }

    [RelayCommand]
    private async Task ShowBlackBoxAsync()
    {
        var lines = await BlackBoxOfRunBeforeAsync();
        if (lines.Count == 0)
        {
            StatusText = "Nothing in the black box of the run before this one: it needs the bridge's files reached, and bridge v21 on the server during that run.";
            return;
        }
        _main.Log(ConsoleKind.Info, "The last minutes of the run of the server before this one, from the bridge's black box (times of this PC):\n"
            + string.Join("\n", lines.Select(l => "  " + l)));
        StatusText = "Black box written in the Console tab.";
    }

    /// <summary>One animal fed and watered (no question: one animal, and the click says which).</summary>
    [RelayCommand]
    private Task FeedAnimalAsync(BridgeAnimal? animal) => animal?.Id is not int id ? Task.CompletedTask : AnimalsActionAsync(null,
        async c => await c.FeedAnimalsAsync(AnimalsRadius, id) > 0 ? $"{animal.Name ?? animal.KindText} fed and watered." : "Nothing done.");

    [RelayCommand]
    private Task FeedAllAnimalsAsync() => AnimalsActionAsync(
        $"Feed and water every animal of the players within {AnimalsRadius} squares of the players online?\n\n"
            + "Their hunger and thirst go to nothing. The feed and the water in the troughs are not touched.",
        async c =>
        {
            int fed = await c.FeedAnimalsAsync(AnimalsRadius);
            return fed == 0 ? "No animals to feed there." : $"{fed} animal{(fed == 1 ? "" : "s")} fed and watered.";
        });

    [RelayCommand]
    private Task FillTroughsAsync() => AnimalsActionAsync(
        $"Fill with water the feeding troughs within {AnimalsRadius} squares of the players online?\n\n"
            + "A trough with feed in it holds no water and is left as it is.",
        async c =>
        {
            var (filled, withFeed, full) = await c.FillTroughsAsync(AnimalsRadius);
            return (filled == 0 ? "No trough filled" : $"{filled} trough{(filled == 1 ? "" : "s")} filled with water")
                + (full > 0 ? $", {full} full already" : "")
                + (withFeed > 0 ? $", {withFeed} left (feed in {(withFeed == 1 ? "it" : "them")}, or no water part: Fix containers and stations…)" : "") + ".";
        });

    /// <summary>Why the containers that lost their liquid part can't be looked for now (null: they can): bridge v14.</summary>
    public string? V14Problem =>
        _client is null || !IsConnected ? "This is done through the SpiffoCON Bridge: connect it in the Bridge tab."
        : BridgeVersion < 14 ? $"This needs bridge v14 (the server runs v{BridgeVersion}): the Workshop item has to be updated and the server restarted."
        : null;

    /// <summary>How far around each player the containers put down or stored are looked at.</summary>
    public const int FluidsRadius = 50;

    /// <summary>What Fix containers and stations is for, for its tooltips.</summary>
    public const string FixFluidsTip =
        "Buckets, bottles and pots that lost the part that holds liquids: after a server crash one that was put down shows as a plain "
        + "\"Bucket\" (not \"Empty Bucket\") and can't be filled any more. And built things that lost a part: troughs and rain collectors "
        + "that take no water, drying racks, workbenches and furnaces nobody can use (these need bridge v19). Counted first, then the "
        + "containers are replaced with new, empty ones where they are, and the built things get their parts back in place, empty";

    /// <summary>"3 containers can't be filled any more and 2 built things can't be used": what a look found.</summary>
    static string BrokenSummary(BridgeFluidFix count, int found)
    {
        int built = Math.Min(count.Built, found), items = found - built;
        var parts = new List<string>();
        if (items > 0)
            parts.Add($"{items} container{(items == 1 ? "" : "s")} lost the part that holds liquids and can't be filled any more");
        if (built > 0)
            parts.Add($"{built} built thing{(built == 1 ? "" : "s")} (troughs, rain collectors, drying racks, workbenches) lost a part and can't be used as {(built == 1 ? "it" : "they")} should");
        return string.Join("; ", parts);
    }

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
        try
        {
            var count = await client.FixFluidsAsync(apply: false, FluidsRadius, username);
            if (count.Players == 0)
                return "Nobody is online: the server only keeps the surroundings of players, and what they carry.";
            if (count.Found == 0)
                return $"No container or built thing has lost a part: looked at what {who} {carry} and at {FluidsRadius} squares around {around}"
                    + (BridgeVersion < 19 ? " (the stations, as drying racks and workbenches, only with bridge v19)." : ".");
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
            if (count.Built > 0)
                where.Add($"{count.Built} built (troughs, rain collectors, drying racks, workbenches...)");
            // what was in a lost part (the grain on a rack, the water in a collector) went with it
            const string builtBack = "built things stay where they are and get the missing parts back, empty";
            string question = $"{BrokenSummary(count, count.Found)}: {count.Describe()}.\n\n"
                + $"{string.Join(", ", where)}; looked at what {who} {carry} and at {FluidsRadius} squares around {around}.\n\n"
                + (count.Built == count.Found ? "Give " + (count.Found == 1 ? "it its" : "each its") + " parts back? Nothing is replaced: " + builtBack + "."
                    : (count.Found - count.Built == 1 ? "Replace the container with a new, empty one of the same type, in the same place?"
                        : "Replace each container with a new, empty one of the same type, in the same place?")
                        + (count.Built > 0 ? " The " + builtBack + "." : ""))
                + (count.Skipped > 0 ? $"\n\n{count.Skipped} worn or attached (a bottle on a belt) will be left: the player has to take it off first." : "");
            if (_main.Confirm?.Invoke(question) != true)
                return null;
            if (!ReferenceEquals(client, _client))
                return ConnectionChanged;
            var done = await client.FixFluidsAsync(apply: true, FluidsRadius, username);
            FluidsNotice = "";
            return (done.Fixed == 0 ? "Nothing fixed." : $"{done.Fixed} fixed ({done.Describe()}).")
                + (done.Skipped > 0 ? $" {done.Skipped} worn or attached left as {(done.Skipped == 1 ? "it is" : "they are")}." : "")
                + (done.Failed > 0 ? $" {done.Failed} could not be put right: the game made no whole one of that type." : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "The bridge could not look at the containers: " + ex.Message;
        }
    }

    /// <summary>Fix containers and stations, for everybody online (the Bridge tab's button).</summary>
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
