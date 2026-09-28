using Renci.SshNet;
using Renci.SshNet.Common;

namespace SpiffoCON.Core.Files;

public sealed record SftpSettings(string Host, int Port, string User, string Password, string? TrustedHostKey);

public sealed record WorkshopFolder(string Path, int ItemCount);

public sealed record SftpProbeResult(
    string HostKeyFingerprint,
    string StartDirectory,
    IReadOnlyList<string> TopLevel,
    IReadOnlyList<string> ServerConfigs,
    IReadOnlyList<WorkshopFolder> WorkshopFolders,
    IReadOnlyList<string> ModFolders,
    IReadOnlyList<string> LogFolders,
    int ScannedDirectories,
    bool Truncated);

/// <summary>The server's SSH host key differs from the one saved at the first connection.</summary>
public sealed class SftpHostKeyMismatchException(string expected, string actual)
    : Exception($"The server's SSH key changed (saved {expected}, now {actual}). If the host did not tell you about a change, do not trust it.")
{
    public string Actual { get; } = actual;
}

/// <summary>
/// Connects over SFTP and looks around (without reading file contents) for what SpiffoCON can use:
/// server configs, workshop mod folders and logs. Hosts lay their files out differently, so this
/// searches instead of assuming paths.
/// </summary>
public static class SftpProbe
{
    const int MaxDepth = 6;
    const int MaxDirectories = 400;

    /// <summary>Folders that hold thousands of files and nothing the probe needs.</summary>
    static readonly HashSet<string> NoDescend = new(StringComparer.OrdinalIgnoreCase)
    {
        "Saves", "backups", "Logs", "db", "media", "jre", "jre64", "java", "natives",
        "linux32", "linux64", "win32", "win64", "proc", "sys", "dev", ".cache", "Lua",
    };

    /// <summary>Connects, checking the host key against <see cref="SftpSettings.TrustedHostKey"/>.</summary>
    public static async Task<(SftpClient Client, string Fingerprint)> ConnectAsync(SftpSettings settings, CancellationToken ct)
    {
        var fingerprint = "";
        var info = new PasswordConnectionInfo(settings.Host, settings.Port, settings.User, settings.Password)
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        // keep-alives make a silently dead link fail instead of hanging a copy forever
        var client = new SftpClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(15) };
        client.HostKeyReceived += (_, e) =>
        {
            fingerprint = e.HostKeyName + " SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = settings.TrustedHostKey is null || settings.TrustedHostKey == fingerprint;
        };

        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
            return (client, fingerprint);
        }
        catch (SshConnectionException) when (settings.TrustedHostKey is not null && fingerprint.Length > 0 && fingerprint != settings.TrustedHostKey)
        {
            client.Dispose();
            throw new SftpHostKeyMismatchException(settings.TrustedHostKey, fingerprint);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public static async Task<SftpProbeResult> RunAsync(SftpSettings settings, CancellationToken ct = default)
    {
        var (client, fingerprint) = await ConnectAsync(settings, ct).ConfigureAwait(false);
        using var _ = client;

        var start = client.WorkingDirectory;
        var topLevel = new List<string>();
        var configs = new List<string>();
        var workshop = new List<WorkshopFolder>();
        var mods = new List<string>();
        var logs = new List<string>();
        int scanned = 0;
        bool truncated = false;

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((start, 0));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (scanned >= MaxDirectories)
            {
                truncated = true;
                break;
            }
            var (path, depth) = queue.Dequeue();
            scanned++;

            var entries = await ListAsync(client, path, ct).ConfigureAwait(false);
            if (depth == 0)
                topLevel.AddRange(entries.Select(e => e.IsDirectory ? e.Name + "/" : e.Name).Order(StringComparer.OrdinalIgnoreCase));

            bool isServerFolder = Name(path).Equals("Server", StringComparison.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!entry.IsDirectory)
                {
                    if (isServerFolder && entry.Name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                        configs.Add(entry.FullName);
                    continue;
                }

                if (entry.Name == "108600")
                {
                    // steamapps/workshop/content/108600/<workshop id>: count, don't descend
                    var items = await ListAsync(client, entry.FullName, ct).ConfigureAwait(false);
                    workshop.Add(new WorkshopFolder(entry.FullName, items.Count(i => i.IsDirectory)));
                    continue;
                }
                if (entry.Name.Equals("Logs", StringComparison.OrdinalIgnoreCase))
                    logs.Add(entry.FullName);
                if (entry.Name.Equals("mods", StringComparison.OrdinalIgnoreCase) && Name(path).Equals("Zomboid", StringComparison.OrdinalIgnoreCase))
                    mods.Add(entry.FullName);

                if (depth + 1 <= MaxDepth && !NoDescend.Contains(entry.Name))
                    queue.Enqueue((entry.FullName, depth + 1));
            }
        }

        client.Disconnect();
        return new SftpProbeResult(fingerprint, start, topLevel, configs, workshop, mods, logs, scanned, truncated);
    }

    static async Task<List<Entry>> ListAsync(SftpClient client, string path, CancellationToken ct)
    {
        var list = new List<Entry>();
        try
        {
            await foreach (var file in client.ListDirectoryAsync(path, ct).ConfigureAwait(false))
            {
                if (file.Name is "." or "..")
                    continue;
                // symlinks are not followed, so the scan can't loop
                list.Add(new Entry(file.Name, file.FullName, file.IsDirectory && !file.IsSymbolicLink));
            }
        }
        catch (SftpPermissionDeniedException) { }
        catch (SftpPathNotFoundException) { }
        // another error on one folder ("Failure") skips it; a dropped connection still stops the scan
        catch (SshException) when (client.IsConnected) { }
        return list;
    }

    static string Name(string path)
    {
        var trimmed = path.TrimEnd('/');
        int slash = trimmed.LastIndexOf('/');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    readonly record struct Entry(string Name, string FullName, bool IsDirectory);
}
