using SpiffoCON.Core.Files;

namespace SpiffoCON.Core.Map;

/// <summary>
/// Where the files of the game's map are: the vanilla map folder (media/maps/Muldraugh, KY) of a game or
/// dedicated server install, or SpiffoCON's copy downloaded from the server. Nothing of the game's is
/// shipped with SpiffoCON.
/// </summary>
public sealed record MapFiles(string MapFolder, string? LabelsJson)
{
    public const string VanillaMap = "Muldraugh, KY";
    public const string WorldMapXml = "worldmap.xml";
    public const string ForestPyramid = "forest.pyramid.zip";
    public const string Pyramid = "pyramid.zip";
    public const string Annotations = "worldmap-annotations.lua";
    public const string LabelsFile = "MapLabel.json";

    /// <summary>Needed for the map; the satellite pyramid (about 50 MB) is optional.</summary>
    public static readonly string[] Required = [WorldMapXml, ForestPyramid, Annotations];

    public string WorldMap => Path.Combine(MapFolder, WorldMapXml);
    public string Forest => Path.Combine(MapFolder, ForestPyramid);
    public string Satellite => Path.Combine(MapFolder, Pyramid);
    public string AnnotationsLua => Path.Combine(MapFolder, Annotations);

    public bool HasSatellite => File.Exists(Satellite);

    static bool Complete(string folder) => Required.All(f => File.Exists(Path.Combine(folder, f)));

    /// <summary>
    /// A game folder, its media or maps folder, or the map folder itself; null when there is no
    /// complete vanilla map there.
    /// </summary>
    public static MapFiles? FromFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return null;
        foreach (var candidate in new[]
                 {
                     folder,
                     Path.Combine(folder, VanillaMap),
                     Path.Combine(folder, "maps", VanillaMap),
                     Path.Combine(folder, "media", "maps", VanillaMap),
                 })
        {
            if (!Complete(candidate))
                continue;
            // <game>/media/maps/<map> → <game>/media/lua/shared/Translate/EN/MapLabel.json
            var media = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(candidate)));
            var labels = media is null ? null : Path.Combine(media, "lua", "shared", "Translate", "EN", LabelsFile);
            // SpiffoCON's copy keeps it next to the map files
            var local = Path.Combine(candidate, LabelsFile);
            return new MapFiles(candidate, File.Exists(local) ? local : labels is not null && File.Exists(labels) ? labels : null);
        }
        return null;
    }

    /// <summary>The server's install root from its workshop folder (…/steamapps/workshop/content/108600).</summary>
    public static string? RemoteRootFromWorkshop(string? workshopFolder)
    {
        if (string.IsNullOrEmpty(workshopFolder))
            return null;
        int i = workshopFolder.IndexOf("/steamapps/", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? null : workshopFolder[..i];
    }

    /// <summary>Remote path → local name, for the files to copy from a server install.</summary>
    public static IReadOnlyList<(string Remote, string Local)> RemoteFiles(string root, bool satellite)
    {
        var map = $"{root.TrimEnd('/')}/media/maps/{VanillaMap}/";
        var files = Required.Select(f => (map + f, f)).ToList();
        files.Add(($"{root.TrimEnd('/')}/media/lua/shared/Translate/EN/{LabelsFile}", LabelsFile));
        if (satellite)
            files.Add((map + Pyramid, Pyramid));
        return files;
    }

    /// <summary>
    /// Copies the map files of a server install into <paramref name="localFolder"/>. A file already there
    /// with the same size is kept. Returns false when the server has no map at <paramref name="root"/>.
    /// </summary>
    public static async Task<bool> DownloadAsync(IRemoteFileSystem fs, string root, string localFolder, bool satellite,
        IProgress<string>? progress, CancellationToken ct)
    {
        var mapFolder = $"{root.TrimEnd('/')}/media/maps/{VanillaMap}";
        var listing = await fs.ListAsync(mapFolder, ct).ConfigureAwait(false);
        if (listing is null || !Required.All(f => listing.Any(e => e.IsFile && e.Name == f)))
            return false;
        var sizes = listing.Where(e => e.IsFile).ToDictionary(e => e.Name, e => e.Length);
        var translate = await fs.ListAsync($"{root.TrimEnd('/')}/media/lua/shared/Translate/EN", ct).ConfigureAwait(false);
        if (translate?.FirstOrDefault(e => e.IsFile && e.Name == LabelsFile) is { } labels)
            sizes[LabelsFile] = labels.Length;

        Directory.CreateDirectory(localFolder);
        foreach (var (remote, local) in RemoteFiles(root, satellite))
        {
            if (!sizes.TryGetValue(local, out var size))
                continue;
            var target = Path.Combine(localFolder, local);
            if (File.Exists(target) && new FileInfo(target).Length == size)
                continue;
            progress?.Report($"Downloading {local} ({size / 1048576.0:0.0} MB)...");
            var part = target + ".part";
            await using (var stream = File.Create(part))
                await fs.DownloadAsync(remote, stream, ct).ConfigureAwait(false);
            if (new FileInfo(part).Length != size)
            {
                File.Delete(part);
                throw new IOException($"{local}: the download was cut short.");
            }
            Replace(part, target);
        }
        return true;
    }

    /// <summary>
    /// Puts a downloaded file in place. The copy being drawn is open (MapPyramid, shared for delete): Windows
    /// won't overwrite it, but it can step aside under another name and go when the map lets it.
    /// </summary>
    public static void Replace(string part, string target)
    {
        if (!File.Exists(target))
        {
            File.Move(part, target);
            return;
        }
        var old = $"{target}.{Guid.NewGuid():N}.old";
        File.Move(target, old);
        try
        {
            File.Move(part, target);
        }
        catch
        {
            File.Move(old, target);
            throw;
        }
        try
        {
            File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // held by something else: a leftover, not a failed download
        }
    }
}
