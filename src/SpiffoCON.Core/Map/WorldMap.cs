using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace SpiffoCON.Core.Map;

/// <summary>What a map feature is drawn as, in the game's own style (media/lua/client/ISUI/Maps/ISMapDefinitions.lua).</summary>
public enum MapLayer
{
    Forest,
    Water,
    RoadTrail,
    RoadTertiary,
    RoadSecondary,
    RoadPrimary,
    Railway,
    Building,
    BuildingCommunity,
    BuildingHospitality,
    BuildingIndustrial,
    BuildingMedical,
    BuildingRestaurants,
    BuildingRetail,
}

/// <summary>One polygon (outer ring and holes) or line, in world squares.</summary>
public sealed record MapFeature(MapLayer Layer, bool IsLine, IReadOnlyList<float[]> Rings, float Width = 0, int CellX = 0, int CellY = 0);

/// <summary>A place name from worldmap-annotations.lua: <c>Town</c> for towns, <c>Place</c> for the rest.</summary>
public sealed record MapLabel(string Text, float X, float Y, bool Town);

/// <summary>A folder of map data (media/maps/&lt;name&gt;) of the base game or of a mod.</summary>
public sealed record MapSource(string Name, string Folder, bool IsMod, string? LabelsJson = null);

/// <summary>
/// The game's world map data: <c>worldmap.xml</c> (cells of 300 squares, each with its polygons and lines),
/// the place names of <c>worldmap-annotations.lua</c> and their English text (<c>MapLabel.json</c>).
/// </summary>
public sealed partial class WorldMap
{
    public const int CellSize = 300;

    public List<MapFeature> Features { get; } = [];

    public List<MapLabel> Labels { get; } = [];

    /// <summary>The squares covered by cells, [MinX, MinY, MaxX, MaxY).</summary>
    public (int MinX, int MinY, int MaxX, int MaxY) Bounds { get; private set; }

    /// <summary>Cells drawn from a mod's data, over the base game's forest and satellite images.</summary>
    public HashSet<(int X, int Y)> ModCells { get; } = [];

    int _cellX, _cellY;

    /// <summary>
    /// Several map folders in the server's Map= order, merged the game's way (MapUtils.initDirectoryMapData):
    /// the first folder with features in a cell draws that cell, later folders are ignored there. Mods
    /// bring their worldmap-forest.xml too; the base game's forest comes from its image pyramid.
    /// </summary>
    public static WorldMap LoadLayered(IEnumerable<MapSource> sources)
    {
        var map = new WorldMap();
        var claimed = new HashSet<(int, int)>();
        foreach (var source in sources)
        {
            var part = new WorldMap();
            foreach (var file in source.IsMod ? ["worldmap-forest.xml", "worldmap.xml"] : new[] { "worldmap.xml" })
            {
                var path = Path.Combine(source.Folder, file);
                if (!File.Exists(path))
                    continue;
                using var stream = File.OpenRead(path);
                part.ReadXml(stream);
            }
            var cells = part.Features.Select(f => (f.CellX, f.CellY)).ToHashSet();
            cells.ExceptWith(claimed);
            map.Features.AddRange(part.Features.Where(f => cells.Contains((f.CellX, f.CellY))));
            if (source.IsMod)
                map.ModCells.UnionWith(cells);
            claimed.UnionWith(cells);

            var annotations = Path.Combine(source.Folder, "worldmap-annotations.lua");
            if (File.Exists(annotations))
                map.ReadAnnotations(File.ReadAllText(annotations),
                    source.LabelsJson is not null && File.Exists(source.LabelsJson) ? ReadLabelTexts(File.ReadAllText(source.LabelsJson)) : null);
        }
        if (claimed.Count > 0)
            map.Bounds = (claimed.Min(c => c.Item1) * CellSize, claimed.Min(c => c.Item2) * CellSize,
                (claimed.Max(c => c.Item1) + 1) * CellSize, (claimed.Max(c => c.Item2) + 1) * CellSize);
        return map;
    }

    public static WorldMap Load(string worldmapXml, string? annotationsLua = null, string? labelsJson = null)
    {
        var map = new WorldMap();
        using (var stream = File.OpenRead(worldmapXml))
            map.ReadXml(stream);
        if (annotationsLua is not null && File.Exists(annotationsLua))
            map.ReadAnnotations(File.ReadAllText(annotationsLua), labelsJson is not null && File.Exists(labelsJson) ? ReadLabelTexts(File.ReadAllText(labelsJson)) : null);
        return map;
    }

    public void ReadXml(Stream stream)
    {
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit });
        int cellX = 0, cellY = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        string? geometry = null;
        var rings = new List<float[]>();
        var ring = new List<float>();
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element)
            {
                switch (xml.Name)
                {
                    case "cell":
                        cellX = Int(xml.GetAttribute("x"));
                        cellY = Int(xml.GetAttribute("y"));
                        (_cellX, _cellY) = (cellX, cellY);
                        minX = Math.Min(minX, cellX);
                        minY = Math.Min(minY, cellY);
                        maxX = Math.Max(maxX, cellX);
                        maxY = Math.Max(maxY, cellY);
                        break;
                    case "feature":
                        geometry = null;
                        rings.Clear();
                        properties.Clear();
                        break;
                    case "geometry":
                        geometry = xml.GetAttribute("type");
                        break;
                    case "coordinates":
                        ring.Clear();
                        if (xml.IsEmptyElement)
                            rings.Add([]);
                        break;
                    case "point":
                        ring.Add(cellX * CellSize + Float(xml.GetAttribute("x")));
                        ring.Add(cellY * CellSize + Float(xml.GetAttribute("y")));
                        break;
                    case "property":
                        if (xml.GetAttribute("name") is { } name && xml.GetAttribute("value") is { } value)
                            properties[name] = value;
                        break;
                }
            }
            else if (xml.NodeType == XmlNodeType.EndElement)
            {
                if (xml.Name == "coordinates")
                {
                    rings.Add([.. ring]);
                    ring.Clear();
                }
                else if (xml.Name == "feature")
                    AddFeature(geometry, rings, properties);
            }
        }
        if (minX <= maxX)
            Bounds = (minX * CellSize, minY * CellSize, (maxX + 1) * CellSize, (maxY + 1) * CellSize);
    }

    void AddFeature(string? geometry, List<float[]> rings, Dictionary<string, string> properties)
    {
        bool line = geometry == "LineString";
        if ((!line && geometry != "Polygon") || rings.Count == 0 || rings[0].Length < (line ? 4 : 6))
            return;
        if (LayerOf(properties) is not { } layer)
            return;
        float width = properties.TryGetValue("width", out var w) && float.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
        Features.Add(new MapFeature(layer, line, rings.Where(r => r.Length >= 4).ToList(), width, _cellX, _cellY));
    }

    static MapLayer? LayerOf(Dictionary<string, string> p)
    {
        if (p.TryGetValue("building", out var building))
            return building switch
            {
                "CommunityServices" => MapLayer.BuildingCommunity,
                "Hospitality" => MapLayer.BuildingHospitality,
                "Industrial" => MapLayer.BuildingIndustrial,
                "Medical" => MapLayer.BuildingMedical,
                "RestaurantsAndEntertainment" => MapLayer.BuildingRestaurants,
                "RetailAndCommercial" => MapLayer.BuildingRetail,
                _ => MapLayer.Building,
            };
        if (p.TryGetValue("highway", out var highway))
            return highway switch
            {
                "primary" => MapLayer.RoadPrimary,
                "secondary" => MapLayer.RoadSecondary,
                "tertiary" => MapLayer.RoadTertiary,
                _ => MapLayer.RoadTrail,
            };
        if (p.ContainsKey("driveway"))
            return MapLayer.RoadTertiary;
        if (p.ContainsKey("railway"))
            return MapLayer.Railway;
        if (p.ContainsKey("water"))
            return MapLayer.Water;
        if (p.TryGetValue("natural", out var natural) && natural is "forest" or "wood")
            return MapLayer.Forest;
        return null;
    }

    // symbolsAPI:addUntranslatedText("MapLabel_Muldraugh", "text-town", 10640, 9600)
    [GeneratedRegex("""add(?:Untranslated|Translated)Text\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*,\s*(-?[0-9.]+)\s*,\s*(-?[0-9.]+)\s*\)""")]
    private static partial Regex AnnotationText();

    public void ReadAnnotations(string lua, IReadOnlyDictionary<string, string>? texts)
    {
        foreach (Match m in AnnotationText().Matches(lua))
        {
            var style = m.Groups[2].Value;
            bool town = style.StartsWith("text-town", StringComparison.Ordinal);
            if (!town && !style.StartsWith("text-place", StringComparison.Ordinal))
                continue;
            var key = m.Groups[1].Value;
            var text = texts is not null && texts.TryGetValue(key, out var t) ? t : Readable(key);
            // a long river gets its name several times; towns and places once
            if (Labels.Any(l => l.Text == text))
                continue;
            Labels.Add(new MapLabel(text, Float(m.Groups[3].Value), Float(m.Groups[4].Value), town));
        }
    }

    /// <summary>MapLabel_FallasLake → Fallas Lake, when the translation file is missing.</summary>
    static string Readable(string key)
    {
        var name = key.StartsWith("MapLabel_", StringComparison.Ordinal) ? key["MapLabel_".Length..] : key;
        return Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ").Replace('_', ' ');
    }

    /// <summary>media/lua/shared/Translate/EN/MapLabel.json: { "MapLabel_Muldraugh": "MULDRAUGH", ... }</summary>
    public static IReadOnlyDictionary<string, string>? ReadLabelTexts(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } s)
                    texts[p.Name] = s;
            return texts;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static int Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static float Float(string? s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
