using System.IO;
using System.Windows;
using System.Windows.Media;
using SpiffoCON.Core.Map;

namespace SpiffoCON.Controls;

/// <summary>
/// The world map ready to draw: frozen geometry per block of the world and per layer (so only what is
/// on screen is drawn), the place names and the game's image pyramids. Built off the UI thread.
/// </summary>
public sealed class MapScene : IDisposable
{
    /// <summary>Squares per block: 4 × 4 cells.</summary>
    const int BlockSize = WorldMap.CellSize * 4;

    public sealed record Block(Rect Bounds, IReadOnlyDictionary<MapLayer, Geometry> Areas, IReadOnlyDictionary<MapLayer, Geometry> Lines);

    public required Rect WorldBounds { get; init; }
    public required IReadOnlyList<Block> Blocks { get; init; }
    public required IReadOnlyList<MapLabel> Labels { get; init; }
    public MapPyramid? Forest { get; init; }
    public MapPyramid? Satellite { get; init; }

    /// <summary>The game's colors for each layer (ISMapDefinitions.lua, initDefaultStyleV1).</summary>
    public static readonly IReadOnlyDictionary<MapLayer, Brush> Fills = new Dictionary<MapLayer, Brush>
    {
        [MapLayer.Forest] = Frozen(189, 197, 163),
        [MapLayer.Water] = Frozen(59, 141, 149),
        [MapLayer.RoadTrail] = Frozen(185, 122, 87),
        [MapLayer.RoadTertiary] = Frozen(171, 158, 143),
        [MapLayer.RoadSecondary] = Frozen(134, 125, 113),
        [MapLayer.RoadPrimary] = Frozen(134, 125, 113),
        [MapLayer.Railway] = Frozen(200, 191, 231),
        [MapLayer.Building] = Frozen(210, 158, 105),
        [MapLayer.BuildingCommunity] = Frozen(139, 117, 235),
        [MapLayer.BuildingHospitality] = Frozen(127, 206, 225),
        [MapLayer.BuildingIndustrial] = Frozen(56, 54, 53),
        [MapLayer.BuildingMedical] = Frozen(229, 128, 151),
        [MapLayer.BuildingRestaurants] = Frozen(245, 225, 60),
        [MapLayer.BuildingRetail] = Frozen(184, 205, 84),
    };

    public static readonly Brush Paper = Frozen(219, 215, 192);
    public static readonly Color ForestColor = Color.FromRgb(189, 197, 163);

    static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public static MapScene Build(MapFiles files)
    {
        var map = WorldMap.Load(files.WorldMap, files.AnnotationsLua, files.LabelsJson);
        var byBlock = map.Features
            .Where(f => f.Rings.Count > 0)
            .GroupBy(f => ((int)Math.Floor(f.Rings[0][0] / BlockSize), (int)Math.Floor(f.Rings[0][1] / BlockSize)));
        var blocks = new List<Block>();
        foreach (var group in byBlock)
        {
            var bounds = Rect.Empty;
            var areas = new Dictionary<MapLayer, Geometry>();
            var lines = new Dictionary<MapLayer, Geometry>();
            foreach (var layer in group.GroupBy(f => (f.Layer, f.IsLine)))
            {
                var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
                using (var ctx = geometry.Open())
                {
                    foreach (var feature in layer)
                    {
                        foreach (var ring in feature.Rings)
                        {
                            ctx.BeginFigure(new Point(ring[0], ring[1]), isFilled: !feature.IsLine, isClosed: !feature.IsLine);
                            var points = new List<Point>(ring.Length / 2);
                            for (int i = 2; i + 1 < ring.Length; i += 2)
                                points.Add(new Point(ring[i], ring[i + 1]));
                            ctx.PolyLineTo(points, isStroked: true, isSmoothJoin: false);
                        }
                    }
                }
                geometry.Freeze();
                bounds.Union(geometry.Bounds);
                (layer.Key.IsLine ? lines : areas)[layer.Key.Layer] = geometry;
            }
            if (!bounds.IsEmpty)
                blocks.Add(new Block(bounds, areas, lines));
        }
        var (minX, minY, maxX, maxY) = map.Bounds;
        MapPyramid? forest = null, satellite = null;
        try
        {
            forest = MapPyramid.Open(files.Forest);
            satellite = files.HasSatellite ? MapPyramid.Open(files.Satellite) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // a damaged zip: the map still draws, without that layer
        }
        return new MapScene
        {
            WorldBounds = new Rect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY)),
            Blocks = blocks,
            Labels = map.Labels,
            Forest = forest,
            Satellite = satellite,
        };
    }

    public void Dispose()
    {
        Forest?.Dispose();
        Satellite?.Dispose();
    }
}
