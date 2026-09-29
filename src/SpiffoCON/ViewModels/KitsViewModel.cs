using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Catalog;
using SpiffoCON.Core.Commands;
using SpiffoCON.Services;

namespace SpiffoCON.ViewModels;

public sealed partial class KitItemView(KitsViewModel owner, KitItem model) : ObservableObject
{
    public KitItem Model { get; } = model;

    public string FullType => Model.FullType;

    public string Name => Model.Name.Length > 0 ? Model.Name : Model.FullType;

    [ObservableProperty] private int count = Math.Max(1, model.Count);

    /// <summary>From the catalog now loaded (null: no icon, or not in this server's catalog).</summary>
    [ObservableProperty] private string? iconPath;

    /// <summary>False when the catalog now loaded doesn't have it (a mod this server lacks?).</summary>
    [ObservableProperty] private bool known = true;

    partial void OnCountChanged(int value)
    {
        Model.Count = Math.Max(1, value);
        owner.Save();
    }
}

public sealed partial class KitView : ObservableObject
{
    readonly KitsViewModel _owner;

    public KitView(KitsViewModel owner, Kit model)
    {
        _owner = owner;
        Model = model;
        name = model.Name;
        foreach (var item in model.Items)
            Items.Add(new KitItemView(owner, item));
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Summary));
    }

    public Kit Model { get; }

    public ObservableCollection<KitItemView> Items { get; } = [];

    [ObservableProperty] private string name;

    public string Summary => Items.Count == 1 ? "1 item" : $"{Items.Count} items";

    partial void OnNameChanged(string value)
    {
        Model.Name = value;
        _owner.Save();
    }
}

/// <summary>Sets of items (starter kits, event prizes) given in one go to a player or to everyone online.</summary>
public sealed partial class KitsViewModel : ObservableObject
{
    readonly MainViewModel _main;

    public KitsViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var kit in KitStore.Load())
            Kits.Add(new KitView(this, kit));
        SelectedKit = Kits.FirstOrDefault();
        _main.Catalog.EntriesChanged += (_, _) => UpdateIcons();
        _main.OnlinePlayersChanged += (_, _) =>
        {
            // fills the player once, from the first list after connecting; a chosen or typed name
            // is never swapped for another player
            if (OnlinePlayers.Count == 0)
                _playerFilled = false;
            else if (!_playerFilled && string.IsNullOrWhiteSpace(Player))
            {
                Player = OnlinePlayers[0];
                _playerFilled = true;
            }
        };
        UpdateIcons();
    }

    bool _playerFilled;

    public ObservableCollection<KitView> Kits { get; } = [];

    public ObservableCollection<string> OnlinePlayers => _main.OnlinePlayers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKit))]
    private KitView? selectedKit;

    public bool HasKit => SelectedKit is not null;

    [ObservableProperty] private string? player;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToPlayer))]
    private bool everyone;

    public bool ToPlayer
    {
        get => !Everyone;
        set => Everyone = !value;
    }
    [ObservableProperty] private string result = "";

    [RelayCommand]
    private void NewKit()
    {
        var kit = new KitView(this, new Kit { Name = UniqueName("New kit") });
        Kits.Add(kit);
        SelectedKit = kit;
        Save();
    }

    [RelayCommand]
    private void DeleteKit()
    {
        if (SelectedKit is not { } kit || _main.Confirm?.Invoke($"Delete the kit \"{kit.Name}\"?") != true)
            return;
        int index = Kits.IndexOf(kit);
        Kits.Remove(kit);
        SelectedKit = Kits.Count == 0 ? null : Kits[Math.Min(index, Kits.Count - 1)];
        Save();
    }

    [RelayCommand]
    private void RemoveItem(KitItemView? item)
    {
        if (item is null || SelectedKit is not { } kit)
            return;
        kit.Items.Remove(item);
        kit.Model.Items.Remove(item.Model);
        Save();
    }

    /// <summary>Adds a catalog item to a kit (a new one when there is none); returns the kit.</summary>
    internal KitView AddItem(CatalogEntry entry, int count, KitView? kit)
    {
        // a kit deleted in the Kits tab may still be the Catalog's choice
        if (kit is not null && !Kits.Contains(kit))
            kit = null;
        if (kit is null)
        {
            kit = new KitView(this, new Kit { Name = UniqueName("New kit") });
            Kits.Add(kit);
            SelectedKit ??= kit;
        }
        count = Math.Max(1, count);
        if (kit.Items.FirstOrDefault(i => i.FullType.Equals(entry.FullType, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            existing.Count += count;
        }
        else
        {
            var model = new KitItem { FullType = entry.FullType, Name = entry.DisplayName, Count = count };
            kit.Model.Items.Add(model);
            kit.Items.Add(new KitItemView(this, model) { IconPath = entry.IconPath });
        }
        Save();
        return kit;
    }

    [RelayCommand]
    private async Task GiveAsync()
    {
        if (SelectedKit is not { } kit)
            return;
        if (!_main.IsSessionActive)
        {
            Result = "Not connected.";
            return;
        }
        if (kit.Items.Count == 0)
        {
            Result = "The kit is empty: add items from the Catalog tab.";
            return;
        }
        // the target is read before the refresh, which may change the online list
        var chosen = Player?.Trim();
        await _main.RefreshPlayersAsync(quiet: true);
        List<string> players;
        if (Everyone)
        {
            players = [.. OnlinePlayers];
            if (players.Count == 0)
            {
                Result = "Nobody is online.";
                return;
            }
            if (_main.Confirm?.Invoke($"Give \"{kit.Name}\" ({kit.Summary}) to each of the {players.Count} online players?") != true)
                return;
        }
        else if (string.IsNullOrWhiteSpace(chosen))
        {
            Result = "Choose a player.";
            return;
        }
        else
        {
            players = [chosen];
        }

        var lines = new List<string>();
        bool stopped = false;
        foreach (var target in players)
        {
            int given = 0;
            var failures = new List<string>();
            foreach (var item in kit.Items.ToList())
            {
                var reply = await _main.RunAsync(PlayerCommands.AddItem(target, item.FullType, item.Count));
                if (reply is null)
                {
                    // the server did not answer: the rest must not trickle in later, one timeout each
                    stopped = true;
                    break;
                }
                if (PlayerCommands.InterpretAddItem(reply, target) != CommandOutcome.Failed)
                    given++;
                else
                    failures.Add($"{item.Name}: {reply?.Trim() ?? "see the console"}");
            }
            lines.Add($"{target}: {given} of {kit.Items.Count} items given." + (failures.Count > 0 ? " " + string.Join("; ", failures) : ""));
            if (stopped)
            {
                lines.Add("Stopped: the server did not answer (see the console); the rest was not given.");
                break;
            }
        }
        Result = $"\"{kit.Name}\" at {DateTime.Now:HH:mm:ss}\n" + string.Join("\n", lines);
    }

    internal void Save()
    {
        try
        {
            KitStore.Save(Kits.Select(k => k.Model));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Result = "Could not save the kits: " + ex.Message;
        }
    }

    void UpdateIcons()
    {
        foreach (var item in Kits.SelectMany(k => k.Items))
        {
            var entry = _main.Catalog.Find(item.FullType);
            item.IconPath = entry?.IconPath;
            item.Known = entry is not null;
        }
    }

    string UniqueName(string name)
    {
        var candidate = name;
        for (int n = 2; Kits.Any(k => k.Name.Equals(candidate, StringComparison.CurrentCultureIgnoreCase)); n++)
            candidate = $"{name} {n}";
        return candidate;
    }
}
