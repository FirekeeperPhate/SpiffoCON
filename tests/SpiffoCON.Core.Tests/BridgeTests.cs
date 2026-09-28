using SpiffoCON.Core.Bridge;

namespace SpiffoCON.Core.Tests;

public sealed class BridgeTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("spiffocon-bridge").FullName;
    readonly CancellationTokenSource _stop = new();

    public void Dispose()
    {
        _stop.Cancel();
        Thread.Sleep(100);
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Answers like bridge/SpiffoCONBridge/.../SpiffoCONBridge.lua does.</summary>
    void RunFakeBridge(Func<string, string[], string> reply)
    {
        _ = Task.Run(async () =>
        {
            long last = 0;
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(50);
                var path = Path.Combine(_dir, BridgeClient.InFile);
                if (!File.Exists(path))
                    continue;
                string[] lines;
                try { lines = File.ReadAllLines(path); } catch (IOException) { continue; }
                // like bridge v3: a batch counts once its END line is there, and END is not a request
                if (lines.Length == 0 || !long.TryParse(lines[0]["SEQ ".Length..], out var seq) || seq == last || !lines.Contains($"END {seq}"))
                    continue;
                last = seq;
                var output = new List<string> { $"SEQ {seq}" };
                foreach (var line in lines.Skip(1).Where(l => l.Length > 0 && l != $"END {seq}"))
                {
                    var parts = line.Split('\t');
                    output.Add(reply(parts[1], parts[2..]).Replace("$ID", parts[0]));
                }
                output.Add($"END {seq}");
                File.WriteAllText(Path.Combine(_dir, BridgeClient.OutFile), string.Join("\n", output) + "\n");
            }
        });
    }

    [Fact]
    public async Task Typed_calls_parse_the_bridge_replies()
    {
        RunFakeBridge((action, args) => action switch
        {
            "ping" => """{"id":"$ID","ok":true,"data":{"version":1,"players":1}}""",
            "players" => """{"id":"$ID","ok":true,"data":[{"username":"rj","role":"admin","x":10600,"y":9400,"z":0,"health":87,"god":false,"vehicle":"Base.CarNormal","hoursSurvived":12.5}]}""",
            "inventory" => """{"id":"$ID","ok":true,"data":[{"container":"Inventory","fullType":"Base.Axe","name":"Axe","count":1,"equipped":true},{"container":"Inventory > Big Hiking Bag","fullType":"Base.Nails","name":"Nails","count":42,"equipped":false}]}""",
            "vehicles" => """{"id":"$ID","ok":true,"data":[]}""",
            "heal" => """{"id":"$ID","ok":true,"data":{"health":100}}""",
            "removeitem" when args is ["rj", "Base.Nails", "5"] => """{"id":"$ID","ok":true,"data":{"removed":5,"skippedWorn":0}}""",
            "removevehicle" => """{"id":"$ID","ok":false,"error":"no vehicle with id 7 (it may be in an area no player has loaded)"}""",
            _ => """{"id":"$ID","ok":false,"error":"unknown action x"}""",
        });
        var client = new BridgeClient(new LocalBridgeFiles(_dir)) { PollInterval = TimeSpan.FromMilliseconds(50) };

        Assert.Equal(1, await client.PingAsync());
        var player = Assert.Single(await client.PlayersAsync());
        Assert.Equal(("rj", "admin", 10600, 87, "Base.CarNormal", 12.5), (player.Username, player.Role, player.X, player.Health, player.Vehicle, player.HoursSurvived));
        var items = await client.InventoryAsync("rj");
        Assert.Equal(42, items[1].Count);
        Assert.True(items[0].Equipped);
        Assert.Empty(await client.VehiclesAsync());
        Assert.Equal(100, await client.HealAsync("rj"));
        Assert.Equal((5, 0), await client.RemoveItemAsync("rj", "Base.Nails", 5));
        var gone = await Assert.ThrowsAsync<BridgeException>(() => client.RemoveVehicleAsync(7));
        Assert.Contains("no vehicle with id 7", gone.Message);
        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.SendAsync("nope"));
        Assert.Contains("unknown action", ex.Message);
    }

    [Fact]
    public async Task No_bridge_times_out_with_advice()
    {
        var client = new BridgeClient(new LocalBridgeFiles(_dir)) { Timeout = TimeSpan.FromMilliseconds(400), PollInterval = TimeSpan.FromMilliseconds(50) };
        var ex = await Assert.ThrowsAsync<BridgeException>(() => client.PingAsync());
        Assert.Contains("PauseEmpty", ex.Message);
        Assert.StartsWith("SEQ ", File.ReadAllText(Path.Combine(_dir, BridgeClient.InFile)));
    }

    [Fact]
    public void Export_keeps_the_workshop_id_of_an_earlier_upload()
    {
        var source = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(source, "Contents", "mods", "SpiffoCONBridge", "42"));
        File.WriteAllText(Path.Combine(source, "workshop.txt"), "version=1\ntitle=SpiffoCON Bridge\n");
        File.WriteAllText(Path.Combine(source, "Contents", "mods", "SpiffoCONBridge", "42", "mod.info"), "id=SpiffoCONBridge\n");
        var workshop = Path.Combine(_dir, "Workshop");

        var target = BridgeMod.ExportForUpload(source, workshop);
        Assert.True(File.Exists(Path.Combine(target, "Contents", "mods", "SpiffoCONBridge", "42", "mod.info")));
        Assert.Null(BridgeMod.ReadWorkshopId(workshop));

        // the game adds the id after the first upload
        File.AppendAllText(Path.Combine(target, "workshop.txt"), "id=3790000001\n");
        BridgeMod.ExportForUpload(source, workshop);
        Assert.Equal("3790000001", BridgeMod.ReadWorkshopId(workshop));
    }

    [Fact]
    public async Task Requests_cannot_break_the_line_format()
    {
        var client = new BridgeClient(new LocalBridgeFiles(_dir));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SendAsync("inventory", "a\tb"));
    }
}
