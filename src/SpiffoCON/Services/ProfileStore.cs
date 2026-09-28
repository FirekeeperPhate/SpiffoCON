using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpiffoCON.Services;

public sealed class ServerProfile
{
    public string Host { get; set; } = "";
    public int RconPort { get; set; } = 27015;
    public bool RememberPasswords { get; set; } = true;

    /// <summary>DPAPI-encrypted (current Windows user), base64.</summary>
    public string? RconPassword { get; set; }

    public bool SftpEnabled { get; set; }
    public int SftpPort { get; set; } = 22;
    public string SftpUser { get; set; } = "";

    /// <summary>Some hosts (e.g. Indifferent Broccoli) use the SFTP password for RCON too.</summary>
    public bool SftpSamePassword { get; set; } = true;

    public string? SftpPassword { get; set; }

    /// <summary>SSH host key seen at the first SFTP connection (trust on first use).</summary>
    public string? SftpHostKey { get; set; }

    /// <summary>The server's workshop/content/108600 folder found by the SFTP probe.</summary>
    public string? SftpWorkshopFolder { get; set; }
}

/// <summary>Stores the profile in %APPDATA%\SpiffoCON; passwords are encrypted with DPAPI.</summary>
public static class ProfileStore
{
    static readonly byte[] Entropy = "SpiffoCON profile"u8.ToArray();
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>SPIFFOCON_PROFILE_DIR overrides the location (UI tests).</summary>
    public static string Folder { get; } =
        Environment.GetEnvironmentVariable("SPIFFOCON_PROFILE_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpiffoCON");

    static string FilePath => Path.Combine(Folder, "profile.json");

    public static ServerProfile Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<ServerProfile>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new();
    }

    public static void Save(ServerProfile profile)
    {
        Directory.CreateDirectory(Folder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, Json));
        File.Move(temp, FilePath, overwrite: true);
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
