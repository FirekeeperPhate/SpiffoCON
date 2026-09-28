using SpiffoCON.Core.Catalog;
using SpiffoCON.Core.Files;
using SpiffoCON.Core.Steam;

namespace SpiffoCON.Core.Tests;

/// <summary>A local folder standing in for the server, counting the operations.</summary>
sealed class LocalRemoteFileSystem(LocalRemoteFileSystem.Counters counters, string? failOn = null) : IRemoteFileSystem
{
    public sealed class Counters
    {
        public int Connections, Lists, Downloads;
        public readonly List<string> Listed = [];
    }

    public Task<IReadOnlyList<RemoteEntry>?> ListAsync(string path, CancellationToken ct)
    {
        Interlocked.Increment(ref counters.Lists);
        lock (counters.Listed)
            counters.Listed.Add(path);
        if (failOn is not null && path.EndsWith(failOn, StringComparison.Ordinal))
            throw new IOException("connection dropped");
        if (!Directory.Exists(path))
            return Task.FromResult<IReadOnlyList<RemoteEntry>?>(null);
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
            .Select(i => new RemoteEntry(i.Name, path + "/" + i.Name, i is DirectoryInfo, i is FileInfo,
                i is FileInfo f ? f.Length : 0, i.LastWriteTimeUtc))
            .ToList();
        return Task.FromResult<IReadOnlyList<RemoteEntry>?>(entries);
    }

    public async Task DownloadAsync(string path, Stream target, CancellationToken ct)
    {
        Interlocked.Increment(ref counters.Downloads);
        await using var source = File.OpenRead(path);
        await source.CopyToAsync(target, ct);
    }

    public void Dispose()
    {
    }
}

public sealed class SftpModFetcherTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "spiffocon-fetch-" + Guid.NewGuid().ToString("N"));
    readonly LocalRemoteFileSystem.Counters _counters = new();

    string Workshop => Path.Combine(_root, "server", "steamapps", "workshop", "content", "108600").Replace('\\', '/');
    string Cache => Path.Combine(_root, "cache");

    public SftpModFetcherTests()
    {
        // item 111: a B42 mod with common + two version folders, and B41 leftovers at the root
        var mod = Path.Combine(Workshop, "111", "mods", "ModA");
        Write(Path.Combine(mod, "mod.info"), "name=Mod A (B41)\nid=ModA");
        Write(Path.Combine(mod, "media", "scripts", "old.txt"), "module Old {}");
        Write(Path.Combine(mod, "common", "media", "scripts", "items", "a.txt"), "module A { item Knife { } }");
        Write(Path.Combine(mod, "common", "media", "models_X", "knife.fbx"), "mesh");
        Write(Path.Combine(mod, "42", "mod.info"), "name=Mod A 42\nid=ModA");
        Write(Path.Combine(mod, "42", "media", "scripts", "b.txt"), "module A {}");
        Write(Path.Combine(mod, "42.13", "mod.info"), "name=Mod A\nid=ModA");
        Write(Path.Combine(mod, "42.13", "media", "scripts", "c.txt"), "module A {}");
        Write(Path.Combine(mod, "42.13", "media", "textures", "Item_Knife.png"), "png");
        Write(Path.Combine(mod, "42.13", "media", "textures", "Icons", "Item_Fork.png"), "png");
        Write(Path.Combine(mod, "42.13", "media", "textures", "WorldItems", "Item_Knife.png"), "png");
        Write(Path.Combine(mod, "42.13", "media", "textures", "Knife_Diffuse.png"), "png");
        Write(Path.Combine(mod, "42.13", "media", "lua", "shared", "Translate", "EN", "ItemName.json"), "{}");
        Write(Path.Combine(mod, "42.13", "media", "lua", "shared", "Translate", "IT", "ItemName.json"), "{}");
        Write(Path.Combine(mod, "42.13", "media", "lua", "shared", "Translate", "EN", "Sandbox.json"), "{}");
        Write(Path.Combine(mod, "42.13", "media", "lua", "client", "ui.lua"), "-- ui");
        Write(Path.Combine(mod, "42.13", "media", "sound", "knife.ogg"), "ogg");

        // item 222: a B41-only mod
        Write(Path.Combine(Workshop, "222", "mods", "OldMod", "mod.info"), "name=Old\nid=OldMod");
        Write(Path.Combine(Workshop, "222", "mods", "OldMod", "media", "scripts", "o.txt"), "module O {}");

        WriteManifest(("111", 1000), ("222", 2000));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    void WriteManifest(params (string Id, long Updated)[] items)
    {
        var body = string.Concat(items.Select(i =>
            $"\t\t\"{i.Id}\"\n\t\t{{\n\t\t\t\"size\"\t\t\"1\"\n\t\t\t\"timeupdated\"\t\t\"{i.Updated}\"\n\t\t\t\"manifest\"\t\t\"9{i.Updated}\"\n\t\t}}\n"));
        Write(WorkshopManifest.PathFor(Workshop)!,
            $"\"AppWorkshop\"\n{{\n\t\"appid\"\t\t\"108600\"\n\t\"WorkshopItemsInstalled\"\n\t{{\n{body}\t}}\n}}\n");
    }

    SftpModFetcher Fetcher(string? failOn = null, int connections = 4, TimeSpan? lockWait = null) =>
        new(_ =>
        {
            Interlocked.Increment(ref _counters.Connections);
            return Task.FromResult<IRemoteFileSystem>(new LocalRemoteFileSystem(_counters, failOn));
        }, Workshop, Cache) { Connections = connections, LockWait = lockWait ?? TimeSpan.FromMinutes(2) };

    IEnumerable<string> Cached(string id) =>
        Directory.EnumerateFiles(Path.Combine(Cache, id), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(Cache, id), f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    [Fact]
    public void Reads_the_workshop_manifest()
    {
        var items = WorkshopManifest.Parse(File.ReadAllText(WorkshopManifest.PathFor(Workshop)!));
        Assert.Equal(1000, items["111"].TimeUpdated);
        Assert.Equal("92000", items["222"].Manifest);
        Assert.Empty(WorkshopManifest.Parse("not a manifest"));
        Assert.Equal("/srv/steamapps/workshop/appworkshop_108600.acf", WorkshopManifest.PathFor("/srv/steamapps/workshop/content/108600/"));
        Assert.Null(WorkshopManifest.PathFor("/srv/mods"));
    }

    [Fact]
    public async Task Copies_only_what_the_catalog_reads_from_the_folders_the_game_loads()
    {
        var result = await Fetcher().FetchAsync(["111", "222", "333"]);

        Assert.Null(result.Error);
        Assert.Equal(["111", "222"], result.Folders.Keys.Order());
        Assert.Equal(
        [
            "mods/ModA/42.13/media/lua/shared/Translate/EN/ItemName.json",
            "mods/ModA/42.13/media/scripts/c.txt",
            "mods/ModA/42.13/media/textures/Icons/Item_Fork.png",
            "mods/ModA/42.13/media/textures/Item_Knife.png",
            "mods/ModA/42.13/media/textures/WorldItems/Item_Knife.png",
            "mods/ModA/42.13/mod.info",
            "mods/ModA/common/media/scripts/items/a.txt",
            "mods/ModA/mod.info",
        ], Cached("111"));
        Assert.Equal(["mods/OldMod/media/scripts/o.txt", "mods/OldMod/mod.info"], Cached("222"));

        // never listed: models, sounds, client lua, the older version folder, the B41 media of a B42 mod
        Assert.DoesNotContain(_counters.Listed, p => p.Contains("/models_X") || p.Contains("/sound") || p.Contains("/client")
            || p.Contains("/42/") || p.EndsWith("/42") || p.EndsWith("ModA/media"));
        Assert.Equal(4, result.Stats.Connections);
        Assert.True(result.Stats.ManifestFound);

        // what SpiffoCON's catalog makes of the copy
        var mod = Assert.Single(ModInfo.Scan(result.Folders["111"], "111"));
        Assert.Equal("Mod A", mod.Name);
        Assert.Equal(["common", "42.13"], mod.ContentRoots.Select(Path.GetFileName));
    }

    [Fact]
    public async Task Unchanged_items_are_not_visited_again()
    {
        await Fetcher().FetchAsync(["111", "222"]);
        _counters.Lists = _counters.Downloads = _counters.Connections = 0;

        var again = await Fetcher().FetchAsync(["111", "222"]);
        Assert.Equal(2, again.Stats.UpToDate);
        Assert.Equal(0, _counters.Lists);
        Assert.Equal(1, _counters.Downloads); // the manifest
        Assert.Equal(1, _counters.Connections);
        Assert.Equal(2, again.Folders.Count);
    }

    [Fact]
    public async Task An_updated_item_is_read_again_and_its_removed_files_leave_the_copy()
    {
        await Fetcher().FetchAsync(["111", "222"]);
        File.Delete(Path.Combine(Workshop, "111", "mods", "ModA", "42.13", "media", "scripts", "c.txt"));
        Write(Path.Combine(Workshop, "111", "mods", "ModA", "42.13", "media", "scripts", "d.txt"), "module A {}");
        WriteManifest(("111", 1500), ("222", 2000));
        _counters.Lists = _counters.Downloads = 0;
        _counters.Listed.Clear();

        var result = await Fetcher().FetchAsync(["111", "222"]);
        Assert.Equal(1, result.Stats.UpToDate);
        Assert.Equal(1, result.Stats.Read);
        Assert.Contains("mods/ModA/42.13/media/scripts/d.txt", Cached("111"));
        Assert.DoesNotContain("mods/ModA/42.13/media/scripts/c.txt", Cached("111"));
        // only the new file and the manifest were downloaded; the others matched the cache
        Assert.Equal(2, _counters.Downloads);
        Assert.DoesNotContain(_counters.Listed, p => p.Contains("/222"));
    }

    [Fact]
    public async Task Without_a_manifest_every_item_is_read_again()
    {
        // Steam's date says what is published, not what the server has installed
        File.Delete(WorkshopManifest.PathFor(Workshop)!);
        var first = await Fetcher().FetchAsync(["111", "222"]);
        Assert.False(first.Stats.ManifestFound);
        _counters.Listed.Clear();
        _counters.Downloads = 0;

        var second = await Fetcher().FetchAsync(["111", "222"]);
        Assert.Equal(0, second.Stats.UpToDate);
        Assert.Contains(_counters.Listed, p => p.Contains("/111"));
        // listed again, but only the (missing) manifest was asked for: the cached files match
        Assert.Equal(1, _counters.Downloads);
        Assert.Equal(2, second.Folders.Count);
    }

    [Fact]
    public async Task Keeps_the_version_folder_the_game_loads_even_when_nothing_is_copied_from_it()
    {
        // mod.info only at the root, content in common, the 42 folder holding only models
        var mod = Path.Combine(Workshop, "333", "mods", "ModC");
        Write(Path.Combine(mod, "mod.info"), "name=Mod C\nid=ModC");
        Write(Path.Combine(mod, "common", "media", "scripts", "c.txt"), "module C { item Spoon { } }");
        Write(Path.Combine(mod, "42", "media", "models_X", "spoon.fbx"), "mesh");

        var result = await Fetcher().FetchAsync(["333"]);
        var info = Assert.Single(ModInfo.Scan(result.Folders["333"], "333"));
        Assert.False(info.LegacyLayout);
        Assert.Equal(["common", "42"], info.ContentRoots.Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_second_copy_of_the_same_cache_waits_its_turn()
    {
        Directory.CreateDirectory(Cache);
        using (new FileStream(Path.Combine(Cache, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var busy = await Fetcher(lockWait: TimeSpan.FromMilliseconds(300)).FetchAsync(["111"]);
            Assert.True(busy.Stats.Busy);
            Assert.IsType<IOException>(busy.Error);
            Assert.Empty(busy.Folders);
        }
        Assert.Null((await Fetcher().FetchAsync(["111"])).Error);
    }

    [Fact]
    public async Task A_failure_stops_the_run_but_keeps_the_up_to_date_items()
    {
        await Fetcher().FetchAsync(["222"]);
        var result = await Fetcher(failOn: "/42.13").FetchAsync(["111", "222"]);

        Assert.IsType<IOException>(result.Error);
        Assert.Equal(["222"], result.Folders.Keys);
        // the failed item is not marked as copied, so the next run reads it again
        var next = await Fetcher().FetchAsync(["111", "222"]);
        Assert.Null(next.Error);
        Assert.Equal(1, next.Stats.Read);
    }

    [Fact]
    public async Task Server_names_cannot_escape_the_cache()
    {
        // a file name with backslashes is legal on a Linux server
        var evil = Path.Combine(Workshop, "111", "mods", "ModA", "42.13", "media", "scripts");
        if (!OperatingSystem.IsWindows())
            Write(Path.Combine(evil, "..\\..\\..\\..\\..\\evil.txt"), "x");
        var result = await Fetcher().FetchAsync(["111"]);
        Assert.Null(result.Error);
        Assert.False(File.Exists(Path.Combine(Cache, "evil.txt")));
        Assert.All(Directory.EnumerateFiles(Cache, "*", SearchOption.AllDirectories),
            f => Assert.StartsWith(Path.GetFullPath(Cache), Path.GetFullPath(f)));
    }
}
