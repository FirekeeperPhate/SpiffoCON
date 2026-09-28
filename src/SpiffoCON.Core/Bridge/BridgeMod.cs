namespace SpiffoCON.Core.Bridge;

/// <summary>
/// Puts the bridge mod where the game's Workshop uploader looks for items
/// (%USERPROFILE%\Zomboid\Workshop\SpiffoCONBridge).
/// </summary>
public static class BridgeMod
{
    public const string FolderName = "SpiffoCONBridge";
    public const string ModId = "SpiffoCONBridge";

    public static string DefaultWorkshopFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Zomboid", "Workshop");

    /// <summary>
    /// Copies the mod from <paramref name="source"/> into <paramref name="workshopFolder"/>. After
    /// the first upload the game writes "id=&lt;workshop id&gt;" into workshop.txt; that line is kept,
    /// otherwise the next upload would create a second Workshop item. Returns the target folder.
    /// </summary>
    public static string ExportForUpload(string source, string workshopFolder)
    {
        var target = Path.Combine(workshopFolder, FolderName);
        var workshopTxt = Path.Combine(target, "workshop.txt");
        var idLine = File.Exists(workshopTxt)
            ? File.ReadAllLines(workshopTxt).FirstOrDefault(l => l.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
            : null;

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }

        if (idLine is not null)
        {
            var lines = File.ReadAllLines(workshopTxt).Where(l => !l.StartsWith("id=", StringComparison.OrdinalIgnoreCase)).ToList();
            lines.Insert(1, idLine);
            File.WriteAllLines(workshopTxt, lines);
        }
        return target;
    }

    /// <summary>The Workshop id the game wrote after uploading, if any.</summary>
    public static string? ReadWorkshopId(string workshopFolder)
    {
        var workshopTxt = Path.Combine(workshopFolder, FolderName, "workshop.txt");
        if (!File.Exists(workshopTxt))
            return null;
        var line = File.ReadAllLines(workshopTxt).FirstOrDefault(l => l.StartsWith("id=", StringComparison.OrdinalIgnoreCase));
        return line?[3..].Trim() is { Length: > 0 } id ? id : null;
    }
}
