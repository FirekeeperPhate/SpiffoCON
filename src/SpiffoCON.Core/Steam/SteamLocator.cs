using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SpiffoCON.Core.Steam;

/// <summary>Finds the local Steam install, its library folders and Project Zomboid content in them.</summary>
public static partial class SteamLocator
{
    public const string ZomboidAppId = "108600";

    public static string? FindSteamRoot()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
            ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        return path is not null && Directory.Exists(path) ? Path.GetFullPath(path) : null;
    }

    /// <summary>Every Steam library folder (the Steam root included).</summary>
    public static IReadOnlyList<string> FindLibraries(string? steamRoot = null)
    {
        steamRoot ??= FindSteamRoot();
        if (steamRoot is null)
            return [];
        var libraries = new List<string> { steamRoot };
        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in LibraryPath().Matches(File.ReadAllText(vdf)))
            {
                var path = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(path))
                    libraries.Add(Path.GetFullPath(path));
            }
        }
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>steamapps/workshop/content/108600 folders that exist.</summary>
    public static IReadOnlyList<string> FindWorkshopFolders(string? steamRoot = null) =>
        FindLibraries(steamRoot)
            .Select(l => Path.Combine(l, "steamapps", "workshop", "content", ZomboidAppId))
            .Where(Directory.Exists)
            .ToList();

    /// <summary>The game (or dedicated server) install folder that contains media/scripts.</summary>
    public static string? FindGameFolder(string? steamRoot = null)
    {
        foreach (var library in FindLibraries(steamRoot))
        {
            foreach (var name in new[] { "ProjectZomboid", "Project Zomboid Dedicated Server" })
            {
                var dir = Path.Combine(library, "steamapps", "common", name);
                if (Directory.Exists(Path.Combine(dir, "media", "scripts")))
                    return dir;
            }
        }
        return null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();
}
