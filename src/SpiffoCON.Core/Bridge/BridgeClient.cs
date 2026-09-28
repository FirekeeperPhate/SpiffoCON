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
        File.Move(path + ".tmp", path, overwrite: true);
    }

    public void Dispose() { }
}

public sealed class SftpBridgeFiles(SftpSettings settings, string remoteFolder) : IBridgeFiles
{
    Renci.SshNet.SftpClient? _client;

    public string Description => $"SFTP {settings.Host}: {remoteFolder}";

    string Remote(string name) => remoteFolder.TrimEnd('/') + "/" + name;

    async Task<Renci.SshNet.SftpClient> ClientAsync(CancellationToken ct)
    {
        if (_client is { IsConnected: true })
            return _client;
        _client?.Dispose();
        (_client, _) = await SftpProbe.ConnectAsync(settings, ct).ConfigureAwait(false);
        return _client;
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
            using (var upload = new MemoryStream(bytes))
                await client.UploadFileAsync(upload, Remote(name), ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _client?.Dispose();
}

public sealed class BridgeException(string message) : Exception(message);

public sealed record BridgeReply(string Id, bool Ok, JsonElement Data, string? Error);

/// <summary>
/// Talks to the SpiffoCON Bridge mod (bridge/SpiffoCONBridge) through two files:
/// spiffocon_in.txt ("SEQ n" + one request per line: id TAB action TAB args) and
/// spiffocon_out.txt ("SEQ n", one JSON reply per line, "END n"). Sequence numbers are
/// millisecond timestamps, so they keep growing across app restarts.
/// </summary>
public sealed class BridgeClient(IBridgeFiles files)
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
            await Files.WriteAsync(InFile, sb.ToString(), ct).ConfigureAwait(false);

            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                var text = await Files.ReadAsync(OutFile, ct).ConfigureAwait(false);
                if (text is not null && TryParse(text, seq, out var replies))
                    return replies;
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

    static bool TryParse(string text, long seq, out IReadOnlyList<BridgeReply> replies)
    {
        replies = [];
        var lines = text.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || lines[0] != $"SEQ {seq}" || lines[^1] != $"END {seq}")
            return false;
        var list = new List<BridgeReply>();
        foreach (var line in lines[1..^1])
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            list.Add(new BridgeReply(
                root.GetProperty("id").ToString(),
                root.TryGetProperty("ok", out var ok) && ok.GetBoolean(),
                root.TryGetProperty("data", out var data) ? data.Clone() : default,
                root.TryGetProperty("error", out var error) ? error.GetString() : null));
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

    /// <summary>World, players and vehicles in one round trip.</summary>
    public async Task<BridgeSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var replies = await SendAsync([("world", []), ("players", []), ("vehicles", [])], ct).ConfigureAwait(false);
        foreach (var r in replies.Where(r => !r.Ok))
            throw new BridgeException(r.Error ?? "The bridge reported an error.");
        return new BridgeSnapshot(
            replies[0].Data.Deserialize(BridgeJson.Default.BridgeWorld) ?? new BridgeWorld(),
            Deserialize(replies[1].Data, BridgeJson.Default.ListBridgePlayer),
            Deserialize(replies[2].Data, BridgeJson.Default.ListBridgeVehicle));
    }

    static List<T> Deserialize<T>(JsonElement data, System.Text.Json.Serialization.Metadata.JsonTypeInfo<List<T>> info) =>
        data.ValueKind == JsonValueKind.Array ? data.Deserialize(info) ?? [] : [];
}

public sealed record BridgeSnapshot(BridgeWorld World, IReadOnlyList<BridgePlayer> Players, IReadOnlyList<BridgeVehicle> Vehicles);

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
}

public sealed record BridgeItem
{
    public string Container { get; init; } = "";
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
    public int? ZombiesLoaded { get; init; }
    public int? Players { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(List<BridgePlayer>))]
[JsonSerializable(typeof(List<BridgeItem>))]
[JsonSerializable(typeof(List<BridgeVehicle>))]
[JsonSerializable(typeof(BridgeWorld))]
internal sealed partial class BridgeJson : JsonSerializerContext;
