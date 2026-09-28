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
    IReadOnlyList<string> Warnings);

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

    string SftpCache => Path.Combine(DataFolder, "cache", "sftp");
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

        // 1. SFTP: the files the server really runs
        if (options.Sftp is not null && options.RemoteWorkshopFolder is not null && ids.Count > 0)
        {
            try
            {
                var fetcher = new SftpModFetcher(options.Sftp, options.RemoteWorkshopFolder, SftpCache);
                foreach (var (id, folder) in await fetcher.FetchAsync(ids, progress, ct).ConfigureAwait(false))
                    folders[id] = (ModFileSource.Sftp, folder);
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
        IReadOnlyDictionary<string, WorkshopItemInfo> details = new Dictionary<string, WorkshopItemInfo>();
        try
        {
            if (ids.Count > 0)
                details = await new WorkshopApi(http).GetDetailsAsync(ids, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            warnings.Add("Steam Workshop info unavailable: " + ex.Message);
        }

        var remaining = ids.Where(id => !folders.ContainsKey(id)).ToList();
        if (options.UseSteamCmd && remaining.Count > 0)
            await UseSteamCmdAsync(remaining, details, folders, options, progress, warnings, ct).ConfigureAwait(false);

        // mods in the server's load order
        progress?.Report("Reading mod scripts...");
        var mods = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (!folders.TryGetValue(id, out var f))
                continue;
            foreach (var mod in ModInfo.Scan(f.Folder, id, options.GameVersion))
                mods.TryAdd(mod.Id, mod);
        }

        var builder = new CatalogBuilder(options.Language);
        builder.ImportSnapshot(ReadSnapshot());
        builder.AddIconFolder(VanillaIconFolder);
        var missing = new List<string>();
        foreach (var modId in server.Mods)
        {
            if (mods.TryGetValue(modId, out var mod))
                builder.AddMod(mod);
            else
                missing.Add(modId);
        }

        var reports = ids.Select(id => new ModSourceReport(
            id,
            details.TryGetValue(id, out var d) && d.Exists ? d.Title : id,
            folders.TryGetValue(id, out var f) ? f.Source : ModFileSource.Missing,
            folders.TryGetValue(id, out var g) ? g.Folder : null)).ToList();
        return new CatalogResult(builder.Build(), reports, missing, warnings);
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
            // without Steam info (offline) a cached copy is used as is
            bool stale = info is { Exists: true } && (!state.TryGetValue(id, out var stamp) || stamp < info.Updated.ToUnixTimeSeconds());
            if (have && !stale)
                folders[id] = (ModFileSource.SteamCmd, cached);
            else if (info is null || info.Exists) // removed or private items can't be downloaded
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
                warnings.Add($"SteamCMD could not download workshop item {id}.");
            SaveState(state);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warnings.Add("SteamCMD: " + ex.Message);
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
