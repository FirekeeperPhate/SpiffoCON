using System.IO;
using System.Reflection;
using Microsoft.Win32;
using SpiffoCON.Core.Updates;

namespace SpiffoCON.Services;

/// <summary>This copy of SpiffoCON: its version, and how it was installed (which installer updates it).</summary>
public static class AppInstall
{
    public static Version Version { get; } =
        typeof(AppInstall).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    static string AppFolder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>The installer's AppId (installer\SpiffoCON.iss): Windows keeps its uninstall entry under it.</summary>
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{E1FBE2AD-C111-415A-A9DB-A11B483B6ACD}_is1";

    /// <summary>
    /// Installed by the installer (also in a folder chosen in the wizard): its uninstall entry names this folder,
    /// in the machine's registry (for everyone) or the user's (just for them).
    /// </summary>
    public static InstallKind Kind { get; } = FromRegistry() ?? InstallKind.Other;

    static InstallKind? FromRegistry()
    {
        foreach (var (hive, kind) in new[] { (RegistryHive.LocalMachine, InstallKind.AllUsers), (RegistryHive.CurrentUser, InstallKind.CurrentUser) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is string location
                    && string.Equals(Path.TrimEndingDirectorySeparator(location), AppFolder, StringComparison.OrdinalIgnoreCase))
                    return kind;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                // no access: not updatable in place
            }
        }
        return null;
    }

    /// <summary>The self-contained build (the Full installer) carries the .NET runtime next to the exe.</summary>
    public static bool SelfContained { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));
}
