using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpiffoCON.Core.Catalog;

public enum CatalogKind { Item, Vehicle }

/// <summary>Where a definition came from: the base game or a mod.</summary>
public sealed record CatalogSource(string Label, string? ModId = null, string? WorkshopId = null)
{
    public static readonly CatalogSource Vanilla = new("Base game");

    public bool IsVanilla => ModId is null;
}

public sealed record CatalogEntry(
    CatalogKind Kind,
    string FullType,
    string DisplayName,
    string Category,
    CatalogSource Source,
    /// <summary>Last mod that changed a definition it didn't create, if any.</summary>
    CatalogSource? ChangedBy,
    string? IconPath,
    bool Hidden,
    float? Weight);

/// <summary>
/// Merges vanilla and mod scripts in load order into a catalog of spawnable items and vehicles.
/// A later definition of the same Module.Name changes only the properties it sets, like the game.
/// </summary>
public sealed partial class CatalogBuilder
{
    /// <summary>The only script properties the catalog needs.</summary>
    static readonly HashSet<string> KeptProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "DisplayName", "DisplayCategory", "ItemType", "Type", "Icon", "IconsForTexture", "Weight", "hidden",
        "carModelName", "template!",
    };

    readonly Dictionary<string, Definition> _items = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Definition> _vehicles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Definition> _vehicleTemplates = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);
    readonly Translations _names = new();
    readonly string _language;

    /// <param name="language">Translate folder name for display names ("EN", "IT", ...); EN is the fallback.</param>
    public CatalogBuilder(string language = "EN") => _language = language.ToUpperInvariant();

    public int ScriptFiles { get; private set; }

    /// <summary>Adds a folder that contains "media" (the game folder, or a mod's content root).</summary>
    public void AddContentRoot(string root, CatalogSource source)
    {
        var media = Path.Combine(root, "media");
        var scripts = Path.Combine(media, "scripts");
        if (Directory.Exists(scripts))
        {
            foreach (var file in Directory.EnumerateFiles(scripts, "*.txt", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (IOException) { continue; }
                AddScript(text, source);
                ScriptFiles++;
            }
        }

        var translate = Path.Combine(media, "lua", "shared", "Translate");
        _names.LoadFolder(Path.Combine(translate, "EN"));
        if (_language != "EN")
            _names.LoadFolder(Path.Combine(translate, _language));

        var textures = Path.Combine(media, "textures");
        if (Directory.Exists(textures))
        {
            foreach (var png in Directory.EnumerateFiles(textures, "Item_*.png", SearchOption.AllDirectories))
                _icons[Path.GetFileNameWithoutExtension(png)["Item_".Length..]] = png;
        }
    }

    /// <summary>
    /// Icons from a folder of Item_*.png (the base-game icons extracted from the user's install).
    /// They never replace an icon already known, so mods added later still win.
    /// </summary>
    public void AddIconFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        foreach (var png in Directory.EnumerateFiles(folder, "Item_*.png"))
            _icons.TryAdd(Path.GetFileNameWithoutExtension(png)["Item_".Length..], png);
    }

    public void AddMod(ModInfo mod)
    {
        var source = new CatalogSource(mod.Name, mod.Id, mod.WorkshopId);
        foreach (var root in mod.ContentRoots)
            AddContentRoot(root, source);
    }

    public void AddScript(string text, CatalogSource source)
    {
        foreach (var module in ScriptParser.Parse(text).Children.Where(b => b.Type.Equals("module", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var block in module.Children)
            {
                if (block.Name.Length == 0)
                    continue;
                var type = block.Type.ToLowerInvariant();
                var target = type switch
                {
                    "item" => _items,
                    "vehicle" => _vehicles,
                    "template vehicle" => _vehicleTemplates,
                    _ => null,
                };
                if (target is not null)
                    Merge(target, module.Name, block, source);
            }
        }
    }

    static void Merge(Dictionary<string, Definition> target, string module, ScriptBlock block, CatalogSource source)
    {
        var fullType = module + "." + block.Name;
        if (!target.TryGetValue(fullType, out var def))
            target[fullType] = def = new Definition(module, block.Name, source);
        else if (source != def.Source)
            def.ChangedBy = source;
        foreach (var (key, value) in block.Values)
            if (KeptProperties.Contains(key))
                def.Props[key] = value;
    }

    public IReadOnlyList<CatalogEntry> Build()
    {
        var entries = new List<CatalogEntry>(_items.Count + _vehicles.Count);
        foreach (var (fullType, def) in _items)
        {
            var icon = def.Get("Icon") ?? def.Get("IconsForTexture")?.Split(';')[0].Trim();
            var name = _names.ItemNames.GetValueOrDefault(fullType) ?? def.Get("DisplayName");
            // untranslated base-game items are internal (wound/bandage/zombie-damage visuals)
            bool hidden = string.Equals(def.Get("hidden"), "true", StringComparison.OrdinalIgnoreCase)
                || (name is null && def.Source.IsVanilla);
            entries.Add(new CatalogEntry(
                CatalogKind.Item,
                fullType,
                name ?? SplitWords(def.Name),
                ItemCategory(def),
                def.Source,
                def.ChangedBy,
                icon is not null ? _icons.GetValueOrDefault(icon) : null,
                hidden,
                float.TryParse(def.Get("Weight"), System.Globalization.CultureInfo.InvariantCulture, out var w) ? w : null));
        }
        foreach (var (fullType, def) in _vehicles)
        {
            entries.Add(new CatalogEntry(
                CatalogKind.Vehicle,
                fullType,
                VehicleName(def),
                VehicleCategory(def.Name),
                def.Source,
                def.ChangedBy,
                null,
                false,
                null));
        }
        entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        return entries;
    }

    /// <summary>
    /// IGUI_VehicleName&lt;script&gt;, else the name of its carModelName or of the vehicle it copies
    /// ("template! = X"), marked as burnt/wrecked when it is one of those variants.
    /// </summary>
    string VehicleName(Definition def)
    {
        if (_names.VehicleNames.TryGetValue(def.Name, out var own))
            return own;
        var current = def;
        for (int depth = 0; depth < 8; depth++)
        {
            string? inherited = null;
            if (current.Get("carModelName") is { } model)
                inherited = _names.VehicleNames.GetValueOrDefault(model);
            inherited ??= current != def ? _names.VehicleNames.GetValueOrDefault(current.Name) : null;
            if (inherited is not null)
            {
                if (def.Name.Contains("Burnt", StringComparison.OrdinalIgnoreCase))
                    return "Burnt " + inherited;
                if (def.Name.Contains("Smashed", StringComparison.OrdinalIgnoreCase))
                    return "Wrecked " + inherited;
                return inherited;
            }
            if (current.Get("template!") is not { } parent)
                break;
            var key = current.Module + "." + parent;
            if (!_vehicleTemplates.TryGetValue(key, out var next) && !_vehicles.TryGetValue(key, out next))
                break;
            current = next;
        }

        // CarNormalBurnt → CarNormal, ModernCar_Martin → ModernCar
        var baseName = WreckSuffix().Replace(def.Name, "");
        if (baseName != def.Name && _names.VehicleNames.TryGetValue(baseName, out var wreckBase))
            return (def.Name.Contains("Burnt", StringComparison.OrdinalIgnoreCase) ? "Burnt " : "Wrecked ") + wreckBase;
        int underscore = def.Name.IndexOf('_');
        if (underscore > 0 && _names.VehicleNames.TryGetValue(def.Name[..underscore], out var variantBase))
            return $"{variantBase} ({SplitWords(def.Name[(underscore + 1)..])})";
        return SplitWords(def.Name);
    }

    [GeneratedRegex("(Burnt|Smashed(Front|Left|Rear|Right)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex WreckSuffix();

    static string ItemCategory(Definition def)
    {
        if (def.Get("DisplayCategory") is { Length: > 0 } category)
            return SplitWords(category);
        // B42: "base:weapon"; B41: "Weapon"
        var type = def.Get("ItemType") ?? def.Get("Type") ?? "Item";
        int colon = type.IndexOf(':');
        type = colon >= 0 ? type[(colon + 1)..] : type;
        return type.Length > 0 ? char.ToUpperInvariant(type[0]) + type[1..] : "Item";
    }

    static string VehicleCategory(string name)
    {
        if (name.Contains("Burnt", StringComparison.OrdinalIgnoreCase) || name.Contains("Smashed", StringComparison.OrdinalIgnoreCase))
            return "Wreck";
        if (name.StartsWith("Trailer", StringComparison.OrdinalIgnoreCase))
            return "Trailer";
        return "Vehicle";
    }

    static string SplitWords(string name) => CamelBoundary().Replace(name.Replace('_', ' '), " ");

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[A-Za-z])(?=[0-9])")]
    private static partial Regex CamelBoundary();

    // ---- vanilla snapshot ----

    /// <summary>
    /// Compact JSON of everything collected so far, so the app can ship the base game's catalog
    /// (the game files are not needed at runtime).
    /// </summary>
    public string ExportSnapshot()
    {
        var snapshot = new Snapshot
        {
            Items = _items.Values.Select(d => new SnapshotDef(d.Module, d.Name, d.Props)).ToList(),
            Vehicles = _vehicles.Values.Select(d => new SnapshotDef(d.Module, d.Name, d.Props)).ToList(),
            VehicleTemplates = _vehicleTemplates.Values.Select(d => new SnapshotDef(d.Module, d.Name, d.Props)).ToList(),
            ItemNames = _names.ItemNames.Where(n => _items.ContainsKey(n.Key)).ToDictionary(),
            VehicleNames = _names.VehicleNames,
        };
        return JsonSerializer.Serialize(snapshot, SnapshotJson.Default.Snapshot);
    }

    public void ImportSnapshot(string json, CatalogSource? source = null)
    {
        source ??= CatalogSource.Vanilla;
        var snapshot = JsonSerializer.Deserialize(json, SnapshotJson.Default.Snapshot) ?? throw new InvalidDataException("Empty catalog snapshot.");
        Import(snapshot.Items, _items, source);
        Import(snapshot.Vehicles, _vehicles, source);
        Import(snapshot.VehicleTemplates, _vehicleTemplates, source);
        foreach (var (k, v) in snapshot.ItemNames)
            _names.ItemNames[k] = v;
        foreach (var (k, v) in snapshot.VehicleNames)
            _names.VehicleNames[k] = v;

        static void Import(List<SnapshotDef> defs, Dictionary<string, Definition> target, CatalogSource source)
        {
            foreach (var d in defs)
            {
                var def = new Definition(d.Module, d.Name, source);
                foreach (var (k, v) in d.Props)
                    def.Props[k] = v;
                target[d.Module + "." + d.Name] = def;
            }
        }
    }

    internal sealed class Snapshot
    {
        public List<SnapshotDef> Items { get; set; } = [];
        public List<SnapshotDef> Vehicles { get; set; } = [];
        public List<SnapshotDef> VehicleTemplates { get; set; } = [];
        public Dictionary<string, string> ItemNames { get; set; } = [];
        public Dictionary<string, string> VehicleNames { get; set; } = [];
    }

    internal sealed record SnapshotDef(string Module, string Name, Dictionary<string, string> Props);

    sealed class Definition(string module, string name, CatalogSource source)
    {
        public string Module { get; } = module;
        public string Name { get; } = name;
        public CatalogSource Source { get; } = source;
        public CatalogSource? ChangedBy { get; set; }
        public Dictionary<string, string> Props { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => Props.GetValueOrDefault(key);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(CatalogBuilder.Snapshot))]
internal sealed partial class SnapshotJson : System.Text.Json.Serialization.JsonSerializerContext;
