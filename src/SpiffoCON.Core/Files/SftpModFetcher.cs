using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SpiffoCON.Core.Catalog;
using SpiffoCON.Core.Steam;

namespace SpiffoCON.Core.Files;

public sealed record SftpFetchStats(
    int Items,
    int UpToDate,
    int Read,
    int Found,
    int Folders,
    int Files,
    int Downloaded,
    long Bytes,
    int Connections,
    bool ManifestFound,
    TimeSpan Elapsed);

/// <param name="Folders">Workshop id → local copy, for the items found on the server.</param>
/// <param name="Error">Why reading stopped early (the up-to-date items are still returned).</param>
public sealed record SftpFetchResult(IReadOnlyDictionary<string, string> Folders, SftpFetchStats Stats, Exception? Error);

/// <summary>
/// Copies from the server only what the catalog reads: mod.info, media/scripts, Item_*.png icons
/// and the item name translations, and only from the folders the game loads (common plus the
/// newest 42.x folder, or the root of a B41 mod). Speed comes from three things:
/// <list type="bullet">
/// <item>items that did not change since the last copy are not visited at all: the server's own
/// workshop manifest (appworkshop_108600.acf) says when each item was updated, with Steam's
/// update time as the fallback;</item>
/// <item>only the useful folders are listed, instead of the whole item;</item>
/// <item>folders and files are shared among several SFTP connections, so the round trips to a
/// distant server overlap instead of adding up.</item>
/// </list>
/// Files already cached with the same size and date are not downloaded again, and files the
/// server no longer has are removed from the copy.
/// </summary>
public sealed class SftpModFetcher
{
    /// <summary>Bump when the copy rules change, so older copies are read again.</summary>
    const int RulesVersion = 2;

    readonly Func<CancellationToken, Task<IRemoteFileSystem>> _connect;
    readonly string _remoteRoot;

    public SftpModFetcher(SftpSettings settings, string remoteWorkshopFolder, string cacheFolder)
        : this(ct => SftpRemoteFileSystem.ConnectAsync(settings, ct), remoteWorkshopFolder, cacheFolder)
    {
    }

    public SftpModFetcher(Func<CancellationToken, Task<IRemoteFileSystem>> connect, string remoteWorkshopFolder, string cacheFolder)
    {
        _connect = connect;
        _remoteRoot = remoteWorkshopFolder.Replace('\\', '/').TrimEnd('/');
        CacheFolder = cacheFolder;
    }

    public string CacheFolder { get; }

    /// <summary>SFTP connections used at the same time (some hosts limit them; failed extra ones are skipped).</summary>
    public int Connections { get; init; } = 4;

    /// <summary>Translations copied besides EN.</summary>
    public string Language { get; init; } = "EN";

    public Version? GameVersion { get; init; }

    /// <summary>Steam's update time per item, used for items the server's manifest doesn't list.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset>? SteamUpdated { get; init; }

    string StateFile => Path.Combine(CacheFolder, "items.json");

    public async Task<SftpFetchResult> FetchAsync(
        IReadOnlyCollection<string> workshopIds, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var result = new Dictionary<string, string>();
        progress?.Report("SFTP: connecting...");
        var first = await _connect(ct).ConfigureAwait(false);
        var connections = new List<IRemoteFileSystem> { first };
        try
        {
            progress?.Report("SFTP: reading the server's workshop manifest...");
            var manifest = await ReadManifestAsync(first, ct).ConfigureAwait(false);
            var state = LoadState();

            var toRead = new List<string>();
            var prints = new Dictionary<string, string?>();
            foreach (var id in workshopIds.Distinct())
            {
                var print = prints[id] = Fingerprint(id, manifest);
                var local = LocalItem(id);
                if (print is not null && state.GetValueOrDefault(id) == print && Directory.Exists(local))
                    result[id] = local;
                else
                    toRead.Add(id);
            }
            int upToDate = result.Count;

            var run = new Run(this, progress, toRead.Count);
            Exception? error = null;
            if (toRead.Count > 0)
            {
                // the other connections, in parallel; a host that refuses them just gets fewer
                var extra = await Task.WhenAll(Enumerable.Range(0, Math.Max(0, Connections - 1)).Select(_ => TryConnectAsync(ct))).ConfigureAwait(false);
                connections.AddRange(extra.OfType<IRemoteFileSystem>());
                run.Connections = connections.Count;

                foreach (var id in toRead)
                    run.Enqueue((fs, c) => run.ItemAsync(fs, id, c));
                error = await run.RunAsync(connections, ct).ConfigureAwait(false);

                if (error is null)
                {
                    foreach (var id in toRead)
                    {
                        if (run.Found.TryGetValue(id, out var wanted))
                        {
                            RemoveStale(id, wanted);
                            result[id] = LocalItem(id);
                            if (prints[id] is { } print)
                                state[id] = print;
                            else
                                state.Remove(id);
                        }
                        else
                        {
                            state.Remove(id);
                        }
                    }
                    SaveState(state);
                }
            }

            var stats = new SftpFetchStats(
                workshopIds.Count, upToDate, toRead.Count, result.Count, run.Folders, run.Files, run.Downloaded, run.Bytes,
                connections.Count, manifest is not null, clock.Elapsed);
            return new SftpFetchResult(result, stats, error);
        }
        finally
        {
            foreach (var c in connections)
                c.Dispose();
        }
    }

    async Task<IRemoteFileSystem?> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            return await _connect(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    async Task<IReadOnlyDictionary<string, InstalledWorkshopItem>?> ReadManifestAsync(IRemoteFileSystem fs, CancellationToken ct)
    {
        if (WorkshopManifest.PathFor(_remoteRoot) is not { } path)
            return null;
        try
        {
            using var buffer = new MemoryStream();
            await fs.DownloadAsync(path, buffer, ct).ConfigureAwait(false);
            var items = WorkshopManifest.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
            return items.Count > 0 ? items : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // no manifest (or not readable): fall back to Steam's update times
            return null;
        }
    }

    /// <summary>What the copy of an item depends on; null when nothing tells whether it changed.</summary>
    string? Fingerprint(string id, IReadOnlyDictionary<string, InstalledWorkshopItem>? manifest)
    {
        string? source = manifest is not null && manifest.TryGetValue(id, out var installed)
            ? $"acf:{installed.TimeUpdated}:{installed.Manifest}"
            : SteamUpdated is not null && SteamUpdated.TryGetValue(id, out var updated) && updated != default
                ? $"steam:{updated.ToUnixTimeSeconds()}"
                : null;
        return source is null ? null : $"v{RulesVersion}|{source}|{Language.ToUpperInvariant()}|{GameVersion}";
    }

    string RemoteItem(string id) => _remoteRoot + "/" + id;

    string LocalItem(string id) => Path.Combine(CacheFolder, id);

    /// <summary>Deletes cached files the server no longer has (or the rules no longer copy).</summary>
    void RemoveStale(string id, ConcurrentDictionary<string, byte> wanted)
    {
        var root = LocalItem(id);
        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!wanted.ContainsKey(rel))
                File.Delete(file);
        }
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList())
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
    }

    Dictionary<string, string> LoadState()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize(File.ReadAllText(StateFile), FetchJson.Default.DictionaryStringString) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return [];
    }

    void SaveState(Dictionary<string, string> state)
    {
        Directory.CreateDirectory(CacheFolder);
        var temp = StateFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, FetchJson.Default.DictionaryStringString));
        File.Move(temp, StateFile, overwrite: true);
    }

    /// <summary>
    /// One pass over the items to read: a queue of small jobs (list a folder, download a file)
    /// shared by the connections. A job adds the jobs it finds.
    /// </summary>
    sealed class Run(SftpModFetcher owner, IProgress<string>? progress, int items)
    {
        readonly Channel<Func<IRemoteFileSystem, CancellationToken, Task>> _queue =
            Channel.CreateUnbounded<Func<IRemoteFileSystem, CancellationToken, Task>>();
        readonly Stopwatch _sinceReport = Stopwatch.StartNew();
        int _pending;
        int _folders, _files, _downloaded;
        long _bytes;

        /// <summary>Items found on the server → the relative paths copied for them.</summary>
        public ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Found { get; } = new();

        public int Connections { get; set; } = 1;
        public int Folders => _folders;
        public int Files => _files;
        public int Downloaded => _downloaded;
        public long Bytes => _bytes;

        public void Enqueue(Func<IRemoteFileSystem, CancellationToken, Task> job)
        {
            Interlocked.Increment(ref _pending);
            _queue.Writer.TryWrite(job);
        }

        /// <summary>Runs until the queue is empty; returns the first error, if any.</summary>
        public async Task<Exception?> RunAsync(IReadOnlyList<IRemoteFileSystem> connections, CancellationToken ct)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Exception? error = null;

            async Task WorkerAsync(IRemoteFileSystem fs)
            {
                try
                {
                    await foreach (var job in _queue.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                    {
                        try
                        {
                            await job(fs, cts.Token).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            // the first failure stops the others; what follows it is noise
                            if (!cts.IsCancellationRequested)
                            {
                                Interlocked.CompareExchange(ref error, ex, null);
                                cts.Cancel();
                            }
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref _pending) == 0)
                                _queue.Writer.TryComplete();
                        }
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                }
            }

            await Task.WhenAll(connections.Select(WorkerAsync)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return error;
        }

        async Task<IReadOnlyList<RemoteEntry>?> ListAsync(IRemoteFileSystem fs, string path, CancellationToken ct)
        {
            var entries = await fs.ListAsync(path, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _folders);
            Report();
            return entries;
        }

        void Report()
        {
            if (progress is null)
                return;
            lock (_sinceReport)
            {
                if (_sinceReport.ElapsedMilliseconds < 250)
                    return;
                _sinceReport.Restart();
            }
            progress.Report($"SFTP: reading {items} mod{(items == 1 ? "" : "s")} over {Connections} connection{(Connections == 1 ? "" : "s")}: "
                + $"{_folders} folders, {_downloaded} files downloaded");
        }

        static RemoteEntry? Child(IReadOnlyList<RemoteEntry> entries, string name, bool directory) =>
            entries.FirstOrDefault(e => (directory ? e.IsDirectory : e.IsFile) && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public async Task ItemAsync(IRemoteFileSystem fs, string id, CancellationToken ct)
        {
            var root = owner.RemoteItem(id);
            if (await ListAsync(fs, root, ct).ConfigureAwait(false) is not { } entries)
                return; // not on the server
            Found[id] = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            // workshop items keep mods in "mods"; ModInfo.Scan reads the item root otherwise
            var mods = Child(entries, "mods", directory: true)?.FullName ?? root;
            Enqueue((f, c) => ModsAsync(f, id, mods, c));
        }

        async Task ModsAsync(IRemoteFileSystem fs, string id, string path, CancellationToken ct)
        {
            foreach (var mod in await ListAsync(fs, path, ct).ConfigureAwait(false) ?? [])
                if (mod.IsDirectory)
                    Enqueue((f, c) => ModFolderAsync(f, id, mod.FullName, c));
        }

        async Task ModFolderAsync(IRemoteFileSystem fs, string id, string path, CancellationToken ct)
        {
            if (await ListAsync(fs, path, ct).ConfigureAwait(false) is not { } entries)
                return;
            // mod.info at the root: B41 mods, and B42 ones whose version folder lacks it
            if (Child(entries, "mod.info", directory: false) is { } info)
                File(id, info);

            var version = ModInfo.PickVersionName(entries.Where(e => e.IsDirectory).Select(e => e.Name), owner.GameVersion);
            if (version is null)
            {
                Root(id, entries);
                return;
            }
            if (Child(entries, "common", directory: true) is { } common)
                Enqueue((f, c) => RootAsync(f, id, common.FullName, c));
            var versioned = Child(entries, version, directory: true)!;
            Enqueue((f, c) => RootAsync(f, id, versioned.FullName, c));
        }

        async Task RootAsync(IRemoteFileSystem fs, string id, string path, CancellationToken ct)
        {
            if (await ListAsync(fs, path, ct).ConfigureAwait(false) is { } entries)
                Root(id, entries);
        }

        /// <summary>A folder holding mod.info and media.</summary>
        void Root(string id, IReadOnlyList<RemoteEntry> entries)
        {
            if (Child(entries, "mod.info", directory: false) is { } info)
                File(id, info);
            if (Child(entries, "media", directory: true) is { } media)
                Enqueue((f, c) => MediaAsync(f, id, media.FullName, c));
        }

        async Task MediaAsync(IRemoteFileSystem fs, string id, string path, CancellationToken ct)
        {
            if (await ListAsync(fs, path, ct).ConfigureAwait(false) is not { } entries)
                return;
            if (Child(entries, "scripts", directory: true) is { } scripts)
                Enqueue((f, c) => WalkAsync(f, id, scripts.FullName, IsScript, recursive: true, c));
            if (Child(entries, "textures", directory: true) is { } textures)
                Enqueue((f, c) => WalkAsync(f, id, textures.FullName, IsIcon, recursive: true, c));
            if (Child(entries, "lua", directory: true) is { } lua)
                Enqueue((f, c) => TranslateAsync(f, id, lua.FullName, c));
        }

        /// <summary>lua/shared/Translate/&lt;EN and the chosen language&gt;.</summary>
        async Task TranslateAsync(IRemoteFileSystem fs, string id, string lua, CancellationToken ct)
        {
            string path = lua;
            foreach (var name in new[] { "shared", "Translate" })
            {
                if (await ListAsync(fs, path, ct).ConfigureAwait(false) is not { } entries || Child(entries, name, directory: true) is not { } next)
                    return;
                path = next.FullName;
            }
            foreach (var language in await ListAsync(fs, path, ct).ConfigureAwait(false) ?? [])
                if (language.IsDirectory && (language.Name.Equals("EN", StringComparison.OrdinalIgnoreCase)
                        || language.Name.Equals(owner.Language, StringComparison.OrdinalIgnoreCase)))
                    Enqueue((f, c) => WalkAsync(f, id, language.FullName, IsTranslation, recursive: false, c));
        }

        async Task WalkAsync(IRemoteFileSystem fs, string id, string path, Func<string, bool> wanted,
            bool recursive, CancellationToken ct)
        {
            foreach (var entry in await ListAsync(fs, path, ct).ConfigureAwait(false) ?? [])
            {
                if (entry.IsDirectory)
                {
                    if (recursive)
                        Enqueue((f, c) => WalkAsync(f, id, entry.FullName, wanted, recursive, c));
                }
                else if (entry.IsFile && wanted(entry.Name))
                {
                    File(id, entry);
                }
            }
        }

        /// <summary>Records a file to keep and queues its download unless the cached copy matches.</summary>
        void File(string id, RemoteEntry entry)
        {
            var itemRoot = owner.RemoteItem(id) + "/";
            if (!entry.FullName.StartsWith(itemRoot, StringComparison.Ordinal))
                return;
            var rel = entry.FullName[itemRoot.Length..];
            // a server-side name must not reach outside the cache (backslashes are legal on Linux)
            if (rel.Contains('\\') || rel.Contains(':') || rel.Split('/').Any(p => p is "" or "." or ".."))
                return;
            var itemCache = Path.GetFullPath(owner.LocalItem(id));
            var target = Path.GetFullPath(Path.Combine(itemCache, rel.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(itemCache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return;

            if (!Found.TryGetValue(id, out var wanted) || !wanted.TryAdd(rel, 0))
                return;
            Interlocked.Increment(ref _files);
            var info = new FileInfo(target);
            if (info.Exists && info.Length == entry.Length && info.LastWriteTimeUtc == entry.LastWriteTimeUtc)
                return;
            Enqueue((fs, ct) => DownloadAsync(fs, entry, target, ct));
        }

        async Task DownloadAsync(IRemoteFileSystem fs, RemoteEntry entry, string target, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var part = target + ".part";
            await using (var file = System.IO.File.Create(part))
                await fs.DownloadAsync(entry.FullName, file, ct).ConfigureAwait(false);
            System.IO.File.Move(part, target, overwrite: true);
            System.IO.File.SetLastWriteTimeUtc(target, entry.LastWriteTimeUtc);
            Interlocked.Increment(ref _downloaded);
            Interlocked.Add(ref _bytes, entry.Length);
            Report();
        }

        static bool IsScript(string name) => name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

        static bool IsIcon(string name) =>
            name.StartsWith("Item_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        static bool IsTranslation(string name) =>
            name.StartsWith("ItemName", StringComparison.OrdinalIgnoreCase) || name.StartsWith("IG_UI", StringComparison.OrdinalIgnoreCase);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class FetchJson : System.Text.Json.Serialization.JsonSerializerContext;
