using Renci.SshNet;
using Renci.SshNet.Common;

namespace SpiffoCON.Core.Files;

/// <summary>
/// Copies from the server only the parts of workshop items the catalog reads (mod.info, scripts,
/// translations, item icons), skipping models, sounds and maps. Files already cached with the same
/// size and date are not downloaded again.
/// </summary>
public sealed class SftpModFetcher(SftpSettings settings, string remoteWorkshopFolder, string cacheFolder)
{
    /// <summary>Folders that never contain anything the catalog needs.</summary>
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "models", "models_X", "anims", "anims_X", "sound", "sounds", "maps", "music", "videos",
        "client", "server", "ui", "AnimSets", "clothing", "radio",
    };

    public string CacheFolder { get; } = cacheFolder;

    public int FilesDownloaded { get; private set; }

    /// <summary>Returns workshop id → local folder for the items present on the server.</summary>
    public async Task<IReadOnlyDictionary<string, string>> FetchAsync(
        IReadOnlyCollection<string> workshopIds, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        var (client, _) = await SftpProbe.ConnectAsync(settings, ct).ConfigureAwait(false);
        using (client)
        {
            int n = 0;
            foreach (var id in workshopIds)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                var remote = remoteWorkshopFolder.TrimEnd('/') + "/" + id;
                if (!await ExistsAsync(client, remote, ct).ConfigureAwait(false))
                    continue;
                progress?.Report($"SFTP: reading mod {n}/{workshopIds.Count} ({id})");
                var local = Path.Combine(CacheFolder, id);
                await CopyTreeAsync(client, remote, local, "", ct).ConfigureAwait(false);
                result[id] = local;
            }
        }
        return result;
    }

    async Task CopyTreeAsync(SftpClient client, string remoteDir, string localDir, string relative, CancellationToken ct)
    {
        List<Renci.SshNet.Sftp.ISftpFile> entries = [];
        try
        {
            await foreach (var f in client.ListDirectoryAsync(remoteDir, ct).ConfigureAwait(false))
                if (f.Name is not "." and not "..")
                    entries.Add(f);
        }
        catch (SftpPermissionDeniedException) { return; }
        catch (SftpPathNotFoundException) { return; }

        foreach (var entry in entries)
        {
            var rel = relative.Length == 0 ? entry.Name : relative + "/" + entry.Name;
            if (entry.IsDirectory)
            {
                if (!Skip.Contains(entry.Name))
                    await CopyTreeAsync(client, entry.FullName, localDir, rel, ct).ConfigureAwait(false);
                continue;
            }
            if (!entry.IsRegularFile || !Wanted(rel))
                continue;

            var target = Path.Combine(localDir, rel.Replace('/', Path.DirectorySeparatorChar));
            var info = new FileInfo(target);
            if (info.Exists && info.Length == entry.Length && info.LastWriteTimeUtc == entry.LastWriteTimeUtc)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using (var file = File.Create(target))
                await client.DownloadFileAsync(entry.FullName, file, ct).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(target, entry.LastWriteTimeUtc);
            FilesDownloaded++;
        }
    }

    static bool Wanted(string relative)
    {
        var name = Path.GetFileName(relative);
        if (name.Equals("mod.info", StringComparison.OrdinalIgnoreCase))
            return true;
        if (relative.Contains("/media/scripts/", StringComparison.OrdinalIgnoreCase))
            return name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        if (relative.Contains("/Translate/", StringComparison.OrdinalIgnoreCase))
            return name.StartsWith("ItemName", StringComparison.OrdinalIgnoreCase) || name.StartsWith("IG_UI", StringComparison.OrdinalIgnoreCase);
        if (relative.Contains("/media/textures/", StringComparison.OrdinalIgnoreCase))
            return name.StartsWith("Item_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    static async Task<bool> ExistsAsync(SftpClient client, string path, CancellationToken ct)
    {
        try
        {
            return await client.ExistsAsync(path, ct).ConfigureAwait(false);
        }
        catch (SftpPermissionDeniedException)
        {
            return false;
        }
    }
}
