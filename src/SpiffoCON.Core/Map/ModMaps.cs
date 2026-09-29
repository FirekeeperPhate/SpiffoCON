using SpiffoCON.Core.Catalog;
using SpiffoCON.Core.Files;

namespace SpiffoCON.Core.Map;

/// <summary>
/// The map folders of mods (Map= names other than the base game's): media/maps/&lt;name&gt; inside a mod of a
/// workshop item, in its version folder (42.x), common, or the mod folder itself (B41 layout). Only
/// the files the world map uses are copied from a server: they are small next to the map's lots.
/// </summary>
public static class ModMaps
{
    public const string WorldMapXml = "worldmap.xml";

    public static readonly string[] Files = [WorldMapXml, "worldmap-forest.xml", "worldmap-annotations.lua"];

    /// <summary>A mod map folder under workshop folders (content/108600) on this PC; null when none has it.</summary>
    public static string? FindLocal(IEnumerable<string> workshopRoots, IReadOnlyCollection<string> items, string mapName)
    {
        foreach (var root in workshopRoots)
        {
            if (!Directory.Exists(root))
                continue;
            var ids = items.Count > 0 ? items : Directory.EnumerateDirectories(root).Select(d => Path.GetFileName(d)!);
            foreach (var id in ids)
            {
                var mods = Path.Combine(root, id, "mods");
                if (!Directory.Exists(mods))
                    continue;
                foreach (var mod in Directory.EnumerateDirectories(mods))
                {
                    var names = Directory.EnumerateDirectories(mod).Select(d => Path.GetFileName(d)!).ToList();
                    foreach (var media in MediaRoots(names))
                    {
                        var folder = Path.Combine(mod, media, "media", "maps", mapName);
                        if (File.Exists(Path.Combine(folder, WorldMapXml)))
                            return Path.GetFullPath(folder);
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Where a mod's media folder can be, the one the game prefers first ("" is the mod folder).</summary>
    static IEnumerable<string> MediaRoots(IReadOnlyCollection<string> subfolders)
    {
        if (ModInfo.PickVersionName(subfolders, null) is { } version)
            yield return version;
        if (subfolders.Contains("common", StringComparer.OrdinalIgnoreCase))
            yield return "common";
        if (subfolders.Contains("media", StringComparer.OrdinalIgnoreCase))
            yield return "";
    }

    /// <summary>SpiffoCON's copy of a mod map (downloaded from a server).</summary>
    public static string CacheFolder(string cacheRoot, string mapName) =>
        Path.Combine(cacheRoot, string.Concat(mapName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.'));

    public static string? FromCache(string cacheRoot, string mapName)
    {
        var folder = CacheFolder(cacheRoot, mapName);
        return File.Exists(Path.Combine(folder, WorldMapXml)) ? folder : null;
    }

    /// <summary>
    /// Finds the wanted map folders in the server's workshop items and copies their world map files into
    /// <paramref name="cacheRoot"/>. Returns map name → local folder for the ones found.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> DownloadAsync(IRemoteFileSystem fs, string remoteWorkshop,
        IReadOnlyList<string> items, IReadOnlyCollection<string> mapNames, string cacheRoot, IProgress<string>? progress, CancellationToken ct)
    {
        var wanted = new HashSet<string>(mapNames, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in items)
        {
            if (wanted.Count == 0)
                break;
            progress?.Report($"Looking for mod maps on the server (workshop item {id})...");
            var mods = await fs.ListAsync($"{remoteWorkshop.TrimEnd('/')}/{id}/mods", ct).ConfigureAwait(false);
            foreach (var mod in mods?.Where(m => m.IsDirectory) ?? [])
            {
                var sub = await fs.ListAsync(mod.FullName, ct).ConfigureAwait(false);
                if (sub is null)
                    continue;
                foreach (var media in MediaRoots(sub.Where(s => s.IsDirectory).Select(s => s.Name).ToList()))
                {
                    var mapsPath = media.Length == 0 ? $"{mod.FullName}/media/maps" : $"{mod.FullName}/{media}/media/maps";
                    var maps = await fs.ListAsync(mapsPath, ct).ConfigureAwait(false);
                    foreach (var map in maps?.Where(m => m.IsDirectory && wanted.Contains(m.Name)) ?? [])
                    {
                        var files = await fs.ListAsync(map.FullName, ct).ConfigureAwait(false);
                        if (files?.Any(f => f.IsFile && f.Name == WorldMapXml) != true)
                            continue;
                        progress?.Report($"Copying the map of {map.Name}...");
                        var local = CacheFolder(cacheRoot, map.Name);
                        Directory.CreateDirectory(local);
                        foreach (var file in files.Where(f => f.IsFile && Files.Contains(f.Name)))
                        {
                            var target = Path.Combine(local, file.Name);
                            if (File.Exists(target) && new FileInfo(target).Length == file.Length)
                                continue;
                            var part = target + ".part";
                            await using (var stream = File.Create(part))
                                await fs.DownloadAsync(file.FullName, stream, ct).ConfigureAwait(false);
                            File.Move(part, target, overwrite: true);
                        }
                        // a file the mod no longer has
                        foreach (var name in Files.Where(n => files.All(f => f.Name != n)))
                            File.Delete(Path.Combine(local, name));
                        found[map.Name] = local;
                        wanted.Remove(map.Name);
                    }
                }
            }
        }
        return found;
    }
}
