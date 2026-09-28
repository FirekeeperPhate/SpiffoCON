using System.Text.RegularExpressions;

namespace SpiffoCON.Core.Catalog;

/// <summary>
/// One mod inside a workshop item (workshop/content/108600/&lt;id&gt;/mods/&lt;folder&gt;).
/// B42 mods keep content in "common" plus version folders ("42", "42.13"); B41 mods keep it at
/// the mod root. <see cref="ContentRoots"/> lists the folders that hold "media", in load order.
/// </summary>
public sealed partial record ModInfo(
    string Id,
    string Name,
    string Folder,
    string? WorkshopId,
    IReadOnlyList<string> ContentRoots,
    IReadOnlyList<string> Requires,
    bool LegacyLayout)
{
    /// <summary>All mods in a workshop item folder (or in a Zomboid/mods folder).</summary>
    public static IReadOnlyList<ModInfo> Scan(string folder, string? workshopId, Version? gameVersion = null)
    {
        var modsFolder = Path.Combine(folder, "mods");
        if (!Directory.Exists(modsFolder))
            modsFolder = folder; // Zomboid/mods holds mod folders directly
        var result = new List<ModInfo>();
        foreach (var modFolder in Directory.EnumerateDirectories(modsFolder))
        {
            var mod = Read(modFolder, workshopId, gameVersion);
            if (mod is not null)
                result.Add(mod);
        }
        return result;
    }

    public static ModInfo? Read(string modFolder, string? workshopId, Version? gameVersion = null)
    {
        var versionFolder = PickVersionFolder(modFolder, gameVersion);
        var common = Path.Combine(modFolder, "common");
        var roots = new List<string>();
        string? infoFile = null;
        bool legacy = false;

        if (versionFolder is not null)
        {
            if (Directory.Exists(common))
                roots.Add(common);
            roots.Add(versionFolder);
            infoFile = Path.Combine(versionFolder, "mod.info");
        }
        if (infoFile is null || !File.Exists(infoFile))
        {
            var rootInfo = Path.Combine(modFolder, "mod.info");
            if (File.Exists(rootInfo))
            {
                infoFile = rootInfo;
                if (versionFolder is null)
                {
                    legacy = true;
                    roots.Add(modFolder);
                }
            }
        }
        if (infoFile is null || !File.Exists(infoFile))
            return null;

        var values = ParseInfo(File.ReadAllLines(infoFile));
        // an empty "id=" or "name=" counts as missing
        var id = values.GetValueOrDefault("id") is { Length: > 0 } given ? given : Path.GetFileName(modFolder);
        var requires = (values.GetValueOrDefault("require") ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeId)
            .Where(r => r.Length > 0)
            .ToList();
        return new ModInfo(NormalizeId(id), values.GetValueOrDefault("name") is { Length: > 0 } name ? name : id, modFolder, workshopId, roots, requires, legacy);
    }

    /// <summary>B42 writes mod ids with a leading backslash in Mods= and require=.</summary>
    public static string NormalizeId(string id) => id.Trim().TrimStart('\\').Trim();

    /// <summary>
    /// The highest "42…" folder not newer than the game (or simply the highest when the game
    /// version is unknown), mirroring how B42 picks versioned mod content.
    /// </summary>
    static string? PickVersionFolder(string modFolder, Version? gameVersion) =>
        PickVersionName(Directory.EnumerateDirectories(modFolder).Select(d => Path.GetFileName(d)), gameVersion) is { } name
            ? Path.Combine(modFolder, name)
            : null;

    /// <summary>Of a mod folder's subfolder names, the version folder the game would load (null: none).</summary>
    public static string? PickVersionName(IEnumerable<string> folderNames, Version? gameVersion)
    {
        string? best = null;
        Version? bestVersion = null;
        foreach (var name in folderNames)
        {
            if (!VersionName().IsMatch(name))
                continue;
            // TryParse: a folder like "42.99999999999" overflows
            if (!Version.TryParse(name.Contains('.') ? name : name + ".0", out var version))
                continue;
            if (version.Major < 42)
                continue;
            if (gameVersion is not null && version > gameVersion)
                continue;
            if (bestVersion is null || version > bestVersion)
            {
                best = name;
                bestVersion = version;
            }
        }
        return best;
    }

    static Dictionary<string, string> ParseInfo(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            // "require" may repeat; keep them all
            values[key] = values.TryGetValue(key, out var old) && key.Equals("require", StringComparison.OrdinalIgnoreCase)
                ? old + "," + line[(eq + 1)..].Trim()
                : line[(eq + 1)..].Trim();
        }
        return values;
    }

    [GeneratedRegex(@"^\d+(\.\d+){0,3}$")]
    private static partial Regex VersionName();
}
