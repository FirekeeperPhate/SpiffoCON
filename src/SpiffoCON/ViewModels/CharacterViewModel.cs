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
public sealed record CharacterSkillGroup(string Category, IReadOnlyList<CharacterSkill> Skills);

/// <summary>A skill: its level (0 to 10), the experience as text when the bridge gives it, and what adding some needs.</summary>
public sealed record CharacterSkill(string Name, int Level, string XpText, string? PerkId, int? ToNextLevel, string AddTip)
{
    public bool CanAdd => PerkId is not null;
}

/// <summary>Something held, worn or attached; the icon is the Catalog's, when it knows the item.</summary>
public sealed record CharacterEquipment(string Group, string Slot, string Name, string ConditionText, double? Condition, string? IconPath = null, string? FullType = null);

/// <summary>A row of the inventory: what the bridge listed, with the Catalog's icon when it knows the item.</summary>
public sealed record CharacterItem(BridgeItem Item, string? IconPath)
{
    public string Container => Item.Container;
    public string ContainerShort => Item.ContainerShort;
    public string FullType => Item.FullType;
    public string Name => Item.Name;
    public int Count => Item.Count;
    public bool Equipped => Item.Equipped;

    /// <summary>"62% · dirty", "30% left", "25% full": the worst of the items of the row (bridge v14), or nothing to say.</summary>
    public string StateText
    {
        get
        {
            var parts = new List<string>();
            static string P(double v) => Math.Round(v * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            if (Item.Condition is { } condition)
                parts.Add(P(condition));
            if (Item.Uses is { } uses)
                parts.Add(P(uses) + " left");
            if (Item.Fill is { } fill)
                parts.Add(fill <= 0 ? "empty" : P(fill) + " full");
            if (Item.Dirty == true)
                parts.Add("dirty");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>What the state is about, for the tooltip.</summary>
    public string StateTip
    {
        get
        {
            var parts = new List<string>();
            if (Item.Condition is not null)
                parts.Add("condition (100% is new)");
            if (Item.Uses is not null)
                parts.Add("what is left of it");
            if (Item.Fill is not null)
                parts.Add("how full it is");
            if (Item.Dirty == true)
                parts.Add("blood or dirt on it");
            return parts.Count == 0 ? "" : char.ToUpperInvariant(string.Join(", ", parts)[0]) + string.Join(", ", parts)[1..]
                + (Count > 1 ? ": the worst of the " + Count.ToString(CultureInfo.InvariantCulture) : "") + ". Right-click for what can be done";
        }
    }

    /// <summary>Worn out or nearly: shown in red.</summary>
    public bool IsPoor => Item.Condition is < 0.35;
}

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
        // the icons are the Catalog's: when it loads (the base game, then the server's mods) they come in
        _main.Catalog.EntriesChanged += OnOnlineChanged;
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
    public ObservableCollection<CharacterItem> Items { get; } = [];

    // what setting a level needs, by the id of the skill: the experience now and what each level takes (bridge v16)
    Dictionary<string, (double Xp, IReadOnlyList<double> Totals)> _levels = [];

    /// <summary>Whether a skill can be set to a level (bridge v16 says what the levels take).</summary>
    public bool CanSetLevel(CharacterSkill skill) => skill.PerkId is { } id && _levels.ContainsKey(id);

    /// <summary>
    /// Brings a skill to a level, up or down: the experience added or taken away is the difference, the way
    /// the game's own admin window does it. Going down is asked first. Then the character is read again.
    /// </summary>
    public async Task SetLevelAsync(CharacterSkill skill, int level)
    {
        if (skill.PerkId is not { } id || !_levels.TryGetValue(id, out var known)
            || Core.Commands.PlayerCommands.XpToLevel(known.Xp, known.Totals, level) is not { } amount)
            return;
        string user = Info?.Username ?? Username;
        // the level it has: left as it is, with what was gained in it
        if (amount == 0 || level == skill.Level)
        {
            _main.StatusText = $"{skill.Name} of {user} is at level {level} already.";
            return;
        }
        if (amount < 0 && _main.Confirm?.Invoke($"Lower {skill.Name} of {user} from level {skill.Level} to {level}?\n\n"
                + $"{(-amount).ToString("#,0", CultureInfo.InvariantCulture)} XP are taken away.") != true)
            return;
        await Actions.SetLevelAsync(user, id, skill.Name, level, amount);
        await RefreshAsync();
    }

    /// <summary>The icon the Catalog has for an item (base game and the mods loaded), if any.</summary>
    string? IconOf(string? fullType) => string.IsNullOrEmpty(fullType) ? null : _main.Catalog.Find(fullType)?.IconPath;

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
        Sync(Items, items.Select(i => new CharacterItem(i, IconOf(i.FullType))).ToList());
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
            _levels = [];
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

        _levels = details.Skills
            .Where(s => s is { Id: not null, Xp: not null, Totals.Count: >= 10 })
            .GroupBy(s => s.Id!).ToDictionary(g => g.Key, g => (g.First().Xp!.Value, (IReadOnlyList<double>)g.First().Totals!));
        var groups = details.Skills
            .GroupBy(s => s.Category ?? "")
            .Select(g => new CharacterSkillGroup(g.Key, g.Select(Skill).ToList()))
            .ToList();
        // the groups hold lists (compared by reference): the skills themselves say whether anything changed, so
        // the list is left alone (and a "+" menu open on it stays) when nothing did
        if (!Skills.SelectMany(g => g.Skills).SequenceEqual(groups.SelectMany(g => g.Skills))
            || !Skills.Select(g => g.Category).SequenceEqual(groups.Select(g => g.Category)))
        {
            Skills.Clear();
            foreach (var g in groups)
                Skills.Add(g);
        }

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
                e.Condition is { } c ? $"{c:P0}" : "", e.Condition, IconOf(e.FullType), e.FullType)).ToList());
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

    static string Number(double v) => Math.Round(v).ToString("#,0", CultureInfo.InvariantCulture);

    /// <summary>A skill as the window shows it: level, experience (bridge v12) and how to add some.</summary>
    static CharacterSkill Skill(BridgeSkill s)
    {
        int level = s.Level ?? 0;
        // "120 / 300 XP" towards the next level; at the last level, or without a next one, the total
        string xp = s is { LevelXp: { } gained, NextXp: { } next } ? $"{Number(gained)} / {Number(next)} XP"
            : s.Xp is { } total ? $"{Number(total)} XP"
            : "";
        // addxp wants the game's id: from the bridge (v12), else from the English name of a vanilla skill
        var id = s.Id ?? Core.Commands.PlayerCommands.Perks.FirstOrDefault(p => p.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))?.Id;
        int? toNext = s is { LevelXp: { } g, NextXp: { } n } && n > g ? (int)Math.Ceiling(n - g) : null;
        return new CharacterSkill(s.Name, level, xp, id, toNext,
            id is null ? "Adding experience needs bridge v12: this skill's id is not known (a mod's skill, or the server is not in English)." : "Add experience");
    }

    /// <summary>
    /// Adds experience to a skill (RCON addxp, as the Players tab; no multiplier: the amount is what they get),
    /// then reads the character again.
    /// </summary>
    public async Task AddXpAsync(CharacterSkill skill, int amount)
    {
        if (skill.PerkId is null || amount <= 0)
            return;
        await Actions.AddXpAsync(Info?.Username ?? Username, skill.PerkId, skill.Name, amount);
        await RefreshAsync();
    }

    /// <summary>Why the items can't be repaired, cleaned, filled or recharged (null: they can): bridge v14.</summary>
    public string? ItemActionsProblem => _main.Bridge.V14Problem;

    /// <summary>Whether RCON can be used now (Give one more).</summary>
    public bool CanGive => _main.IsSessionActive && Online;

    /// <summary>The row of the inventory an equipped thing is in: the main inventory, by its type.</summary>
    public CharacterItem? RowOf(CharacterEquipment equipment) =>
        equipment.FullType is null ? null
        : Items.FirstOrDefault(i => i.FullType == equipment.FullType && i.Equipped)
            ?? Items.FirstOrDefault(i => i.FullType == equipment.FullType && i.Container == "Inventory");

    /// <summary>Repair, clean, fill, empty or recharge the items of a row (bridge v14), then read again.</summary>
    public async Task ItemActionAsync(CharacterItem item, BridgeClient.ItemAction action)
    {
        if (Info is not { } info)
            return;
        if (await _main.Bridge.ItemActionAsync(info.Username, item.Item, action) is { } result)
            _main.StatusText = result;
        await RefreshAsync();
    }

    /// <summary>One more of an item, into the main inventory (RCON additem), then read again.</summary>
    public async Task GiveOneMoreAsync(CharacterItem item)
    {
        string user = Info?.Username ?? Username;
        if (!_main.IsSessionActive)
        {
            _main.StatusText = "Not connected.";
            return;
        }
        var reply = await _main.RunAsync(Core.Commands.PlayerCommands.AddItem(user, item.FullType, 1));
        _main.StatusText = $"One more {item.Name} for {user}: " + (reply is null
            ? "failed, see the console."
            : Core.Commands.PlayerCommands.InterpretAddItem(reply, user) switch
            {
                Core.Commands.CommandOutcome.Success => reply.Trim(),
                Core.Commands.CommandOutcome.Failed => "failed: " + reply.Trim(),
                _ => "sent, the server gave no reply.",
            });
        await RefreshAsync();
    }

    /// <summary>The item in the Catalog tab (to give more of it, or put it in a kit).</summary>
    public void ShowInCatalog(CharacterItem item)
    {
        if (_main.Catalog.Show(item.FullType))
            _main.ShowTab("Catalog");
        else
            _main.StatusText = $"The Catalog does not know {item.FullType}: load the server's mods there.";
    }

    public void CopyId(CharacterItem item)
    {
        Services.SafeClipboard.SetText(item.FullType);
        _main.StatusText = $"Copied: {item.FullType}";
    }

    public Task RemoveAsync(CharacterItem item, bool all) => RemoveAsync(item, all ? item.Count : 1);

    [RelayCommand]
    private async Task RemoveOneAsync(CharacterItem? item) => await RemoveAsync(item, 1);

    [RelayCommand]
    private async Task RemoveAllAsync(CharacterItem? item) => await RemoveAsync(item, item?.Count ?? 0);

    async Task RemoveAsync(CharacterItem? item, int count)
    {
        if (item is null || Info is not { } info)
            return;
        await _main.Bridge.RemoveItemAsync(info.Username, item.Item, count);
        _main.StatusText = _main.Bridge.StatusText;
        await RefreshAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        _soon.Stop();
        _main.Bridge.Players.CollectionChanged -= OnSourceChanged;
        _main.OnlinePlayersChanged -= OnOnlineChanged;
        _main.Catalog.EntriesChanged -= OnOnlineChanged;
    }
}
