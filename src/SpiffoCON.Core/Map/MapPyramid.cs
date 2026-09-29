using System.IO.Compression;

namespace SpiffoCON.Core.Map;

/// <summary>
/// An image pyramid of the game (pyramid.zip, forest.pyramid.zip in media/maps/&lt;map&gt;): 256-pixel
/// PNG tiles named <c>&lt;level&gt;/tile&lt;x&gt;x&lt;y&gt;.png</c>; level 0 has one pixel per square and each
/// level halves the one before. <c>pyramid.txt</c> gives the covered squares.
/// </summary>
public sealed class MapPyramid : IDisposable
{
    public const int TileSize = 256;

    readonly ZipArchive _zip;
    readonly Lock _lock = new();

    MapPyramid(ZipArchive zip, int minX, int minY, int maxX, int maxY, int levels)
    {
        _zip = zip;
        (MinX, MinY, MaxX, MaxY, Levels) = (minX, minY, maxX, maxY, levels);
    }

    public int MinX { get; }
    public int MinY { get; }
    public int MaxX { get; }
    public int MaxY { get; }

    /// <summary>How many levels there are (0 up to Levels - 1).</summary>
    public int Levels { get; }

    /// <summary>Squares covered by one tile of this level.</summary>
    public static int TileSquares(int level) => TileSize << level;

    public static MapPyramid? Open(string path)
    {
        if (!File.Exists(path))
            return null;
        var zip = ZipFile.OpenRead(path);
        try
        {
            int minX = 0, minY = 0, maxX = 0, maxY = 0;
            if (zip.GetEntry("pyramid.txt") is { } info)
            {
                using var reader = new StreamReader(info.Open());
                while (reader.ReadLine() is { } line)
                {
                    if (!line.StartsWith("bounds=", StringComparison.Ordinal))
                        continue;
                    var parts = line["bounds=".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 4 && parts.All(p => int.TryParse(p, out _)))
                        (minX, minY, maxX, maxY) = (int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
                }
            }
            var present = zip.Entries
                .Select(e => e.FullName.IndexOf("/tile", StringComparison.Ordinal) is > 0 and var slash && int.TryParse(e.FullName.AsSpan(0, slash), out var level) ? level : -1)
                .Where(l => l >= 0).ToHashSet();
            int levels = 0;
            while (levels < 16 && present.Contains(levels))
                levels++;
            if (levels == 0 || maxX <= minX || maxY <= minY)
            {
                zip.Dispose();
                return null;
            }
            return new MapPyramid(zip, minX, minY, maxX, maxY, levels);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>The PNG bytes of a tile, or null where the pyramid has none. Safe from any thread.</summary>
    public byte[]? ReadTile(int level, int x, int y)
    {
        lock (_lock)
        {
            if (_disposed || _zip.GetEntry($"{level}/tile{x}x{y}.png") is not { } entry)
                return null;
            using var stream = entry.Open();
            var bytes = new byte[entry.Length];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }

    bool _disposed;

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _zip.Dispose();
        }
    }
}
