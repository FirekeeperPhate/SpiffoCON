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
    bool _busy;

    public BridgeViewModel(MainViewModel main)
    {
        _main = main;
        _timer.Tick += async (_, _) => await RefreshAsync();
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
        if (AutoRefresh && IsConnected)
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
        var text = $"Mods: {BridgeMod.ModId}" + (WorkshopId is null ? "" : $"\nWorkshopItems: {WorkshopId}");
        Clipboard.SetText(text);
        StatusText = "Copied: add them to the server's mod list (Mods= and WorkshopItems=) and restart it.";
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
                var probe = await SftpProbe.RunAsync(sftp);
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
        _client?.Files.Dispose();
        _client = new BridgeClient(files);
        Source = files.Description;
        StatusText = "Asking the bridge...";
        try
        {
            var started = DateTime.Now;
            int version = await _client.PingAsync();
            BridgeVersion = version;
            IsConnected = true;
            StatusText = $"Bridge v{version} answered in {(DateTime.Now - started).TotalSeconds:0.0} s."
                + (CanAct ? "" : " Admin actions need bridge v2: upload the new version to the Workshop and restart the server.");
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            IsConnected = false;
            StatusText = ex.Message;
        }
        UpdateTimer();
    }

    // ---- data ----

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_client is null || _busy)
            return;
        _busy = true;
        try
        {
            var snapshot = await _client.SnapshotAsync();
            var w = snapshot.World;
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
            StatusText = $"Updated at {DateTime.Now:HH:mm:ss}" + (AutoRefresh ? " · every 15 s" : "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    async Task LoadInventoryAsync(string username)
    {
        if (_client is null)
            return;
        InventoryTitle = $"Inventory of {username}: loading...";
        Inventory.Clear();
        try
        {
            var items = await _client.InventoryAsync(username);
            foreach (var item in items)
                Inventory.Add(item);
            InventoryTitle = $"Inventory of {username}: {items.Sum(i => i.Count)} items";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            InventoryTitle = $"Inventory of {username}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyPosition(BridgePlayer? player)
    {
        if (player?.X is null)
            return;
        Clipboard.SetText($"{player.X},{player.Y},{player.Z}");
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
        try
        {
            StatusText = await action(_client);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "Failed: " + ex.Message;
        }
        await RefreshAsync();
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
        if (item is null || SelectedPlayer is not { } player)
            return Task.CompletedTask;
        return ActAsync(
            $"Remove {count} × {item.Name} ({item.FullType}) from {player.Username}'s inventory?\n\nWorn clothes and attached items are not touched.",
            async c =>
            {
                var (removed, skipped) = await c.RemoveItemAsync(player.Username, item.FullType, count);
                return $"Removed {removed} × {item.Name} from {player.Username}." + (skipped > 0 ? $" {skipped} worn or attached left in place." : "");
            },
            reloadInventory: true);
    }

    [RelayCommand]
    private Task RepairVehicleAsync(BridgeVehicle? v) => v?.Id is not int id ? Task.CompletedTask : ActAsync(
        $"Repair {v.Script} #{id} completely?",
        async c => { await c.RepairVehicleAsync(id); return $"{v.Script} #{id} repaired."; });

    [RelayCommand]
    private Task RefuelVehicleAsync(BridgeVehicle? v) => v?.Id is not int id ? Task.CompletedTask : ActAsync(
        $"Fill the tank of {v.Script} #{id}?",
        async c => { await c.RefuelVehicleAsync(id); return $"{v.Script} #{id} refuelled."; });

    [RelayCommand]
    private Task RemoveVehicleAsync(BridgeVehicle? v) => v?.Id is not int id ? Task.CompletedTask : ActAsync(
        $"Remove {v.Script} #{id} from the world for good?" + (v.Driver is { } d ? $"\n\n{d} is driving it." : ""),
        async c => { await c.RemoveVehicleAsync(id); return $"{v.Script} #{id} removed."; });

    public void Close()
    {
        _timer.Stop();
        _client?.Files.Dispose();
    }
}
