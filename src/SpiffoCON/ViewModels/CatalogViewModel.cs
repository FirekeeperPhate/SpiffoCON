using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Catalog;
using SpiffoCON.Core.Commands;
using SpiffoCON.Core.Files;
using SpiffoCON.Services;

namespace SpiffoCON.ViewModels;

/// <summary>Items and vehicles (base game + the server's mods) and the give/spawn actions.</summary>
public sealed partial class CatalogViewModel : ObservableObject
{
    public const string AllSources = "All sources";
    public const string VanillaOnly = "Base game";
    public const string ModsOnly = "All mods";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    readonly MainViewModel _main;
    readonly CatalogService _service;
    readonly DispatcherTimer _searchDelay;
    IReadOnlyList<CatalogEntry> _entries = [];
    string[] _searchWords = [];

    public CatalogViewModel(MainViewModel main)
    {
        _main = main;
        // mod cache and SteamCMD; SPIFFOCON_DATA_DIR overrides it (UI tests)
        var dataFolder = Environment.GetEnvironmentVariable("SPIFFOCON_DATA_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpiffoCON");
        _service = new CatalogService(dataFolder, Http);
        _searchDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            ApplyFilter();
        };
        _main.OnlinePlayersChanged += OnPlayersChanged;
        _ = LoadVanillaAsync();
    }

    // ---- list ----

    [ObservableProperty] private ICollectionView? view;
    [ObservableProperty] private string search = "";
    [ObservableProperty] private int kindFilter; // 0 all, 1 items, 2 vehicles
    [ObservableProperty] private string sourceFilter = AllSources;
    [ObservableProperty] private bool showHidden;
    [ObservableProperty] private string countText = "";
    [ObservableProperty] private string statusText = "Base game catalog. Connect and press Load from server to add the server's mods.";
    [ObservableProperty] private bool isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsItemSelected), nameof(IsVehicleSelected), nameof(HasSelection))]
    private CatalogEntry? selected;

    public bool HasSelection => Selected is not null;
    public bool IsItemSelected => Selected?.Kind == CatalogKind.Item;
    public bool IsVehicleSelected => Selected?.Kind == CatalogKind.Vehicle;

    public ObservableCollection<string> SourceOptions { get; } = [AllSources, VanillaOnly, ModsOnly];

    partial void OnSearchChanged(string value)
    {
        _searchWords = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    partial void OnKindFilterChanged(int value) => ApplyFilter();
    partial void OnSourceFilterChanged(string value) => ApplyFilter();
    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    async Task LoadVanillaAsync()
    {
        try
        {
            var entries = await Task.Run(() => _service.LoadVanilla());
            SetEntries(entries);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
        {
            StatusText = "Could not load the base game catalog: " + ex.Message;
        }
    }

    void SetEntries(IReadOnlyList<CatalogEntry> entries)
    {
        _entries = entries;
        var sources = entries.Where(e => !e.Source.IsVanilla).Select(e => e.Source.Label).Distinct().Order(StringComparer.CurrentCultureIgnoreCase);
        var keep = SourceFilter;
        SourceOptions.Clear();
        foreach (var s in new[] { AllSources, VanillaOnly, ModsOnly }.Concat(sources))
            SourceOptions.Add(s);
        SourceFilter = SourceOptions.Contains(keep) ? keep : AllSources;

        var view = new ListCollectionView(entries.ToList()) { Filter = Matches };
        View = view;
        UpdateCount();
    }

    void ApplyFilter()
    {
        View?.Refresh();
        UpdateCount();
    }

    void UpdateCount()
    {
        int shown = View?.Cast<object>().Count() ?? 0;
        int items = _entries.Count(e => e.Kind == CatalogKind.Item);
        CountText = $"{shown} shown · {items} items · {_entries.Count - items} vehicles";
    }

    bool Matches(object o)
    {
        var e = (CatalogEntry)o;
        if (e.Hidden && !ShowHidden)
            return false;
        if (KindFilter == 1 && e.Kind != CatalogKind.Item || KindFilter == 2 && e.Kind != CatalogKind.Vehicle)
            return false;
        switch (SourceFilter)
        {
            case AllSources:
                break;
            case VanillaOnly:
                if (!e.Source.IsVanilla) return false;
                break;
            case ModsOnly:
                if (e.Source.IsVanilla && e.ChangedBy is null) return false;
                break;
            default:
                if (e.Source.Label != SourceFilter && e.ChangedBy?.Label != SourceFilter) return false;
                break;
        }
        foreach (var word in _searchWords)
        {
            if (!e.DisplayName.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                && !e.FullType.Contains(word, StringComparison.OrdinalIgnoreCase)
                && !e.Category.Contains(word, StringComparison.CurrentCultureIgnoreCase))
                return false;
        }
        return true;
    }

    // ---- loading the server's mods ----

    // ---- base-game icons ----

    bool _loadedFromServer;

    /// <summary>Asks for the Project Zomboid folder; set by the view.</summary>
    public Func<string?>? PickGameFolder { get; set; }

    public bool HasVanillaIcons => Directory.Exists(_service.VanillaIconFolder) && Directory.EnumerateFiles(_service.VanillaIconFolder, "Item_*.png").Any();

    /// <summary>
    /// Takes the item icons from the user's own game install: the dedicated server has none, and
    /// the game's art isn't SpiffoCON's to ship.
    /// </summary>
    [RelayCommand]
    private async Task ExtractIconsAsync()
    {
        var packs = IconExtractor.FindTexturePacks();
        if (packs is null)
        {
            var folder = PickGameFolder?.Invoke();
            if (folder is null)
                return;
            packs = IconExtractor.ResolveTexturePacks(folder);
            if (packs is null)
            {
                StatusText = "No media\\texturepacks\\*.pack there: choose the ProjectZomboid folder of your game install.";
                return;
            }
        }

        IsLoading = true;
        try
        {
            var progress = new Progress<string>(s => StatusText = s);
            int count = await Task.Run(() => IconExtractor.Extract(packs, _service.VanillaIconFolder, progress));
            OnPropertyChanged(nameof(HasVanillaIcons));
            if (_loadedFromServer && _main.IsSessionActive)
                await LoadFromServerAsync();
            else
                await LoadVanillaAsync();
            StatusText = count > 0
                ? $"{count} base-game icons taken from {packs}."
                : "No item icons found in those texture packs.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "Could not read the texture packs: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public ObservableCollection<ModSourceReport> ModSources { get; } = [];
    [ObservableProperty] private string modSummary = "";

    [RelayCommand]
    private async Task LoadFromServerAsync()
    {
        if (!_main.IsSessionActive)
        {
            StatusText = "Connect to the server first: the mod list comes from its options.";
            return;
        }

        IsLoading = true;
        try
        {
            StatusText = "Reading the server's mod list (showoptions)...";
            var reply = await _main.RunAsync("showoptions", logReply: false);
            if (reply is null)
            {
                StatusText = "showoptions failed: see the console.";
                return;
            }
            var options = ServerOptions.Parse(reply);
            if (options.Count == 0)
            {
                StatusText = "The server's reply to showoptions was not a list of options.";
                return;
            }

            var sftp = _main.CurrentSftpSettings();
            string? remoteWorkshop = _main.Profile.SftpWorkshopFolder;
            if (sftp is not null && remoteWorkshop is null && options.WorkshopItems.Count > 0)
            {
                StatusText = "Looking for the mod folder over SFTP...";
                try
                {
                    var probe = await SftpProbe.RunAsync(sftp);
                    remoteWorkshop = probe.WorkshopFolders.OrderByDescending(w => w.ItemCount).FirstOrDefault()?.Path;
                    _main.Profile.SftpHostKey = probe.HostKeyFingerprint;
                    _main.Profile.SftpWorkshopFolder = remoteWorkshop;
                    _main.SaveProfile();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    StatusText = "SFTP: " + ex.Message;
                }
            }

            var dispatcher = Application.Current.Dispatcher;
            var progress = new Progress<string>(s => StatusText = s);
            var loadOptions = new CatalogLoadOptions
            {
                Sftp = sftp,
                RemoteWorkshopFolder = remoteWorkshop,
                ConfirmSteamCmd = (bytes, items) => dispatcher.InvokeAsync(() => _main.Confirm?.Invoke(
                    $"{items.Count} mods are not available over SFTP or in your Steam folder.\n\n" +
                    $"Download them with SteamCMD ({FormatSize(bytes)})? SteamCMD downloads whole mods, " +
                    "models and sounds included; SpiffoCON keeps them in its cache for next time.") ?? true).Task,
            };
            var result = await Task.Run(() => _service.LoadAsync(options, loadOptions, progress));

            SetEntries(result.Entries);
            _loadedFromServer = true;
            ModSources.Clear();
            foreach (var s in result.Sources)
                ModSources.Add(s);
            ModSummary = Summarize(result, options);
            StatusText = $"Loaded {result.Entries.Count(e => !e.Source.IsVanilla)} entries from {options.Mods.Count - result.MissingMods.Count} mods.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "Loading failed: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    static string Summarize(CatalogResult result, ServerOptions options)
    {
        var parts = new List<string>
        {
            $"{options.Mods.Count} mods in {options.WorkshopItems.Count} workshop items",
        };
        foreach (var g in result.Sources.GroupBy(s => s.Source).OrderBy(g => g.Key))
        {
            parts.Add(g.Key switch
            {
                ModFileSource.Sftp => $"{g.Count()} via SFTP",
                ModFileSource.LocalSteam => $"{g.Count()} from your Steam folder",
                ModFileSource.SteamCmd => $"{g.Count()} via SteamCMD",
                _ => $"{g.Count()} not found",
            });
        }
        var text = string.Join(" · ", parts);
        if (result.MissingMods.Count > 0)
            text += "\nMods without files: " + string.Join(", ", result.MissingMods);
        if (result.Warnings.Count > 0)
            text += "\n" + string.Join("\n", result.Warnings);
        return text;
    }

    static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{Math.Max(1, bytes >> 20)} MB";

    // ---- actions ----

    public ObservableCollection<string> Players => _main.OnlinePlayers;
    [ObservableProperty] private string player = "";
    [ObservableProperty] private int quantity = 1;
    [ObservableProperty] private string actionResult = "";

    [RelayCommand]
    private Task RefreshPlayersAsync() => _main.RefreshPlayersAsync();

    void OnPlayersChanged(object? sender, EventArgs e)
    {
        if (!Players.Contains(Player))
            Player = Players.FirstOrDefault() ?? Player;
    }

    [RelayCommand]
    private async Task GiveItemAsync()
    {
        if (Selected is not { Kind: CatalogKind.Item } item || !CheckTarget())
            return;
        var reply = await _main.RunAsync(PlayerCommands.AddItem(Player.Trim(), item.FullType, Quantity));
        ActionResult = Describe(reply, reply is null ? CommandOutcome.Failed : PlayerCommands.InterpretAddItem(reply),
            $"Gave {Math.Max(1, Quantity)} × {item.DisplayName} to {Player.Trim()}.");
    }

    [RelayCommand]
    private async Task SpawnVehicleAsync()
    {
        if (Selected is not { Kind: CatalogKind.Vehicle } vehicle || !CheckTarget())
            return;
        var reply = await _main.RunAsync(PlayerCommands.AddVehicle(vehicle.FullType, Player.Trim()));
        ActionResult = Describe(reply, reply is null ? CommandOutcome.Failed : PlayerCommands.InterpretAddVehicle(reply),
            $"Spawned {vehicle.DisplayName} next to {Player.Trim()}.");
    }

    [RelayCommand]
    private void CopyId()
    {
        if (Selected is not null)
            Clipboard.SetText(Selected.FullType);
    }

    bool CheckTarget()
    {
        if (!_main.IsSessionActive)
            ActionResult = "Not connected.";
        else if (string.IsNullOrWhiteSpace(Player))
            ActionResult = "Choose a player.";
        else
            return true;
        return false;
    }

    static string Describe(string? reply, CommandOutcome outcome, string success) => outcome switch
    {
        CommandOutcome.Success => success,
        CommandOutcome.Failed => reply is null ? "Failed: see the console." : "Failed: " + reply.Trim(),
        _ => "Sent; server replied: " + (reply?.Trim() is { Length: > 0 } r ? r : "(nothing)"),
    };
}
