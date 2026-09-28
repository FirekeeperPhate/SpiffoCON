using System.IO;
using System.Text.Json;

namespace SpiffoCON.Services;

public sealed class KitItem
{
    public string FullType { get; set; } = "";

    /// <summary>The name when it was added (the item may belong to a mod another server lacks).</summary>
    public string Name { get; set; } = "";

    public int Count { get; set; } = 1;
}

public sealed class Kit
{
    public string Name { get; set; } = "";

    public List<KitItem> Items { get; set; } = [];
}

/// <summary>Item kits, shared by all servers of the list, in %APPDATA%\SpiffoCON\kits.json.</summary>
public static class KitStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static string FilePath => Path.Combine(ProfileStore.Folder, "kits.json");

    /// <summary>
    /// The saved kits. A file that can't be read is moved aside (kits.json.bad-…) rather than
    /// silently replaced by the next save.
    /// </summary>
    public static List<Kit> Load()
    {
        if (!File.Exists(FilePath))
            return [];
        try
        {
            var kits = JsonSerializer.Deserialize<List<Kit?>>(File.ReadAllText(FilePath)) ?? [];
            foreach (var kit in kits.OfType<Kit>())
            {
                kit.Name ??= "";
                kit.Items = (kit.Items ?? []).Where(i => i is not null && !string.IsNullOrWhiteSpace(i.FullType)).ToList();
                foreach (var item in kit.Items)
                    item.Name ??= "";
            }
            return kits.OfType<Kit>().ToList();
        }
        catch (JsonException)
        {
            if (ProfileStore.SetAside(FilePath) is null)
                ReadOnly = true;
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // locked or unreadable for now: keep the file, and don't overwrite it this session
            ReadOnly = true;
            return [];
        }
    }

    /// <summary>Set when the file exists but could not be read: saving would wipe it.</summary>
    public static bool ReadOnly { get; private set; }

    public static void Save(IEnumerable<Kit> kits)
    {
        if (ReadOnly)
            throw new IOException("kits.json could not be read at start-up, so it is not overwritten. Restart SpiffoCON.");
        Directory.CreateDirectory(ProfileStore.Folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(kits.ToList(), Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}
