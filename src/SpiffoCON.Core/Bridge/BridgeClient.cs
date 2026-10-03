using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Renci.SshNet.Common;
using SpiffoCON.Core.Files;

namespace SpiffoCON.Core.Bridge;

/// <summary>The server's Zomboid/Lua folder, where the bridge mod reads requests and writes replies.</summary>
public interface IBridgeFiles : IDisposable
{
    string Description { get; }

    /// <summary>The file's text, or null if it doesn't exist.</summary>
    Task<string?> ReadAsync(string name, CancellationToken ct = default);

    /// <summary>Replaces the file in one step, so the bridge never reads half a request.</summary>
    Task WriteAsync(string name, string text, CancellationToken ct = default);
}

public sealed class LocalBridgeFiles(string folder) : IBridgeFiles
{
    public string Description => "Folder: " + folder;

    public async Task<string?> ReadAsync(string name, CancellationToken ct = default)
    {
        var path = Path.Combine(folder, name);
        if (!File.Exists(path))
            return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(string name, string text, CancellationToken ct = default)
    {
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path + ".tmp", text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        // the game (a local server) may be reading the file this very moment: Windows then refuses
        // the replace for a few milliseconds
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(path + ".tmp", path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() { }
}

public sealed class SftpBridgeFiles(SftpSettings settings, string remoteFolder) : IBridgeFiles
{
    Renci.SshNet.SftpClient? _client;
    SftpSettings _settings = settings;
    bool _disposed;

    public string Description => $"SFTP {_settings.Host}: {remoteFolder}";

    string Remote(string name) => remoteFolder.TrimEnd('/') + "/" + name;

    async Task<Renci.SshNet.SftpClient> ClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
            return _client;
        // forget the old client before reconnecting: if this attempt fails, the next one must
        // not find a disposed client (IsConnected throws then) and never try again
        _client?.Dispose();
        _client = null;
        var (client, fingerprint) = await SftpProbe.ConnectAsync(_settings, ct).ConfigureAwait(false);
        if (_disposed)
        {
            // closed while connecting: this session must not stay open
            client.Dispose();
            throw new ObjectDisposedException(GetType().Name);
        }
        // reconnections insist on the key seen now
        _settings = _settings.Pinned(fingerprint);
        _client = client;
        return client;
    }

    public async Task<string?> ReadAsync(string name, CancellationToken ct = default)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        try
        {
            using var buffer = new MemoryStream();
            await client.DownloadFileAsync(Remote(name), buffer, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }

    public async Task WriteAsync(string name, string text, CancellationToken ct = default)
    {
        var client = await ClientAsync(ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(text);
        var temp = Remote(name) + ".tmp";
        using (var upload = new MemoryStream(bytes))
            await client.UploadFileAsync(upload, temp, ct).ConfigureAwait(false);
        try
        {
            client.RenameFile(temp, Remote(name), isPosix: true);
        }
        catch (Exception ex) when (ex is SshException or NotSupportedException)
        {
            // no posix-rename: a request file is small and rewritten every time, so an in-place
            // upload is acceptable here (the SEQ/END framing rejects a half-read one)
            using (var upload = new MemoryStream(bytes))
                await client.UploadFileAsync(upload, Remote(name), ct).ConfigureAwait(false);
            try { await client.DeleteFileAsync(temp, ct).ConfigureAwait(false); } catch (SshException) { }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _client?.Dispose();
    }
}

public sealed class BridgeException(string message) : Exception(message);

public sealed record BridgeReply(string Id, bool Ok, JsonElement Data, string? Error);

/// <summary>
/// Talks to the SpiffoCON Bridge mod (bridge/SpiffoCONBridge) through two files:
/// spiffocon_in.txt ("SEQ n" + one request per line: id TAB action TAB args) and
/// spiffocon_out.txt ("SEQ n", one JSON reply per line, "END n"). Sequence numbers are
/// millisecond timestamps, so they keep growing across app restarts.
/// </summary>
public sealed partial class BridgeClient(IBridgeFiles files)
{
    public const string InFile = "spiffocon_in.txt";
    public const string OutFile = "spiffocon_out.txt";

    readonly SemaphoreSlim _gate = new(1, 1);
    long _lastSeq;

    public IBridgeFiles Files { get; } = files;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(700);

    public async Task<IReadOnlyList<BridgeReply>> SendAsync(IReadOnlyList<(string Action, string[] Args)> requests, CancellationToken ct = default)
    {
        foreach (var (action, args) in requests)
            foreach (var part in args.Prepend(action))
                if (part.Contains('\t') || part.Contains('\n') || part.Contains('\r'))
                    throw new ArgumentException("Bridge request values can't contain tabs or line breaks.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long seq = Math.Max(_lastSeq + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _lastSeq = seq;
            var sb = new StringBuilder().Append("SEQ ").Append(seq).Append('\n');
            for (int i = 0; i < requests.Count; i++)
                sb.Append(i + 1).Append('\t').Append(string.Join('\t', requests[i].Args.Prepend(requests[i].Action))).Append('\n');
            // bridge v3 runs a batch only once it sees its END line: never half a request file
            sb.Append("END ").Append(seq).Append('\n');
            await Files.WriteAsync(InFile, sb.ToString(), ct).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + Timeout;
            // the last look is after the deadline, right before giving up: an action the server ran in the
            // last moments is not reported as failed
            for (bool last = false; !last;)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                last = DateTime.UtcNow >= deadline;
                var text = await Files.ReadAsync(OutFile, ct).ConfigureAwait(false);
                if (text is not null && TryParse(text, seq, out var replies))
                    return InRequestOrder(replies, requests.Count);
            }
            // an empty batch replaces the unanswered one: a paused server must not run it much later,
            // after the user was told it failed (and maybe tried again)
            try
            {
                long cancel = Math.Max(_lastSeq + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                _lastSeq = cancel;
                await Files.WriteAsync(InFile, $"SEQ {cancel}\nEND {cancel}\n", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
            throw new BridgeException(
                $"No reply from the bridge within {Timeout.TotalSeconds:0} s. Check that the SpiffoCON Bridge mod is in the server's mod list " +
                "(Mods= and WorkshopItems=). The bridge runs only while the server isn't paused: with PauseEmpty=true an empty server is paused.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JsonElement> SendAsync(string action, params string[] args)
    {
        var reply = (await SendAsync([(action, args)]).ConfigureAwait(false))[0];
        if (!reply.Ok)
            throw new BridgeException(reply.Error ?? "The bridge reported an error.");
        return reply.Data;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"""id""\s*:\s*""?([^"",}]+)")]
    private static partial System.Text.RegularExpressions.Regex ReplyId();

    /// <summary>
    /// One reply per request, matched by id: bridge v2 answers the END line of a v3 request file as
    /// an extra (failed) request, which must not count.
    /// </summary>
    static IReadOnlyList<BridgeReply> InRequestOrder(IReadOnlyList<BridgeReply> replies, int count)
    {
        var result = new List<BridgeReply>(count);
        for (int i = 1; i <= count; i++)
        {
            var id = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            result.Add(replies.FirstOrDefault(r => r.Id == id) ?? new BridgeReply(id, false, default, "The bridge sent no reply to this request."));
        }
        return result;
    }

    static bool TryParse(string text, long seq, out IReadOnlyList<BridgeReply> replies)
    {
        replies = [];
        // '\n' only: ReplaceLineEndings would also break a reply at a U+2028 inside a name
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        if (lines.Length < 2 || lines[0] != $"SEQ {seq}" || lines[^1] != $"END {seq}")
            return false;
        var list = new List<BridgeReply>();
        foreach (var line in lines[1..^1])
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                list.Add(new BridgeReply(
                    root.GetProperty("id").ToString(),
                    root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                    root.TryGetProperty("data", out var data) ? data.Clone() : default,
                    root.TryGetProperty("error", out var error) ? error.GetString() : null));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                // one unreadable reply (bridge v2 left control characters unescaped) fails only its request
                var id = ReplyId().Match(line) is { Success: true } m ? m.Groups[1].Value : "?";
                list.Add(new BridgeReply(id, false, default, "The bridge sent a reply SpiffoCON could not read: " + ex.Message));
            }
        }
        replies = list;
        return true;
    }

    // ---- typed calls ----

    public async Task<int> PingAsync() => (await SendAsync("ping").ConfigureAwait(false)).GetProperty("version").GetInt32();

    public async Task<IReadOnlyList<BridgePlayer>> PlayersAsync() =>
        Deserialize((await SendAsync("players").ConfigureAwait(false)), BridgeJson.Default.ListBridgePlayer);

    public async Task<IReadOnlyList<BridgeItem>> InventoryAsync(string username) =>
        Deserialize((await SendAsync("inventory", username).ConfigureAwait(false)), BridgeJson.Default.ListBridgeItem);

    public async Task<IReadOnlyList<BridgeVehicle>> VehiclesAsync() =>
        Deserialize((await SendAsync("vehicles").ConfigureAwait(false)), BridgeJson.Default.ListBridgeVehicle);

    public async Task<BridgeWorld> WorldAsync() =>
        (await SendAsync("world").ConfigureAwait(false)).Deserialize(BridgeJson.Default.BridgeWorld) ?? new BridgeWorld();

    // ---- write actions (bridge v2); the bridge also writes each one to the admin log ----

    /// <summary>Full heal of every body part, synced to the player's client. Returns the new health.</summary>
    public async Task<int?> HealAsync(string username)
    {
        var data = await SendAsync("heal", username).ConfigureAwait(false);
        return data.TryGetProperty("health", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : null;
    }

    /// <summary>
    /// Removes up to <paramref name="count"/> items of a type (0 = all), bags included. Worn clothes
    /// and attached items are left alone: they are counted in SkippedWorn.
    /// </summary>
    /// <param name="container">Only items listed under this container ("Inventory > Backpack"); bridge v3.</param>
    public async Task<(int Removed, int SkippedWorn)> RemoveItemAsync(string username, string fullType, int count, string? container = null)
    {
        var countText = Math.Max(0, count).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var data = await (string.IsNullOrEmpty(container)
            ? SendAsync("removeitem", username, fullType, countText)
            : SendAsync("removeitem", username, fullType, countText, container)).ConfigureAwait(false);
        return (data.GetProperty("removed").GetInt32(), data.TryGetProperty("skippedWorn", out var s) ? s.GetInt32() : 0);
    }

    // the script name makes bridge v3 refuse another vehicle that got the same runtime id meanwhile
    public Task RepairVehicleAsync(int id, string? script = null) => VehicleAsync("repairvehicle", id, script);

    public Task RefuelVehicleAsync(int id, string? script = null) => VehicleAsync("refuelvehicle", id, script);

    public Task RemoveVehicleAsync(int id, string? script = null) => VehicleAsync("removevehicle", id, script);

    // ---- corpses (bridge v6) ----

    /// <summary>The bridge clamps the radius to this.</summary>
    public const int MaxCorpseRadius = 100;

    /// <summary>
    /// Removes the zombie corpses (not players' or animals') within a radius of a square, on every floor.
    /// Returns how many, and how many of those squares the server had loaded (none: nobody is near).
    /// </summary>
    public async Task<(int Removed, int LoadedSquares)> RemoveCorpsesAsync(int x, int y, int radius, string? aroundPlayer = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // with a player, the bridge uses where they are now (x and y are from the last refresh)
        var data = await (aroundPlayer is null
            ? SendAsync("removecorpses", x.ToString(inv), y.ToString(inv), radius.ToString(inv))
            : SendAsync("removecorpses", x.ToString(inv), y.ToString(inv), radius.ToString(inv), aroundPlayer)).ConfigureAwait(false);
        return (data.GetProperty("removed").GetInt32(), data.TryGetProperty("loaded", out var loaded) ? loaded.GetInt32() : 0);
    }

    /// <summary>
    /// The items lying on the ground within a radius (every floor): counted, or with <paramref name="apply"/>
    /// removed. Those inside a safehouse stay, and are counted apart, unless <paramref name="safehouses"/>.
    /// </summary>
    public async Task<BridgeGroundItems> RemoveGroundItemsAsync(int x, int y, int radius, bool apply, bool safehouses, string? aroundPlayer = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var data = await SendAsync("removegrounditems", x.ToString(inv), y.ToString(inv), radius.ToString(inv),
            apply ? "1" : "0", safehouses ? "1" : "0", aroundPlayer ?? "").ConfigureAwait(false);
        return data.Deserialize(BridgeJson.Default.BridgeGroundItems) ?? new BridgeGroundItems();
    }

    /// <summary>Puts out the fires within a radius (every floor). Returns the burning squares put out, and the loaded squares.</summary>
    public async Task<(int Stopped, int LoadedSquares)> StopFiresAsync(int x, int y, int radius, string? aroundPlayer = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var data = await SendAsync("stopfires", x.ToString(inv), y.ToString(inv), radius.ToString(inv), aroundPlayer ?? "").ConfigureAwait(false);
        return (data.GetProperty("stopped").GetInt32(), data.TryGetProperty("loaded", out var loaded) ? loaded.GetInt32() : 0);
    }

    // ---- items on the ground and fires above, safehouses, player sheet, time, keys: bridge v7 ----

    public async Task<IReadOnlyList<BridgeSafehouse>> SafehousesAsync() =>
        Deserialize(await SendAsync("safehouses").ConfigureAwait(false), BridgeJson.Default.ListBridgeSafehouse);

    /// <summary>
    /// Removes a safehouse (what is inside stays). The owner is checked by the bridge: ids are given again.
    /// False when the server could only take it off its own list, and clients learn at their next login.
    /// </summary>
    public async Task<bool> RemoveSafehouseAsync(string id, string? owner)
    {
        var data = await SendAsync("removesafehouse", id, owner ?? "").ConfigureAwait(false);
        return !data.TryGetProperty("synced", out var synced) || synced.GetBoolean();
    }

    /// <summary>Traits, skills and condition of an online player.</summary>
    public async Task<BridgePlayerDetails> PlayerDetailsAsync(string username) =>
        (await SendAsync("playerdetails", username).ConfigureAwait(false)).Deserialize(BridgeJson.Default.BridgePlayerDetails) ?? new BridgePlayerDetails();

    /// <summary>
    /// Skips the clock forward to the next time it is that hour (0 up to 24, decimals for minutes): the game
    /// has no way back. Returns the world after it, with <see cref="BridgeWorld.SkippedHours"/>.
    /// </summary>
    public async Task<BridgeWorld> SetTimeAsync(double hour) =>
        (await SendAsync("settime", hour.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false))
            .Deserialize(BridgeJson.Default.BridgeWorld) ?? new BridgeWorld();

    /// <summary>Puts a key of the vehicle in the player's inventory. Returns the key's name.</summary>
    public async Task<string?> GiveVehicleKeyAsync(int id, string? script, string username)
    {
        var data = await SendAsync("vehiclekey", id.ToString(System.Globalization.CultureInfo.InvariantCulture), script ?? "", username).ConfigureAwait(false);
        return data.TryGetProperty("name", out var name) ? name.GetString() : null;
    }

    // ---- items put on the ground (bridge v8) ----

    /// <summary>The bridge puts at most this many objects down in one request.</summary>
    public const int MaxSpawn = 500;

    /// <summary>
    /// Puts items on the floor of a square (it must be loaded: near a player). Types the server does not
    /// know are skipped and named in the reply.
    /// </summary>
    public async Task<BridgeSpawn> SpawnItemsAsync(int x, int y, int z, IReadOnlyList<(string FullType, int Count)> items)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var (fullType, count) in items)
            if (fullType.Length == 0 || fullType.Any(c => c is ',' or '=' || char.IsWhiteSpace(c)) || count < 1)
                throw new ArgumentException($"Not an item to put down: \"{fullType}\" x {count}.");
        var list = string.Join(",", items.Select(i => i.FullType + "=" + i.Count.ToString(inv)));
        var data = await SendAsync("spawnitems", x.ToString(inv), y.ToString(inv), z.ToString(inv), list).ConfigureAwait(false);
        return data.Deserialize(BridgeJson.Default.BridgeSpawn) ?? new BridgeSpawn();
    }

    /// <summary>The burnt and smashed vehicles within a radius: counted, or with <paramref name="apply"/> removed.</summary>
    public async Task<BridgeWrecks> RemoveWrecksAsync(int x, int y, int radius, bool apply, string? aroundPlayer = null)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var data = await SendAsync("removewrecks", x.ToString(inv), y.ToString(inv), radius.ToString(inv), apply ? "1" : "0", aroundPlayer ?? "").ConfigureAwait(false);
        return data.Deserialize(BridgeJson.Default.BridgeWrecks) ?? new BridgeWrecks();
    }

    // ---- weather (bridge v5) ----

    /// <summary>The weather settings the bridge can set.</summary>
    public enum ClimateSetting { Fog, Clouds, Wind, Temperature, Snow }

    /// <summary>
    /// Overrides weather settings (fog, clouds, snow 0-1; wind km/h; temperature °C), or gives one back to
    /// the game with a null value, all in one round trip. Players see it at the next ten-minute climate
    /// tick. Returns the world after them.
    /// </summary>
    public async Task<BridgeWorld> SetClimateAsync(IReadOnlyList<(ClimateSetting Setting, double? Value)> settings, CancellationToken ct = default)
    {
        var replies = await SendAsync(settings.Select(s => ("climate", new[]
        {
            s.Setting.ToString().ToLowerInvariant(),
            s.Value is { } v ? v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : "off",
        })).ToList(), ct).ConfigureAwait(false);
        if (replies.FirstOrDefault(r => !r.Ok) is { } failed)
            throw new BridgeException(failed.Error ?? "The bridge reported an error.");
        return replies.Count == 0 ? new BridgeWorld() : replies[^1].Data.Deserialize(BridgeJson.Default.BridgeWorld) ?? new BridgeWorld();
    }

    /// <summary>Every weather setting back to the game.</summary>
    public async Task<BridgeWorld> ResetClimateAsync() =>
        (await SendAsync("climate", "reset").ConfigureAwait(false)).Deserialize(BridgeJson.Default.BridgeWorld) ?? new BridgeWorld();

    Task VehicleAsync(string action, int id, string? script)
    {
        var idText = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrEmpty(script) ? SendAsync(action, idText) : SendAsync(action, idText, script);
    }

    /// <summary>World, players and vehicles in one round trip.</summary>
    /// <param name="safehouses">Also the safehouses (bridge v7: an older bridge would answer "unknown action").</param>
    /// <param name="deathsAndZombies">Also the deaths and the zombies per area (bridge v8).</param>
    public async Task<BridgeSnapshot> SnapshotAsync(CancellationToken ct = default, bool safehouses = false, bool deathsAndZombies = false)
    {
        var requests = new List<(string, string[])> { ("world", []), ("players", []), ("vehicles", []) };
        // always in these places: the replies are read by position
        if (safehouses || deathsAndZombies)
            requests.Add(safehouses ? ("safehouses", []) : ("ping", []));
        if (deathsAndZombies)
        {
            requests.Add(("deaths", []));
            requests.Add(("zombiecells", []));
        }
        var replies = await SendAsync(requests, ct).ConfigureAwait(false);
        // one part failing (bridge v3 on B42 can't list vehicles) must not hide the others
        if (replies.All(r => !r.Ok))
            throw new BridgeException(replies[0].Error ?? "The bridge reported an error.");
        var problems = new[] { "world", "players", "vehicles", safehouses ? "safehouses" : "ping", "deaths", "zombies" }.Zip(replies)
            .Where(p => !p.Second.Ok).Select(p => $"{p.First}: {p.Second.Error}").ToList();
        return new BridgeSnapshot(
            replies[0].Ok ? replies[0].Data.Deserialize(BridgeJson.Default.BridgeWorld) ?? new BridgeWorld() : new BridgeWorld(),
            replies[1].Ok ? Deserialize(replies[1].Data, BridgeJson.Default.ListBridgePlayer) : [],
            replies[2].Ok ? Deserialize(replies[2].Data, BridgeJson.Default.ListBridgeVehicle) : [])
        {
            Safehouses = safehouses && replies.Count > 3 && replies[3].Ok ? Deserialize(replies[3].Data, BridgeJson.Default.ListBridgeSafehouse) : null,
            Deaths = replies.Count > 4 && replies[4].Ok ? Deserialize(replies[4].Data, BridgeJson.Default.ListBridgeDeath) : null,
            ZombieCells = replies.Count > 5 && replies[5].Ok ? Deserialize(replies[5].Data, BridgeJson.Default.ListBridgeZombieCell) : null,
            Problem = problems.Count == 0 ? null : string.Join("; ", problems),
        };
    }

    static List<T> Deserialize<T>(JsonElement data, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> info) =>
        data.ValueKind == JsonValueKind.Array ? data.Deserialize(info) ?? [] : [];
}

public sealed record BridgeSnapshot(BridgeWorld World, IReadOnlyList<BridgePlayer> Players, IReadOnlyList<BridgeVehicle> Vehicles)
{
    /// <summary>The parts the bridge could not give (the rest of the snapshot is still valid).</summary>
    public string? Problem { get; init; }

    /// <summary>Null when they were not asked for (or could not be read): what was known before stays.</summary>
    public IReadOnlyList<BridgeSafehouse>? Safehouses { get; init; }

    /// <summary>The last deaths of players (bridge v8), oldest first; null when not asked for.</summary>
    public IReadOnlyList<BridgeDeath>? Deaths { get; init; }

    /// <summary>The zombies of the loaded areas per 10 x 10 squares (bridge v8); null when not asked for.</summary>
    public IReadOnlyList<BridgeZombieCell>? ZombieCells { get; init; }
}

public sealed record BridgePlayer
{
    public string Username { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Role { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Z { get; init; }
    public bool? Dead { get; init; }
    public int? Health { get; init; }
    public bool? God { get; init; }
    public bool? Invisible { get; init; }
    public bool? Noclip { get; init; }
    public double? HoursSurvived { get; init; }
    public int? ZombieKills { get; init; }
    public string? Profession { get; init; }
    public string? SteamId { get; init; }
    public string? Vehicle { get; init; }

    /// <summary>Bridge v7: the zombies within about 30 squares (absent with an older bridge).</summary>
    public int? ZombiesNear { get; init; }
}

/// <summary>Bridge v7: what playerdetails adds to a player.</summary>
public sealed record BridgePlayerDetails
{
    public string Username { get; init; } = "";
    public string? Profession { get; init; }
    public List<string> Traits { get; init; } = [];
    public List<BridgeSkill> Skills { get; init; } = [];

    /// <summary>hunger, thirst, fatigue, endurance, stress, panic...: each 0 to 1 of its own range.</summary>
    public Dictionary<string, double?> Stats { get; init; } = [];
    public bool? Infected { get; init; }

    /// <summary>Body parts bitten.</summary>
    public int? Bitten { get; init; }
    public bool? OnFire { get; init; }
    public int? Health { get; init; }
    public double? HoursSurvived { get; init; }
    public int? ZombieKills { get; init; }
}

public sealed record BridgeSkill
{
    public string Name { get; init; } = "";
    public string? Category { get; init; }
    public int? Level { get; init; }
}

public sealed record BridgeSafehouse
{
    /// <summary>The id the server knows it by now (given again to others as safehouses come and go).</summary>
    public string Id { get; init; } = "";
    public string? Title { get; init; }
    public string? Owner { get; init; }
    public List<string> Members { get; init; } = [];
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? W { get; init; }
    public int? H { get; init; }

    /// <summary>Milliseconds since 1970 (UTC).</summary>
    public long? LastVisited { get; init; }

    /// <summary>Members online now.</summary>
    public int? Online { get; init; }
}

public sealed record BridgeDeath
{
    /// <summary>Milliseconds since 1970 (UTC), real time.</summary>
    public long? Time { get; init; }
    public string Username { get; init; } = "";
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Z { get; init; }

    /// <summary>The player who killed them, "zombie", or absent when the game does not say.</summary>
    public string? Killer { get; init; }
    public double? HoursSurvived { get; init; }
    public int? ZombieKills { get; init; }

    public DateTimeOffset? When => Time is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
}

public sealed record BridgeZombieCell
{
    /// <summary>The corner of the area (squares).</summary>
    public int X { get; init; }
    public int Y { get; init; }

    /// <summary>Zombies in it.</summary>
    public int N { get; init; }
}

public sealed record BridgeWrecks
{
    public int Found { get; init; }
    public int Removed { get; init; }
}

public sealed record BridgeSpawn
{
    public int Spawned { get; init; }

    /// <summary>Types the server does not know (a mod it does not have), skipped.</summary>
    public List<string> Unknown { get; init; } = [];
}

public sealed record BridgeGroundItems
{
    /// <summary>Items that would be (or were) removed.</summary>
    public int Found { get; init; }
    public int Removed { get; init; }

    /// <summary>Items left because they are inside a safehouse.</summary>
    public int InSafehouses { get; init; }

    /// <summary>Items left because they are not on the floor: put on a table, a shelf, a counter.</summary>
    public int OnFurniture { get; init; }

    /// <summary>Ground squares of the area the server had loaded (none: nobody is near).</summary>
    public int Loaded { get; init; }
}

public sealed record BridgeItem
{
    public string Container { get; init; } = "";

    /// <summary>The container without the "Inventory > " every bag's path starts with: "School Bag #2".</summary>
    [JsonIgnore]
    public string ContainerShort => Container.StartsWith("Inventory > ", StringComparison.Ordinal) ? Container["Inventory > ".Length..] : Container;

    public string FullType { get; init; } = "";
    public string Name { get; init; } = "";
    public int Count { get; init; }
    public bool Equipped { get; init; }
}

public sealed record BridgeVehicle
{
    public int? Id { get; init; }
    public string? Script { get; init; }
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Z { get; init; }
    public string? Driver { get; init; }
    public bool? EngineRunning { get; init; }
}

public sealed record BridgeWorld
{
    public int? Year { get; init; }
    public int? Month { get; init; }
    public int? Day { get; init; }
    public int? Hour { get; init; }
    public int? Minute { get; init; }
    public int? WorldAgeHours { get; init; }
    public double? Temperature { get; init; }
    public double? Rain { get; init; }
    public double? Fog { get; init; }
    public double? Clouds { get; init; }

    /// <summary>km/h.</summary>
    public double? Wind { get; init; }

    /// <summary>Whether precipitation falls as snow.</summary>
    public bool? Snow { get; init; }

    // bridge v5: the weather SpiffoCON set (absent: the game decides)
    public double? AdminFog { get; init; }
    public double? AdminClouds { get; init; }

    /// <summary>km/h.</summary>
    public double? AdminWind { get; init; }

    /// <summary>°C.</summary>
    public double? AdminTemperature { get; init; }

    /// <summary>Snowfall 0-1.</summary>
    public double? AdminSnow { get; init; }

    /// <summary>Only in the reply to a time change: the hours the clock skipped forward.</summary>
    public double? SkippedHours { get; init; }
    public int? ZombiesLoaded { get; init; }
    public int? Players { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(List<BridgePlayer>))]
[JsonSerializable(typeof(List<BridgeItem>))]
[JsonSerializable(typeof(List<BridgeVehicle>))]
[JsonSerializable(typeof(BridgeWorld))]
[JsonSerializable(typeof(List<BridgeSafehouse>))]
[JsonSerializable(typeof(BridgePlayerDetails))]
[JsonSerializable(typeof(BridgeGroundItems))]
[JsonSerializable(typeof(BridgeSpawn))]
[JsonSerializable(typeof(List<BridgeDeath>))]
[JsonSerializable(typeof(List<BridgeZombieCell>))]
[JsonSerializable(typeof(BridgeWrecks))]
internal sealed partial class BridgeJson : JsonSerializerContext;
