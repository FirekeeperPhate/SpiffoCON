using System.IO.Compression;
using System.Text.Json;
using SpiffoCON.Core.Commands;
using SpiffoCON.Core.Files;
using SpiffoCON.Core.Steam;

namespace SpiffoCON.Core.Catalog;

public enum ModFileSource { Sftp, LocalSteam, SteamCmd, Missing }

public sealed record ModSourceReport(string WorkshopId, string Title, ModFileSource Source, string? Folder);

public sealed record CatalogResult(
    IReadOnlyList<CatalogEntry> Entries,
    IReadOnlyList<ModSourceReport> Sources,
    /// <summary>Mods= ids whose files were not found in any source.</summary>
    IReadOnlyList<string> MissingMods,
    IReadOnlyList<string> Warnings)
{
    /// <summary>How the SFTP copy went (null when SFTP was not used).</summary>
    public SftpFetchStats? SftpStats { get; init; }
}

public sealed class CatalogLoadOptions
{
    public SftpSettings? Sftp { get; init; }

    /// <summary>The server's steamapps/workshop/content/108600 folder, from the SFTP probe.</summary>
    public string? RemoteWorkshopFolder { get; init; }

    public bool UseLocalSteam { get; init; } = true;
    public bool UseSteamCmd { get; init; } = true;

    /// <summary>
    /// Asked before SteamCMD downloads (whole mods, models included). Gets the total bytes and the
    /// items; returning false skips SteamCMD. Null means "don't ask".
    /// </summary>
    public Func<long, IReadOnlyList<WorkshopItemInfo>, Task<bool>>? ConfirmSteamCmd { get; init; }

    public string Language { get; init; } = "EN";

    /// <summary>Picks B42 versioned mod folders; null = the highest one.</summary>
    public Version? GameVersion { get; init; }
}

/// <summary>
/// Builds the catalog for a server: the bundled base-game snapshot plus the server's mods, in the
/// server's load order. Mod files come from SFTP, then the local Steam workshop folder, then
/// SteamCMD (cached in <paramref name="dataFolder"/>).
/// </summary>
public sealed class CatalogService(string dataFolder, HttpClient http)
{
    const string SnapshotResource = "SpiffoCON.Core.Data.vanilla-catalog.json.gz";

    public string DataFolder { get; } = dataFolder;

    /// <summary>Seconds before an unlisted item (no Steam date) is downloaded again.</summary>
    const long UnlistedMaxAge = 7 * 24 * 3600;

    string SftpCacheRoot => Path.Combine(DataFolder, "cache", "sftp");

    /// <summary>
    /// One copy per server (host, port, folder): two servers may run different versions of the
    /// same mod, and each copy's bookkeeping must describe its own server.
    /// </summary>
    string SftpCacheFor(SftpSettings sftp, string remoteFolder)
    {
        var key = $"{sftp.Host.ToLowerInvariant()}|{sftp.Port}|{remoteFolder.TrimEnd('/')}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..16];
        RemoveLegacySftpCache();
        // "s-": a hash made only of digits must not look like a 0.9.4 folder to the cleanup
        return Path.Combine(SftpCacheRoot, "s-" + hash.ToLowerInvariant());
    }

    /// <summary>
    /// Up to 0.9.4 the copies of all servers shared cache/sftp/&lt;workshop id&gt;: removed. Per-server
    /// copies unused for two months go too.
    /// </summary>
    void RemoveLegacySftpCache()
    {
        try
        {
            if (!Directory.Exists(SftpCacheRoot))
                return;
            foreach (var dir in Directory.EnumerateDirectories(SftpCacheRoot))
                if (Path.GetFileName(dir).All(char.IsAsciiDigit))
                    Directory.Delete(dir, recursive: true);
            var oldState = Path.Combine(SftpCacheRoot, "items.json");
            if (File.Exists(oldState))
                File.Delete(oldState);
            // copies of servers not loaded for two months (removed from the list, gone)
            foreach (var dir in Directory.EnumerateDirectories(SftpCacheRoot, "s-*"))
            {
                var state = Path.Combine(dir, "items.json");
                var used = File.Exists(state) ? File.GetLastWriteTimeUtc(state) : Directory.GetLastWriteTimeUtc(dir);
                if (DateTime.UtcNow - used > TimeSpan.FromDays(60))
                    Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // leftovers only cost disk space
        }
    }
    string SteamCmdFolder => Path.Combine(DataFolder, "steamcmd");
    string SteamCmdStateFile => Path.Combine(DataFolder, "cache", "steamcmd-items.json");

    /// <summary>Base-game icons extracted from the user's game install (Item_*.png).</summary>
    public string VanillaIconFolder => Path.Combine(DataFolder, "icons", "vanilla");

    /// <summary>The base game only (used before connecting, or when mods can't be read).</summary>
    public IReadOnlyList<CatalogEntry> LoadVanilla(string language = "EN")
    {
        var builder = new CatalogBuilder(language);
        builder.ImportSnapshot(ReadSnapshot());
        builder.AddIconFolder(VanillaIconFolder);
        return builder.Build();
    }

    public static string ReadSnapshot()
    {
        using var stream = typeof(CatalogService).Assembly.GetManifestResourceStream(SnapshotResource)
            ?? throw new InvalidOperationException("The base-game catalog is missing from the build.");
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    public async Task<CatalogResult> LoadAsync(ServerOptions server, CatalogLoadOptions options, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var ids = server.WorkshopItems;
        var folders = new Dictionary<string, (ModFileSource Source, string Folder)>();

        // titles and update times (also tell SFTP which cached copies are still current)
        IReadOnlyDictionary<string, WorkshopItemInfo> details = new Dictionary<string, WorkshopItemInfo>();
        try
        {
            if (ids.Count > 0)
            {
                progress?.Report("Asking Steam about the server's mods...");
                details = await new WorkshopApi(http).GetDetailsAsync(ids, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // offline, or a reply of an unexpected shape: titles and dates are only a bonus
            warnings.Add("Steam Workshop info unavailable: " + ex.Message);
        }

        // 1. SFTP: the files the server really runs
        SftpFetchStats? sftpStats = null;
        bool sftpBusy = false;
        if (options.Sftp is not null && options.RemoteWorkshopFolder is not null && ids.Count > 0)
        {
            try
            {
                var fetcher = new SftpModFetcher(options.Sftp, options.RemoteWorkshopFolder, SftpCacheFor(options.Sftp, options.RemoteWorkshopFolder))
                {
                    Language = options.Language,
                    GameVersion = options.GameVersion,
                };
                var fetched = await fetcher.FetchAsync(ids, progress, ct).ConfigureAwait(false);
                foreach (var (id, folder) in fetched.Folders)
                    folders[id] = (ModFileSource.Sftp, folder);
                sftpStats = fetched.Stats;
                sftpBusy = fetched.Stats.Busy;
                if (fetched.Error is { } error)
                    warnings.Add("SFTP stopped early: " + error.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add("SFTP: " + ex.Message);
            }
        }

        // 2. local Steam: mods the user is subscribed to
        if (options.UseLocalSteam)
        {
            var local = SteamLocator.FindWorkshopFolders();
            foreach (var id in ids.Where(id => !folders.ContainsKey(id)))
            {
                var found = local.Select(w => Path.Combine(w, id)).FirstOrDefault(Directory.Exists);
                if (found is not null)
                    folders[id] = (ModFileSource.LocalSteam, found);
            }
        }

        // 3. SteamCMD: anonymous download into our cache
        var remaining = ids.Where(id => !folders.ContainsKey(id)).ToList();
        // not while another window is copying the same mods: they are likely there in a moment
        if (options.UseSteamCmd && remaining.Count > 0 && !sftpBusy)
            await UseSteamCmdAsync(remaining, details, folders, options, progress, warnings, ct).ConfigureAwait(false);

        // mods in the server's load order
        progress?.Report("Reading mod scripts...");
        var mods = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (!folders.TryGetValue(id, out var f))
                continue;
            // one unreadable workshop item must not cost the catalog all the others
            try
            {
                foreach (var mod in ModInfo.Scan(f.Folder, id, options.GameVersion))
                    mods.TryAdd(mod.Id, mod);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Workshop item {id} could not be read: {ex.Message}");
            }
        }

        var builder = new CatalogBuilder(options.Language);
        builder.ImportSnapshot(ReadSnapshot());
        builder.AddIconFolder(VanillaIconFolder);
        var missing = new List<string>();
        foreach (var modId in server.Mods)
        {
            if (!mods.TryGetValue(modId, out var mod))
            {
                missing.Add(modId);
                continue;
            }
            try
            {
                builder.AddMod(mod);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Mod {modId} could not be read: {ex.Message}");
            }
        }

        var reports = ids.Select(id => new ModSourceReport(
            id,
            details.TryGetValue(id, out var d) && d.Exists ? d.Title : id,
            folders.TryGetValue(id, out var f) ? f.Source : ModFileSource.Missing,
            folders.TryGetValue(id, out var g) ? g.Folder : null)).ToList();
        return new CatalogResult(builder.Build(), reports, missing, warnings) { SftpStats = sftpStats };
    }

    async Task UseSteamCmdAsync(
        List<string> ids,
        IReadOnlyDictionary<string, WorkshopItemInfo> details,
        Dictionary<string, (ModFileSource, string)> folders,
        CatalogLoadOptions options,
        IProgress<string>? progress,
        List<string> warnings,
        CancellationToken ct)
    {
        var steamCmd = new SteamCmd(SteamCmdFolder, http);
        var state = LoadState();
        var toDownload = new List<string>();
        foreach (var id in ids)
        {
            var cached = Path.Combine(steamCmd.WorkshopFolder, id);
            bool have = Directory.Exists(cached) && state.ContainsKey(id);
            var info = details.GetValueOrDefault(id);
            // without Steam info (offline) a cached copy is used as is; an item Steam doesn't list
            // publicly (unlisted, like the SpiffoCON Bridge) has no date, so it is fetched again weekly
            state.TryGetValue(id, out var stamp);
            bool stale = info is { Exists: true }
                ? stamp < info.Updated.ToUnixTimeSeconds()
                : info is { Exists: false } && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - stamp > UnlistedMaxAge;
            if (have && !stale)
                folders[id] = (ModFileSource.SteamCmd, cached);
            else
                // also when the web API says "not found": it says so for unlisted items (e.g. the
                // SpiffoCON Bridge), which SteamCMD downloads fine; removed ones just fail there
                toDownload.Add(id);
        }
        if (toDownload.Count == 0)
            return;

        var items = toDownload.Select(id => details.GetValueOrDefault(id) ?? new WorkshopItemInfo(id, id, default, 0, true)).ToList();
        long total = items.Sum(i => i.Size);
        if (options.ConfirmSteamCmd is not null && !await options.ConfirmSteamCmd(total, items).ConfigureAwait(false))
        {
            // fall back to whatever older copy is cached
            foreach (var id in toDownload.Where(id => Directory.Exists(Path.Combine(steamCmd.WorkshopFolder, id))))
                folders[id] = (ModFileSource.SteamCmd, Path.Combine(steamCmd.WorkshopFolder, id));
            return;
        }

        progress?.Report($"SteamCMD: downloading {toDownload.Count} mods ({total / 1048576.0:0} MB)...");
        try
        {
            var done = await steamCmd.DownloadAsync(toDownload, progress, ct).ConfigureAwait(false);
            foreach (var (id, folder) in done)
            {
                folders[id] = (ModFileSource.SteamCmd, folder);
                state[id] = details.TryGetValue(id, out var info) && info.Exists
                    ? info.Updated.ToUnixTimeSeconds()
                    : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            }
            foreach (var id in toDownload.Where(id => !done.ContainsKey(id)))
                warnings.Add($"SteamCMD could not download workshop item {id}." + (UseOlderCopy(id) ? " Using the copy downloaded earlier." : ""));
            SaveState(state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            int older = toDownload.Count(id => !folders.ContainsKey(id) && UseOlderCopy(id));
            warnings.Add("SteamCMD: " + ex.Message + (older > 0 ? $" Using {older} copies downloaded earlier." : ""));
        }

        bool UseOlderCopy(string id)
        {
            var cached = Path.Combine(steamCmd.WorkshopFolder, id);
            if (!Directory.Exists(cached))
                return false;
            folders[id] = (ModFileSource.SteamCmd, cached);
            return true;
        }
    }

    Dictionary<string, long> LoadState()
    {
        try
        {
            if (File.Exists(SteamCmdStateFile))
                return JsonSerializer.Deserialize(File.ReadAllText(SteamCmdStateFile), CatalogJson.Default.DictionaryStringInt64) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return [];
    }

    void SaveState(Dictionary<string, long> state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SteamCmdStateFile)!);
        File.WriteAllText(SteamCmdStateFile, JsonSerializer.Serialize(state, CatalogJson.Default.DictionaryStringInt64));
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class CatalogJson : System.Text.Json.Serialization.JsonSerializerContext;
