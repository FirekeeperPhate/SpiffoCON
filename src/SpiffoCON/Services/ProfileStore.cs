using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpiffoCON.Services;

public sealed class ServerProfile : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    string _name = "";
    string _host = "";

    /// <summary>The name shown in the server list.</summary>
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; Changed(nameof(Name)); Changed(nameof(DisplayName)); } }
    }

    public string Host
    {
        get => _host;
        set { if (_host != value) { _host = value; Changed(nameof(Host)); Changed(nameof(DisplayName)); } }
    }

    [JsonIgnore]
    public string DisplayName =>
        Name.Trim() is { Length: > 0 } name ? name : Host.Trim() is { Length: > 0 } host ? host : "(unnamed)";

    public int RconPort { get; set; } = 27015;
    public bool RememberPasswords { get; set; } = true;

    /// <summary>DPAPI-encrypted (current Windows user), base64.</summary>
    public string? RconPassword { get; set; }

    public bool SftpEnabled { get; set; }

    /// <summary>Some hosts serve SFTP from another address than the game server.</summary>
    public bool SftpCustomHost { get; set; }

    public string SftpHost { get; set; } = "";

    /// <summary>The SFTP host <see cref="SftpHostKey"/> was seen on (null: older profiles, the server host).</summary>
    public string? SftpHostKeyFor { get; set; }

    /// <summary>The SFTP host the remembered folders below were found on.</summary>
    public string? SftpFoldersHost { get; set; }
    public int SftpPort { get; set; } = 22;
    public string SftpUser { get; set; } = "";

    /// <summary>Some hosts (e.g. Indifferent Broccoli) use the SFTP password for RCON too.</summary>
    public bool SftpSamePassword { get; set; } = true;

    public string? SftpPassword { get; set; }

    /// <summary>SSH host key seen at the first SFTP connection (trust on first use).</summary>
    public string? SftpHostKey { get; set; }

    /// <summary>The server's workshop/content/108600 folder found by the SFTP probe.</summary>
    public string? SftpWorkshopFolder { get; set; }

    /// <summary>The server's &lt;name&gt;_SandboxVars.lua chosen in the Sandbox tab.</summary>
    public string? SftpSandboxPath { get; set; }

    /// <summary>The server's Zomboid/Logs folder found over SFTP.</summary>
    public string? SftpLogsFolder { get; set; }

    /// <summary>The server's Zomboid/Lua folder, where the bridge mod exchanges files.</summary>
    public string? SftpLuaFolder { get; set; }

    /// <summary>The server's Zomboid folder (db, Saves), for the Accounts tab.</summary>
    public string? SftpZomboidFolder { get; set; }

    /// <summary>
    /// A copy under a new id and name. Folders found over SFTP belong to the original server
    /// (another server on the same host has its own), so the copy looks them up again.
    /// </summary>
    public ServerProfile Duplicate(string name)
    {
        var copy = JsonSerializer.Deserialize<ServerProfile>(JsonSerializer.Serialize(this))!;
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = name;
        copy.SftpHostKeyFor ??= Host;
        copy.SftpFoldersHost = null;
        copy.SftpWorkshopFolder = null;
        copy.SftpSandboxPath = null;
        copy.SftpLogsFolder = null;
        copy.SftpLuaFolder = null;
        copy.SftpZomboidFolder = null;
        return copy;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Which desktop notifications to show (for every server).</summary>
public sealed class NotificationSettings
{
    public bool PlayerJoins { get; set; } = true;

    public bool Chat { get; set; } = true;

    /// <summary>Words that make a chat message worth a notification; empty: every message.</summary>
    public string ChatWords { get; set; } = "admin";

    public bool Connection { get; set; } = true;

    public bool OnlyWhenInactive { get; set; } = true;
}

/// <summary>The saved servers and the one in use.</summary>
public sealed class ServerBook
{
    public string? Selected { get; set; }

    public ObservableCollection<ServerProfile> Servers { get; set; } = [];

    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>A game folder chosen for the Map tab (the same on every server).</summary>
    public string? MapFolder { get; set; }

    /// <summary>Passwords typed in this session, by server id, also when they are not remembered on disk.</summary>
    [JsonIgnore]
    public Dictionary<string, (string? Rcon, string? Sftp)> SessionPasswords { get; } = [];

    /// <summary>The selected server; adds an empty one when the book is empty.</summary>
    [JsonIgnore]
    public ServerProfile Current
    {
        get
        {
            if (Servers.Count == 0)
                Servers.Add(new ServerProfile { Name = "My server" });
            var current = Servers.FirstOrDefault(s => s.Id == Selected) ?? Servers[0];
            Selected = current.Id;
            return current;
        }
    }
}

/// <summary>Stores the server list in %APPDATA%\SpiffoCON; passwords are encrypted with DPAPI.</summary>
public static class ProfileStore
{
    static readonly byte[] Entropy = "SpiffoCON profile"u8.ToArray();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>SPIFFOCON_PROFILE_DIR overrides the location (UI tests).</summary>
    public static string Folder { get; } =
        Environment.GetEnvironmentVariable("SPIFFOCON_PROFILE_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpiffoCON");

    static string BookPath => Path.Combine(Folder, "servers.json");

    /// <summary>The single profile of versions up to 0.9.2; read once, then left alone.</summary>
    static string LegacyPath => Path.Combine(Folder, "profile.json");

    /// <summary>Why the server list could not be read at start-up (null: it could).</summary>
    public static string? LoadProblem { get; private set; }

    /// <summary>The list exists but could not be read (locked...): saving would wipe it.</summary>
    static bool _readOnly;

    public static ServerBook Load()
    {
        if (File.Exists(BookPath))
        {
            try
            {
                var book = JsonSerializer.Deserialize<ServerBook>(File.ReadAllText(BookPath)) ?? new();
                // hand edits may leave nulls behind
                book.Servers ??= [];
                book.Notifications ??= new();
                foreach (var bad in book.Servers.Where(s => s is null || string.IsNullOrEmpty(s.Id)).ToList())
                    book.Servers.Remove(bad);
                return book;
            }
            catch (JsonException)
            {
                if (SetAside(BookPath) is { } aside)
                    LoadProblem = $"The server list could not be read and was moved to {Path.GetFileName(aside)}; starting with an empty list.";
                else
                {
                    _readOnly = true;
                    LoadProblem = "The server list could not be read; it is not overwritten this time.";
                }
                return new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _readOnly = true;
                LoadProblem = $"The server list could not be read ({ex.Message}); it is not overwritten this time. Restart SpiffoCON.";
                return new();
            }
        }
        try
        {
            if (File.Exists(LegacyPath) && JsonSerializer.Deserialize<ServerProfile>(File.ReadAllText(LegacyPath)) is { } old)
            {
                old.Name ??= "";
                old.Host ??= "";
                if (old.Name.Length == 0)
                    old.Name = old.Host.Length > 0 ? old.Host : "My server";
                return new ServerBook { Selected = old.Id, Servers = [old] };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // the old profile stays where it is
        }
        return new();
    }

    /// <summary>Renames a file that can't be read to name.bad-yyyyMMdd-HHmmss; returns the new path.</summary>
    internal static string? SetAside(string path)
    {
        var aside = $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            File.Move(path, aside);
            return aside;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(ServerBook book)
    {
        if (_readOnly)
            throw new IOException("the server list could not be read at start-up, so it is not overwritten");
        Directory.CreateDirectory(Folder);
        var temp = BookPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(book, Json));
        File.Move(temp, BookPath, overwrite: true);
    }

    public static string? Protect(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    /// <summary>Empty when missing or unreadable (e.g. the profile was copied from another PC).</summary>
    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}
