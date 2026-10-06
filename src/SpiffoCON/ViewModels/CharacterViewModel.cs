using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Bridge;

namespace SpiffoCON.ViewModels;

/// <summary>One line of the facts about a character: "Position", "10600, 9400".</summary>
public sealed record CharacterFact(string Label, string Value);

/// <summary>A need or mood as a bar: 0 (none) to 1 (as bad as it gets).</summary>
public sealed record CharacterStat(string Name, double Value, string Text);

/// <summary>A part of the body that has something to say.</summary>
public sealed record CharacterPart(string Name, int Health, string Text);

/// <summary>The skills of one category ("Combat").</summary>
public sealed record CharacterSkillGroup(string Category, IReadOnlyList<BridgeSkill> Skills);

/// <summary>Something held, worn or attached.</summary>
public sealed record CharacterEquipment(string Group, string Slot, string Name, string ConditionText, double? Condition);

/// <summary>
/// Everything SpiffoCON knows about one player in one place (the "Character details" window): who and
/// where from RCON and the bridge's snapshot, health, needs, traits, skills, equipment and inventory from the
/// bridge, the account from the server's database when it was read. It follows the bridge's refreshes.
/// </summary>
public sealed partial class CharacterViewModel : ObservableObject, IDisposable
{
    readonly MainViewModel _main;
    readonly DispatcherTimer _soon = new() { Interval = TimeSpan.FromMilliseconds(800) };
    bool _reading, _again, _disposed;

    public CharacterViewModel(MainViewModel main, string username)
    {
        _main = main;
        Username = username;
        _soon.Tick += async (_, _) =>
        {
            _soon.Stop();
            await RefreshAsync();
        };
        // a bridge refresh updates the players in place: one read for the whole burst of changes
        _main.Bridge.Players.CollectionChanged += OnSourceChanged;
        _main.OnlinePlayersChanged += OnOnlineChanged;
        ShowWho();
        _ = RefreshAsync();
    }

    public string Username { get; }

    /// <summary>For the status bar of the window: the outcome of the actions.</summary>
    public MainViewModel Main => _main;

    public PlayerActions Actions => _main.PlayerActions;

    /// <summary>Raised after each read: the window builds the actions again (what is possible changes).</summary>
    public event EventHandler? Updated;

    [ObservableProperty] private string title = "";
    [ObservableProperty] private string subtitle = "";

    /// <summary>Why something is missing (the bridge not connected, the player offline, an older bridge).</summary>
    [ObservableProperty] private string notice = "";

    /// <summary>"INFECTED · 1 bite · on fire": what can't wait.</summary>
    [ObservableProperty] private string alarm = "";
    [ObservableProperty] private double health;
    [ObservableProperty] private string healthText = "";
    [ObservableProperty] private bool hasHealth;
    [ObservableProperty] private string partsNote = "";
    [ObservableProperty] private string statsNote = "";

    /// <summary>The bridge gave the sheet (v7): health, needs, traits and skills are there to show.</summary>
    [ObservableProperty] private bool hasSheet;

    /// <summary>The bridge gave the equipment (v11).</summary>
    [ObservableProperty] private bool hasEquipment;
    [ObservableProperty] private string traits = "";
    [ObservableProperty] private string equipmentNote = "";
    [ObservableProperty] private string inventoryTitle = "Inventory";
    [ObservableProperty] private string updatedText = "";
    [ObservableProperty] private bool isBusy;

    public ObservableCollection<CharacterFact> Facts { get; } = [];
    public ObservableCollection<CharacterStat> Stats { get; } = [];
    public ObservableCollection<CharacterPart> Parts { get; } = [];
    public ObservableCollection<CharacterSkillGroup> Skills { get; } = [];
    public ObservableCollection<CharacterEquipment> Equipment { get; } = [];
    public ObservableCollection<BridgeItem> Items { get; } = [];

    void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshSoon();

    void OnOnlineChanged(object? sender, EventArgs e) => RefreshSoon();

    void RefreshSoon()
    {
        if (_disposed)
            return;
        ShowWho();
        _soon.Stop();
        _soon.Start();
    }

    BridgePlayer? Info => Actions.BridgeInfo(Username);

    bool Online => _main.IsSessionActive && Actions.IsOnline(Username);

    /// <summary>The header and the facts: from what is already in memory (no request).</summary>
    void ShowWho()
    {
        var info = Info;
        var account = _main.Accounts.Find(Username);
        Title = BridgeViewModel.WithCharacter(info?.Username ?? Username, info?.CharacterName ?? account?.CharacterName);
        var state = !_main.IsSessionActive ? "not connected"
            : info?.Dead == true ? "dead"
            : Online ? "online"
            : info is not null ? "in the game (not in the RCON list yet)"
            : "offline";
        Subtitle = string.Join(" · ", new[] { state, info?.Role ?? account?.Role, info?.Profession }.Where(s => !string.IsNullOrEmpty(s)));

        var facts = new List<CharacterFact>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                facts.Add(new(label, value));
        }
        var inv = CultureInfo.InvariantCulture;
        if (info is not null)
        {
            if (info.X is { } x && info.Y is { } y)
                Add("Position", $"{x}, {y}" + (info.Z is { } z and not 0 ? $", floor {z}" : ""));
            Add("Vehicle", info.Vehicle);
            Add("Zombies near", info.ZombiesNear?.ToString(inv));
            Add("Survived", info.HoursSurvived is { } h ? Hours(h) : null);
            Add("Zombie kills", info.ZombieKills?.ToString(inv));
            var powers = new[] { (info.God, "god mode"), (info.Invisible, "invisible"), (info.Noclip, "no clip") }
                .Where(p => p.Item1 == true).Select(p => p.Item2).ToList();
            Add("Powers", powers.Count > 0 ? string.Join(", ", powers) : null);
            Add("Steam ID", info.SteamId);
        }
        else if (account is not null)
        {
            // offline: where the server last saved them
            Add("Last position", account.Position);
        }
        if (account is not null)
        {
            Add("Last connection", account.LastSeen);
            if (account.Banned)
                Add("Account", "banned");
            if (account.Kicks > 0 || account.Bans > 0)
                Add("History", $"{account.Kicks} kick{(account.Kicks == 1 ? "" : "s")}, {account.Bans} ban{(account.Bans == 1 ? "" : "s")}");
        }
        Sync(Facts, facts);
    }

    static string Hours(double hours) =>
        hours >= 48 ? $"{(int)(hours / 24)} days, {(int)(hours % 24)} hours"
        : hours.ToString("0.#", CultureInfo.InvariantCulture) + " hours";

    // replaced only when different: the lists don't flicker (or lose their scroll) at every refresh
    static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source))
            return;
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed)
            return;
        // one read at a time; a change that came meanwhile gets its own read after
        if (_reading)
        {
            _again = true;
            return;
        }
        _reading = true;
        IsBusy = true;
        try
        {
            do
            {
                _again = false;
                await ReadAsync();
            }
            while (_again && !_disposed);
        }
        finally
        {
            _reading = false;
            IsBusy = false;
        }
        if (!_disposed)
            Updated?.Invoke(this, EventArgs.Empty);
    }

    async Task ReadAsync()
    {
        ShowWho();
        var info = Info;
        if (info is null)
        {
            // nothing to ask the bridge about: it only knows who is in the game now
            Clear(_main.Bridge.IsConnected
                ? $"{Username} is not in the game: health, skills, equipment and inventory show while they are online."
                : "Health, skills, equipment and inventory come from the SpiffoCON Bridge: connect it in the Bridge tab.");
            return;
        }
        var (items, details, problem) = await _main.Bridge.ReadCharacterAsync(info.Username);
        if (_disposed)
            return;
        if (items is null)
        {
            Clear(problem ?? "The bridge did not answer.");
            return;
        }
        Sync(Items, items);
        int count = items.Sum(i => i.Count);
        InventoryTitle = $"Inventory: {count} item{(count == 1 ? "" : "s")}"
            + (details is { Weight: { } w, MaxWeight: { } max }
                ? $", {w.ToString("0.#", CultureInfo.InvariantCulture)} of {max.ToString("0.#", CultureInfo.InvariantCulture)} carried" : "");
        ShowDetails(info, details);
        UpdatedText = $"Read at {DateTime.Now:HH:mm:ss}";
    }

    void Clear(string why)
    {
        Notice = why;
        Alarm = "";
        HasHealth = false;
        HasSheet = false;
        HasEquipment = false;
        StatsNote = "";
        PartsNote = "";
        Traits = "";
        EquipmentNote = "";
        InventoryTitle = "Inventory";
        Stats.Clear();
        Parts.Clear();
        Skills.Clear();
        Equipment.Clear();
        Items.Clear();
    }

    static readonly (string Key, string Name)[] StatNames =
    [
        ("hunger", "Hunger"), ("thirst", "Thirst"), ("fatigue", "Tiredness"), ("endurance", "Exertion"), ("pain", "Pain"),
        ("panic", "Panic"), ("stress", "Stress"), ("boredom", "Boredom"), ("unhappiness", "Unhappiness"),
        ("intoxication", "Drunkenness"), ("sickness", "Sickness"),
    ];

    void ShowDetails(BridgePlayer info, BridgePlayerDetails? details)
    {
        Health = info.Health ?? details?.Health ?? 0;
        HasHealth = (info.Health ?? details?.Health) is not null;
        HealthText = HasHealth ? $"{Health:0} of 100" : "";
        if (details is null)
        {
            // bridge v2 to v6: the inventory only
            Notice = _main.Bridge.BridgeVersion < 7
                ? $"Health, traits and skills need bridge v7 (the server runs v{_main.Bridge.BridgeVersion})."
                : "The bridge could not read this character's sheet.";
            HasSheet = HasHealth;
            HasEquipment = false;
            StatsNote = "";
            Alarm = "";
            Traits = "";
            PartsNote = "";
            EquipmentNote = "";
            Stats.Clear();
            Parts.Clear();
            Skills.Clear();
            Equipment.Clear();
            return;
        }
        HasSheet = true;
        HasEquipment = details.Equipment is not null;
        Notice = details.Parts is null ? "Body parts and equipment need bridge v11: the Workshop item has to be updated and the server restarted." : "";

        var alarms = new List<string>();
        if (details.Infected == true)
            alarms.Add("INFECTED");
        if (details.Bitten is > 0 and var bites)
            alarms.Add($"{bites} bite{(bites == 1 ? "" : "s")}");
        if (details.OnFire == true)
            alarms.Add("ON FIRE");
        if (details.Asleep == true)
            alarms.Add("asleep");
        Alarm = string.Join(" · ", alarms);

        // endurance is the other way round in the game (1: rested): shown as exertion, like the others
        Sync(Stats, StatNames
            .Select(s => (s.Name, Value: details.Stats.GetValueOrDefault(s.Key) is { } v ? Math.Clamp(s.Key == "endurance" ? 1 - v : v, 0, 1) : (double?)null))
            // only what is there to see: a rested, fed, calm character has no bars
            .Where(s => s.Value is >= 0.01)
            .Select(s => new CharacterStat(s.Name, s.Value!.Value, $"{s.Value:P0}"))
            .ToList());

        StatsNote = Stats.Count == 0 ? "Fed, rested and calm: nothing to report." : "";
        Traits = string.Join(", ", details.Traits);

        Sync(Skills, details.Skills
            .GroupBy(s => s.Category ?? "")
            .Select(g => new CharacterSkillGroup(g.Key, g.ToList()))
            .ToList());

        if (details.Parts is { } parts)
        {
            var hurt = parts.Where(p => p.Conditions.Count > 0 || p.Health is < 100)
                .Select(p => new CharacterPart(p.Name, p.Health ?? 100, string.Join(", ", p.Conditions)))
                .ToList();
            Sync(Parts, hurt);
            PartsNote = hurt.Count == 0 ? "Every part of the body is sound." : "";
        }
        else
        {
            Parts.Clear();
            PartsNote = "";
        }

        if (details.Equipment is { } equipment)
        {
            Sync(Equipment, equipment.Select(e => new CharacterEquipment(
                e.Kind switch { "hand" => "In hand", "worn" => "Worn", "attached" => "Attached", _ => e.Kind },
                Slot(e.Slot), e.Name,
                e.Condition is { } c ? $"{c:P0}" : "", e.Condition)).ToList());
            EquipmentNote = equipment.Count == 0 ? "Nothing worn, held or attached." : "";
        }
        else
        {
            Equipment.Clear();
            EquipmentNote = "";
        }
    }

    /// <summary>"base:jacket_hat" → "Jacket hat": a body location the game has no name for.</summary>
    internal static string Slot(string slot)
    {
        int colon = slot.LastIndexOf(':');
        if (colon < 0 || colon == slot.Length - 1)
            return slot;
        var words = slot[(colon + 1)..].Replace('_', ' ').Trim();
        return words.Length == 0 ? slot : char.ToUpperInvariant(words[0]) + words[1..];
    }

    [RelayCommand]
    private async Task RemoveOneAsync(BridgeItem? item) => await RemoveAsync(item, 1);

    [RelayCommand]
    private async Task RemoveAllAsync(BridgeItem? item) => await RemoveAsync(item, item?.Count ?? 0);

    async Task RemoveAsync(BridgeItem? item, int count)
    {
        if (item is null || Info is not { } info)
            return;
        await _main.Bridge.RemoveItemAsync(info.Username, item, count);
        _main.StatusText = _main.Bridge.StatusText;
        await RefreshAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        _soon.Stop();
        _main.Bridge.Players.CollectionChanged -= OnSourceChanged;
        _main.OnlinePlayersChanged -= OnOnlineChanged;
    }
}
