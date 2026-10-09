using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpiffoCON.Core.Bridge;
using SpiffoCON.Services;

namespace SpiffoCON.ViewModels;

/// <summary>
/// A part in the list of the vehicle window: its condition as the game's mechanics window writes it
/// ("55%", or "Missing"), what is in it, and what its button does.
/// </summary>
public sealed record VehiclePartRow(
    BridgeVehiclePart Part, string Category, string Name, string ConditionText, string Color, string Detail,
    bool CanRepair, string RepairText, string RepairTip)
{
    public string Id => Part.Id;
}

/// <summary>A part on the drawing of the vehicle: a rectangle of the plan, coloured by its condition.</summary>
public sealed record VehicleShape(
    string PartId, string Tip, double X, double Y, double Width, double Height, double Radius,
    string Fill, string Stroke, double StrokeThickness, bool Missing);

/// <summary>
/// One vehicle part by part (the "Vehicle details" window), as the game's mechanics window shows it: the
/// condition of each part, the ones that are gone, and a repair for each. From the bridge (v13); it follows
/// the bridge's refreshes.
/// </summary>
public sealed partial class VehicleViewModel : ObservableObject, IDisposable
{
    readonly MainViewModel _main;
    readonly DispatcherTimer _soon = new() { Interval = TimeSpan.FromMilliseconds(800) };
    bool _reading, _again, _disposed;
    BridgeVehicleDetails? _details;

    public VehicleViewModel(MainViewModel main, int id, string? script)
    {
        _main = main;
        Id = id;
        Script = script;
        Title = $"{script ?? "Vehicle"} #{id}";
        _soon.Tick += async (_, _) =>
        {
            _soon.Stop();
            await RefreshAsync();
        };
        // a bridge refresh fills the vehicles again: one read for the whole burst of changes
        _main.Bridge.Vehicles.CollectionChanged += OnSourceChanged;
        _ = RefreshAsync();
    }

    /// <summary>The runtime id of the vehicle, and its model: an id that now is another model is refused.</summary>
    public int Id { get; }
    public string? Script { get; }

    /// <summary>For the status bar of the window: the outcome of the actions.</summary>
    public MainViewModel Main => _main;

    [ObservableProperty] private string title = "";
    [ObservableProperty] private string subtitle = "";

    /// <summary>Why there is nothing to show (the bridge not connected, an older one, the vehicle gone).</summary>
    [ObservableProperty] private string notice = "";
    [ObservableProperty] private bool hasVehicle;
    [ObservableProperty] private double condition;
    [ObservableProperty] private string conditionText = "";
    [ObservableProperty] private string conditionColor = Good;

    /// <summary>"2 parts missing: Front Left Tire, Rear Right Window".</summary>
    [ObservableProperty] private string missingText = "";
    [ObservableProperty] private string updatedText = "";
    [ObservableProperty] private bool isBusy;

    /// <summary>The part chosen in the list or on the drawing.</summary>
    [ObservableProperty] private VehiclePartRow? selectedPart;

    public ObservableCollection<CharacterFact> Facts { get; } = [];
    public ObservableCollection<VehiclePartRow> Parts { get; } = [];
    public ObservableCollection<VehicleShape> Shapes { get; } = [];

    /// <summary>The online players, for "Give a key to".</summary>
    public IReadOnlyList<string> KeyTakers => _main.OnlinePlayers.ToList();

    void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed)
            return;
        _soon.Stop();
        _soon.Start();
    }

    partial void OnSelectedPartChanged(VehiclePartRow? value) => ShowShapes();

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
    }

    async Task ReadAsync()
    {
        var (details, problem) = await _main.Bridge.ReadVehicleAsync(Id, Script);
        if (_disposed)
            return;
        if (details is null)
        {
            // gone, unloaded, or another model under this id now: nothing here is true any more
            _details = null;
            Notice = problem ?? "The bridge did not answer.";
            HasVehicle = false;
            MissingText = "";
            Facts.Clear();
            Parts.Clear();
            Shapes.Clear();
            return;
        }
        _details = details;
        Notice = "";
        HasVehicle = true;
        Show(details);
        UpdatedText = $"Read at {DateTime.Now:HH:mm:ss}";
    }

    static string N(double v, string format = "0.#") => v.ToString(format, CultureInfo.InvariantCulture);

    void Show(BridgeVehicleDetails v)
    {
        Title = $"{v.Name ?? v.Script ?? Script ?? "Vehicle"} #{v.Id ?? Id}";
        Subtitle = string.Join(" · ", new[] { v.Name is null ? null : v.Script, v.Kind, v.EngineRunning == true ? "engine running" : null }
            .Where(s => !string.IsNullOrEmpty(s)));
        Condition = Math.Clamp(v.Condition ?? 0, 0, 100);
        ConditionText = v.Condition is { } c ? N(c) + "%" : "";
        ConditionColor = ColorOf(Condition);

        var facts = new List<CharacterFact>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                facts.Add(new(label, value));
        }
        if (v is { X: { } x, Y: { } y })
            Add("Position", $"{x}, {y}" + (v.Z is { } z and not 0 ? $", floor {z}" : ""));
        // seat 0 is the driver's
        Add("In it", v.Seats.Count == 0 ? "nobody" : string.Join(", ", v.Seats.OrderBy(s => s.Seat).Select(s => s.Seat == 0 ? s.Username + " (driving)" : s.Username)));
        Add("Weight", v.Mass is { } mass ? N(mass, "#,0") : null);
        Add("Engine power", v.EnginePower is { } power ? N(power, "0") + " hp" : null);
        Add("Engine quality", v.EngineQuality is { } quality ? N(quality, "0") : null);
        Add("Engine loudness", v.EngineLoudness is { } loud ? N(loud, "0") : null);
        Add("Rust", v.Rust is { } rust ? N(rust * 100, "0") + "%" : null);
        Add("Key", v.KeyInIgnition == true ? "in the ignition" : v.Hotwired == true ? "hotwired" : null);
        Sync(Facts, facts);

        var missing = v.Parts.Where(p => p.Missing).Select(p => p.Name).ToList();
        MissingText = missing.Count == 0 ? "" : $"{missing.Count} part{(missing.Count == 1 ? "" : "s")} missing: {string.Join(", ", missing)}";

        var chosen = SelectedPart?.Id;
        // by category, in an order that reads like a walk around the car; inside one, the game's own order
        var rows = v.Parts
            .Select((p, i) => (Part: p, Index: i))
            .OrderBy(p => CategoryRank(p.Part.Category)).ThenBy(p => p.Index)
            .Select(p => Row(p.Part))
            .ToList();
        if (!Parts.SequenceEqual(rows))
        {
            Parts.Clear();
            foreach (var row in rows)
                Parts.Add(row);
            // the list was filled again: the same part stays chosen
            SelectedPart = chosen is null ? null : Parts.FirstOrDefault(p => p.Id == chosen);
        }
        ShowShapes();
    }

    static readonly string[] CategoryOrder =
        ["engine", "gastank", "tire", "brakes", "suspension", "door", "bodywork", "lights", "seat", "trunk", "dashboard"];

    static int CategoryRank(string category)
    {
        int rank = Array.IndexOf(CategoryOrder, category);
        return rank < 0 ? CategoryOrder.Length : rank;
    }

    const string Bad = "#E5534B", Good = "#57AB5A";

    /// <summary>From red (0) to green (100), as the game colours a condition.</summary>
    internal static string ColorOf(double condition)
    {
        double t = Math.Clamp(condition, 0, 100) / 100;
        int Mix(int from, int to) => (int)Math.Round(from + (to - from) * t);
        return $"#{Mix(0xE5, 0x57):X2}{Mix(0x53, 0xAB):X2}{Mix(0x4B, 0x5A):X2}";
    }

    static VehiclePartRow Row(BridgeVehiclePart p)
    {
        int condition = p.Condition ?? 0;
        var detail = new List<string>();
        if (!p.Missing)
        {
            if (!string.IsNullOrEmpty(p.Item) && !p.Item.Equals(p.Name, StringComparison.OrdinalIgnoreCase))
                detail.Add(p.Item);
            if (p is { Content: { } content, Amount: { } amount, Capacity: { } capacity })
            {
                detail.Add(content switch
                {
                    "Air" => $"pressure {N(amount, "0")} of {N(capacity, "0")}",
                    "Gasoline" => $"fuel {N(amount)} of {N(capacity, "0")}",
                    _ => $"{content} {N(amount)} of {N(capacity, "0")}",
                });
            }
            if (p.Charge is { } charge)
                detail.Add($"charge {N(charge * 100, "0")}%");
            if (p.Open == true)
                detail.Add("open");
            if (p.Locked == true)
                detail.Add("locked");
        }
        // the game's repair of a part also fills it: a tyre inflated, the tank full
        bool worn = !p.Missing && condition < 100;
        bool notFull = !p.Missing && p is { Content: not null, Amount: { } a, Capacity: { } cap } && a < cap - 0.5;
        return new VehiclePartRow(
            p, string.IsNullOrEmpty(p.CategoryName) ? p.Category : p.CategoryName, p.Name,
            p.Missing ? "Missing" : condition.ToString(CultureInfo.InvariantCulture) + "%",
            p.Missing ? Bad : ColorOf(condition),
            string.Join(" · ", detail),
            p.Missing || worn || notFull,
            p.Missing ? "Put back" : worn ? "Repair" : notFull ? "Fill" : "Repair",
            p.Missing ? "A new one is installed, whole" + (p.Content == "Air" ? " and inflated" : "")
                : worn ? "To 100%" + (p.Content is null ? "" : ", and filled")
                : notFull ? (p.Content == "Air" ? "Inflated" : "Filled to the top")
                : "Whole already");
    }

    // The plan of a car seen from above, nose up, on a 240 x 440 canvas: where each of the game's usual
    // parts is drawn (x, y, width, height, corner). A vehicle shows the ones it has; parts with other names
    // (a van's middle doors, a mod's own) are in the list only.
    static readonly Dictionary<string, (double X, double Y, double W, double H, double R)> Plan = new()
    {
        ["HeadlightLeft"] = (56, 18, 34, 10, 4), ["HeadlightRight"] = (150, 18, 34, 10, 4),
        ["EngineDoor"] = (56, 34, 128, 74, 6),
        ["Engine"] = (84, 46, 56, 50, 4), ["Battery"] = (146, 46, 26, 22, 3),
        ["Windshield"] = (60, 114, 120, 22, 6),
        ["Heater"] = (66, 142, 32, 10, 3), ["Radio"] = (104, 142, 32, 10, 3), ["GloveBox"] = (142, 142, 32, 10, 3),
        ["TireFrontLeft"] = (20, 52, 22, 58, 6), ["TireFrontRight"] = (198, 52, 22, 58, 6),
        ["BrakeFrontLeft"] = (46, 60, 8, 18, 2), ["BrakeFrontRight"] = (186, 60, 8, 18, 2),
        ["SuspensionFrontLeft"] = (46, 82, 8, 18, 2), ["SuspensionFrontRight"] = (186, 82, 8, 18, 2),
        ["DoorFrontLeft"] = (46, 156, 8, 62, 3), ["DoorFrontRight"] = (186, 156, 8, 62, 3),
        ["WindowFrontLeft"] = (57, 160, 6, 54, 2), ["WindowFrontRight"] = (177, 160, 6, 54, 2),
        ["SeatFrontLeft"] = (70, 158, 44, 54, 6), ["SeatFrontRight"] = (126, 158, 44, 54, 6),
        ["DoorRearLeft"] = (46, 226, 8, 62, 3), ["DoorRearRight"] = (186, 226, 8, 62, 3),
        ["WindowRearLeft"] = (57, 230, 6, 54, 2), ["WindowRearRight"] = (177, 230, 6, 54, 2),
        ["SeatRearLeft"] = (70, 228, 44, 54, 6), ["SeatRearRight"] = (126, 228, 44, 54, 6),
        ["WindshieldRear"] = (60, 294, 120, 18, 6),
        ["TruckBed"] = (60, 318, 120, 74, 6), ["TruckBedOpen"] = (60, 318, 120, 74, 6), ["TrailerTrunk"] = (60, 318, 120, 74, 6),
        ["GasTank"] = (68, 326, 40, 30, 4), ["Muffler"] = (132, 376, 44, 10, 4),
        ["TrunkDoor"] = (56, 398, 128, 12, 4), ["DoorRear"] = (56, 398, 128, 12, 4),
        ["TireRearLeft"] = (20, 318, 22, 58, 6), ["TireRearRight"] = (198, 318, 22, 58, 6),
        ["BrakeRearLeft"] = (46, 326, 8, 18, 2), ["BrakeRearRight"] = (186, 326, 8, 18, 2),
        ["SuspensionRearLeft"] = (46, 348, 8, 18, 2), ["SuspensionRearRight"] = (186, 348, 8, 18, 2),
        ["HeadlightRearLeft"] = (56, 414, 34, 8, 4), ["HeadlightRearRight"] = (150, 414, 34, 8, 4),
    };

    void ShowShapes()
    {
        if (_details is not { } v)
            return;
        var chosen = SelectedPart?.Id;
        var shapes = new List<VehicleShape>();
        // the large ones first: the engine is drawn over the hood, the tank over the trunk
        foreach (var p in v.Parts.Where(p => Plan.ContainsKey(p.Id)).OrderByDescending(p => Plan[p.Id].W * Plan[p.Id].H))
        {
            var (x, y, w, h, r) = Plan[p.Id];
            bool selected = p.Id == chosen;
            string color = p.Missing ? Bad : ColorOf(p.Condition ?? 0);
            shapes.Add(new VehicleShape(
                p.Id, p.Name + ": " + (p.Missing ? "missing" : (p.Condition ?? 0).ToString(CultureInfo.InvariantCulture) + "%"),
                x, y, w, h, r,
                // one that is gone: an empty outline where it should be
                p.Missing ? "#00000000" : color,
                selected ? "#FFFFFF" : p.Missing ? Bad : "#B0101010",
                selected ? 2 : p.Missing ? 1.5 : 1,
                p.Missing));
        }
        Sync(Shapes, shapes);
    }

    /// <summary>A click on the drawing: that part is chosen in the list.</summary>
    public void Select(string partId) => SelectedPart = Parts.FirstOrDefault(p => p.Id == partId) ?? SelectedPart;

    // replaced only when different: the lists don't flicker (or lose their scroll) at every refresh
    static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        if (target.SequenceEqual(source))
            return;
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    /// <summary>The vehicle as the Bridge tab's commands take it.</summary>
    BridgeVehicle AsListed() => new()
    {
        Id = Id,
        Script = _details?.Script ?? Script,
        Driver = _details?.Seats.FirstOrDefault(s => s.Seat == 0)?.Username,
    };

    [RelayCommand]
    private async Task RepairPartAsync(VehiclePartRow? row)
    {
        if (row is null)
            return;
        SelectedPart = Parts.FirstOrDefault(p => p.Id == row.Id) ?? SelectedPart;
        _main.StatusText = await _main.Bridge.RepairPartAsync(Id, _details?.Script ?? Script, row.Part);
        await RefreshAsync();
    }

    async Task ThroughBridgeAsync(Func<Task> action)
    {
        await action();
        _main.StatusText = _main.Bridge.StatusText;
        await RefreshAsync();
    }

    [RelayCommand]
    private Task RepairAllAsync() => ThroughBridgeAsync(() => _main.Bridge.RepairVehicleCommand.ExecuteAsync(AsListed()));

    [RelayCommand]
    private Task RefuelAsync() => ThroughBridgeAsync(() => _main.Bridge.RefuelVehicleCommand.ExecuteAsync(AsListed()));

    /// <summary>The vehicle was removed from this window: nothing left to show.</summary>
    public event EventHandler? Removed;

    [RelayCommand]
    private async Task RemoveAsync()
    {
        await ThroughBridgeAsync(() => _main.Bridge.RemoveVehicleCommand.ExecuteAsync(AsListed()));
        // answered no, or it failed: the bridge still lists the vehicle (or lists nothing: not connected)
        if (!_disposed && !HasVehicle && _main.Bridge.IsConnected && !_main.Bridge.Vehicles.Any(v => v.Id == Id))
            Removed?.Invoke(this, EventArgs.Empty);
    }

    public Task GiveKeyAsync(string username) => ThroughBridgeAsync(() => _main.Bridge.GiveVehicleKeyAsync(AsListed(), username));

    [RelayCommand]
    private void ShowOnMap()
    {
        if (_details is not { X: { } x, Y: { } y })
            return;
        _main.ShowTab("Map");
        _main.Map.ShowAt(x, y);
    }

    [RelayCommand]
    private void CopyPosition()
    {
        if (_details is not { X: { } x, Y: { } y } v)
            return;
        var text = $"{x},{y},{v.Z ?? 0}";
        SafeClipboard.SetText(text);
        _main.StatusText = $"Copied: {text}";
    }

    public void Dispose()
    {
        _disposed = true;
        _soon.Stop();
        _main.Bridge.Vehicles.CollectionChanged -= OnSourceChanged;
    }
}
