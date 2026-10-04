using MoonSharp.Interpreter;
using SpiffoCON.Core.Bridge;

namespace SpiffoCON.Core.Tests;

/// <summary>
/// The bridge mod's own Lua script (bridge/SpiffoCONBridge/.../SpiffoCONBridge.lua), run in a Lua
/// interpreter against a mock of the game (Bridge/GameMock.lua) and driven by the real
/// <see cref="BridgeClient"/> through the real file protocol: requests, JSON replies, C# types.
/// The mock's world: rj at 100,102 with two school bags, kate at 500,500; corpses, items on the ground
/// and fires around 100,100; a safehouse of rj at 102,98 (5x5) and one of kate; a pick-up truck, id 7.
/// </summary>
public sealed class BridgeScriptTests
{
    readonly Script _game = new();
    readonly BridgeClient _client;

    public BridgeScriptTests()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Bridge");
        _game.DoString(File.ReadAllText(Path.Combine(folder, "GameMock.lua")));
        _game.DoString(File.ReadAllText(Path.Combine(folder, "SpiffoCONBridge.lua")));
        var files = new GameFiles(_game);
        files.Tick(); // the first poll only notes what was there before the start
        _client = new BridgeClient(files) { PollInterval = TimeSpan.FromMilliseconds(2), Timeout = TimeSpan.FromSeconds(2) };
    }

    /// <summary>What the game was told to do since the last call (the admin log apart).</summary>
    List<string> Told()
    {
        var lines = _game.Globals.Get("LOG").Table.Values.Select(v => v.String).Where(l => !l.StartsWith("log ")).ToList();
        _game.DoString("LOG = {}");
        return lines;
    }

    double Lua(string expression) => _game.DoString("return " + expression).Number;

    [Fact]
    public async Task The_snapshot_has_the_zombies_near_each_player_and_the_safehouses()
    {
        Assert.Equal(10, await _client.PingAsync());
        var snapshot = await _client.SnapshotAsync(safehouses: true);
        Assert.Null(snapshot.Problem);
        var rj = Assert.Single(snapshot.Players, p => p.Username == "rj");
        Assert.Equal((100, 102, 0, 87, "Carpenter", 5), (rj.X, rj.Y, rj.Z, rj.Health, rj.Profession, rj.ZombiesNear));
        Assert.Equal(1, snapshot.Players.Single(p => p.Username == "kate").ZombiesNear);

        Assert.Equal(2, snapshot.Safehouses!.Count);
        var house = snapshot.Safehouses[1];
        // a title with quotes comes through the script's own JSON encoder
        Assert.Equal(("2", "Kate \"the\" base", "kate", 480, 480, 10, 12), (house.Id, house.Title, house.Owner, house.X, house.Y, house.W, house.H));
        // the game lists the owner among the players of a safehouse: not a member of their own
        Assert.Equal(["kate"], snapshot.Safehouses[0].Members);
        Assert.Empty(house.Members);
        Assert.Equal(1790947308993, house.LastVisited);

        // an older client does not ask for them
        Assert.Null((await _client.SnapshotAsync()).Safehouses);
    }

    [Fact]
    public async Task Two_bags_of_one_type_are_told_apart()
    {
        var nails = (await _client.InventoryAsync("rj")).Where(i => i.FullType == "Base.Nails").ToList();
        Assert.Equal(
            [("Inventory > School Bag > Toolbox", 1), ("Inventory > School Bag", 3), ("Inventory > School Bag #2", 5)],
            nails.Select(i => (i.Container, i.Count)));
        Assert.Equal("School Bag #2", nails[2].ContainerShort);

        Assert.Equal((5, 0), await _client.RemoveItemAsync("rj", "Base.Nails", 0, "Inventory > School Bag #2"));
        Assert.Equal((1, 0), await _client.RemoveItemAsync("rj", "Base.Nails", 1, "Inventory > School Bag"));
        nails = (await _client.InventoryAsync("rj")).Where(i => i.FullType == "Base.Nails").ToList();
        Assert.Equal([("Inventory > School Bag > Toolbox", 1), ("Inventory > School Bag", 2)], nails.Select(i => (i.Container, i.Count)));
        Assert.Equal(6, Told().Count(l => l == "sendRemove Nails from School Bag"));

        // two bags of different types under one name are apart too
        var screws = (await _client.InventoryAsync("rj")).Where(i => i.FullType == "Base.Screws").ToList();
        Assert.Equal([("Inventory > Duffel Bag", 1), ("Inventory > Duffel Bag #2", 2)], screws.Select(i => (i.Container, i.Count)));
        Assert.Equal((2, 0), await _client.RemoveItemAsync("rj", "Base.Screws", 0, "Inventory > Duffel Bag #2"));
        Assert.Equal(("Inventory > Duffel Bag", 1), (await _client.InventoryAsync("rj")).Where(i => i.FullType == "Base.Screws").Select(i => (i.Container, i.Count)).Single());
    }

    [Fact]
    public async Task A_player_sheet_has_traits_skills_and_condition()
    {
        var sheet = await _client.PlayerDetailsAsync("kate");
        Assert.Equal(("kate", "Carpenter", true, 1, false), (sheet.Username, sheet.Profession, sheet.Infected, sheet.Bitten, sheet.OnFire));
        // a trait the game has no definition for keeps its id
        Assert.Equal(["Brave", "Fast Reader", "base:mystery"], sheet.Traits);
        // the categories themselves are not skills
        Assert.Equal([("Axe", "Combat", 4), ("Carpentry", "Crafting", 2)], sheet.Skills.Select(s => (s.Name, s.Category, s.Level)));
        // each stat as a fraction of its own range: panic is kept 0-100 by the game
        Assert.Equal((0.26, 0.5, 1.0), (sheet.Stats["hunger"], sheet.Stats["panic"], sheet.Stats["endurance"]));

        var ex = await Assert.ThrowsAsync<BridgeException>(() => _client.PlayerDetailsAsync("ghost"));
        Assert.Contains("ghost is not online", ex.Message);
    }

    [Fact]
    public async Task A_safehouse_is_removed_the_way_that_tells_the_clients()
    {
        // ids are given again as safehouses come and go: the owner must still be the one the list showed
        var ex = await Assert.ThrowsAsync<BridgeException>(() => _client.RemoveSafehouseAsync("2", "rj"));
        Assert.Contains("now belongs to kate", ex.Message);

        Assert.True(await _client.RemoveSafehouseAsync("2", "kate"));
        Assert.Equal(["SafehouseRelease to all 2"], Told());

        // a server with safehouse wars off (the option is 0) removes it the same way
        _game.DoString("WAR_HIT_POINTS = 0; safehouse(3, 'Third', 'ann', {}, 300, 300, 4, 4)");
        Assert.True(await _client.RemoveSafehouseAsync("3", "ann"));
        Assert.Equal(["SafehouseRelease to all 3"], Told());

        // the server option can't be read: off the server's list at least, and said so
        _game.DoString("getServerOptions = function() error('no options') end");
        Assert.False(await _client.RemoveSafehouseAsync("1", "rj"));
        Assert.Equal(["removeSafeHouse (not synced) 1"], Told());
        Assert.Empty(await _client.SafehousesAsync());
        await Assert.ThrowsAsync<BridgeException>(() => _client.RemoveSafehouseAsync("1", "rj"));
    }

    [Fact]
    public async Task Items_on_the_ground_are_counted_then_removed_and_safehouses_are_left()
    {
        var count = await _client.RemoveGroundItemsAsync(100, 100, 3, apply: false, safehouses: false);
        // the vase a player put on a table is not on the ground
        Assert.Equal((2, 0, 2, 1, 29), (count.Found, count.Removed, count.InSafehouses, count.OnFurniture, count.Loaded));
        Assert.Empty(Told());

        var done = await _client.RemoveGroundItemsAsync(100, 100, 3, apply: true, safehouses: false);
        Assert.Equal((2, 2, 1), (done.Removed, done.InSafehouses, done.OnFurniture));
        // every floor of the circle, and nothing inside the safehouse or beyond the radius
        Assert.Equal(["removeGround can", "removeGround plank upstairs"], Told());

        // around a player: where the bridge has them now, not the x and y that were sent
        Assert.Equal(1, (await _client.RemoveGroundItemsAsync(0, 0, 4, apply: false, safehouses: false, aroundPlayer: "rj")).Found);
        // nobody near: nothing is loaded there
        Assert.Equal(0, (await _client.RemoveGroundItemsAsync(9000, 9000, 4, apply: true, safehouses: false)).Loaded);
    }

    [Fact]
    public async Task Fires_are_put_out_and_only_zombie_corpses_removed()
    {
        Assert.Equal(2, (await _client.StopFiresAsync(100, 100, 3)).Stopped);
        Assert.Equal(["stopFire 100,100,0", "stopFire 101,101,1"], Told());
        // the lit campfire a square away is a fire to the game, but not one to put out: not counted, not touched
        Assert.Equal(0, (await _client.StopFiresAsync(100, 100, 3)).Stopped);
        Assert.Empty(Told());
        Assert.Equal(1, Lua("#getCell():getGridSquare(99, 100, 0).fires"));

        // the dead player on the same square stays, and so does the zombie four squares away
        Assert.Equal(2, (await _client.RemoveCorpsesAsync(100, 100, 3)).Removed);
        Assert.Equal(["removeCorpse z1", "removeCorpse z-basement"], Told());
        // a radius is clamped, not refused
        Assert.Equal(1, (await _client.RemoveCorpsesAsync(100, 100, 100000)).Removed);
    }

    /// <summary>
    /// The game counts the age of the world in nights that begin at 7:00, plus the time of day, and turns
    /// the calendar at 24:00: whatever the two hours, the world must get older by exactly the hours
    /// skipped, and the game's own next ticks must not count a night or a day again.
    /// </summary>
    [Theory]
    [InlineData(20.5, 7.5, 11)]
    [InlineData(22.1, 6.25, 8.15)]
    [InlineData(12, 8, 20)]
    [InlineData(5, 22, 17)]
    [InlineData(5, 6, 1)]
    [InlineData(8, 7, 23)]
    [InlineData(6, 7, 1)]
    [InlineData(0.5, 23.5, 23)]
    [InlineData(23.5, 0.5, 1)]
    [InlineData(10, 18, 8)]
    [InlineData(15, 10, 19)]   // into the next day and past 7:00: the night is counted once the day has turned
    [InlineData(5, 3, 22)]     // into the next day, from before 7:00
    [InlineData(8, 20 / 60.0 + 8, 20 / 60.0)]   // 8:20, a minute that is not a round fraction of an hour
    [InlineData(21, 5 / 60.0 + 23, 5 / 60.0 + 2)]
    public async Task The_clock_skips_forward_to_the_hour_and_the_world_ages_by_as_much(double now, double target, double skipped)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        _game.DoString($"CLOCK.time, CLOCK.nights, CLOCK.day = {now.ToString(invariant)}, 36, 14");
        double before = Lua("CLOCK.age()");

        var world = await _client.SetTimeAsync(target);
        Assert.Equal(skipped, world.SkippedHours!.Value, 0.011);
        // to the minute that was asked for
        int minutes = (int)Math.Round(target * 60);
        Assert.Equal((minutes / 60, minutes % 60), (world.Hour, world.Minute));
        // right away, before the game has ticked: nothing in between sees the world a day off
        Assert.Equal(skipped, Lua("CLOCK.age()") - before, 0.011);

        // the game's ticks, each followed by the bridge's (OnTick comes after GameTime.update)
        _game.DoString("for i = 1, 5 do CLOCK.tick(); SpiffoCONBridgePoll() end");
        Assert.Equal(skipped, Lua("CLOCK.age()") - before, 0.011);
        Assert.Equal(target, Lua("CLOCK.time"), 0.011);
        Assert.Equal(target <= now ? 15 : 14, (int)Lua("CLOCK.day"));
    }

    [Fact]
    public async Task A_time_that_is_no_hour_of_the_day_or_almost_a_day_away_is_refused()
    {
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetTimeAsync(24));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetTimeAsync(-1));
        // not a number: no comparison holds, and the clock must not be set to it
        await Assert.ThrowsAsync<BridgeException>(() => _client.SendAsync("settime", "nan"));

        // two minutes behind the clock (or the very same time) would be a day: surely a mistake
        _game.DoString("CLOCK.time = 14 + 32 / 60");
        var ex = await Assert.ThrowsAsync<BridgeException>(() => _client.SetTimeAsync(14.5));
        Assert.Contains("it is 14:32 in game", ex.Message);
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetTimeAsync(14 + 32 / 60.0));
        Assert.Empty(Told());

        // while the game has not yet turned the day of the last change, another is not computed on it
        await _client.SetTimeAsync(10);
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetTimeAsync(12));
    }

    [Fact]
    public async Task An_area_out_of_this_world_is_refused()
    {
        // a coordinate too big to count on would loop for ever inside the server's tick
        await Assert.ThrowsAsync<BridgeException>(() => _client.SendAsync("stopfires", "1e300", "0", "5"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SendAsync("removecorpses", "nan", "0", "5"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SendAsync("removegrounditems", "0", "inf", "5", "1", "0"));
        Assert.Empty(Told());
    }

    [Fact]
    public async Task Items_and_kits_are_put_on_the_floor_of_a_loaded_square()
    {
        var done = await _client.SpawnItemsAsync(100, 100, 0, [("Base.Axe", 2), ("Mod.Unknown", 3), ("Base.Nails", 3)]);
        Assert.Equal(5, done.Spawned);
        // a type the server does not know (a mod it lacks) is named once, and the rest still goes down
        Assert.Equal(["Mod.Unknown"], done.Unknown);
        Assert.Equal(["spawn Base.Axe", "spawn Base.Axe", "spawn Base.Nails", "spawn Base.Nails", "spawn Base.Nails"], Told());
        // on the floor (no height), spread over the square rather than in one pile
        // (radius 1 also takes in the can on this square and the plank upstairs next to it, which were there)
        Assert.Equal(5 + 2, (await _client.RemoveGroundItemsAsync(100, 100, 1, apply: false, safehouses: false)).Found);
        Assert.Equal(5, _game.DoString("local seen = {} for _, p in ipairs(SPAWNED_AT) do seen[p] = true end local n = 0 for _ in pairs(seen) do n = n + 1 end return n").Number);

        // nobody near: the square is not loaded, nothing is put down
        var ex = await Assert.ThrowsAsync<BridgeException>(() => _client.SpawnItemsAsync(9000, 9000, 0, [("Base.Axe", 1)]));
        Assert.Contains("not loaded", ex.Message);
        // too many at once, or not a list the bridge can read
        await Assert.ThrowsAsync<BridgeException>(() => _client.SpawnItemsAsync(100, 100, 0, [("Base.Nails", 400), ("Base.Axe", 101)]));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SendAsync("spawnitems", "100", "100", "0", "Base.Axe"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SendAsync("spawnitems", "nan", "100", "0", "Base.Axe=1"));
        await Assert.ThrowsAsync<ArgumentException>(() => _client.SpawnItemsAsync(100, 100, 0, [("Base.Axe,Base.Gun", 1)]));
        Assert.Empty(Told());
    }

    [Fact]
    public async Task Deaths_of_players_are_kept_with_where_and_by_whom_and_survive_a_restart()
    {
        // the game fires OnCharacterDeath for every character: zombies are not kept, nor animals (IsoPlayers too)
        _game.DoString("rj.attackedBy = A_ZOMBIE; DEATH_HANDLER(rj)");
        _game.DoString("NOW = NOW + 60000; kate.attackedBy = rj; DEATH_HANDLER(kate)");
        _game.DoString("DEATH_HANDLER(A_ZOMBIE)");
        _game.DoString("DEATH_HANDLER(AN_ANIMAL)");
        var deaths = await _client.SendAsync("deaths");
        var snapshot = await _client.SnapshotAsync(deathsAndZombies: true);
        Assert.Null(snapshot.Problem);
        Assert.Equal([("rj", 100, 102, 0, "zombie"), ("kate", 500, 500, 1, "rj")],
            snapshot.Deaths!.Select(d => (d.Username, d.X!.Value, d.Y!.Value, d.Z!.Value, d.Killer)));
        Assert.Equal(60000, snapshot.Deaths![1].Time - snapshot.Deaths[0].Time);
        Assert.Equal(12.3, snapshot.Deaths[0].HoursSurvived);

        // a restart of the server: a new script finds them in its file
        var restarted = new Script();
        var folder = Path.Combine(AppContext.BaseDirectory, "Bridge");
        restarted.DoString(File.ReadAllText(Path.Combine(folder, "GameMock.lua")));
        restarted.Globals.Get("FILES").Table.Set("spiffocon_deaths.txt", _game.Globals.Get("FILES").Table.Get("spiffocon_deaths.txt"));
        restarted.DoString(File.ReadAllText(Path.Combine(folder, "SpiffoCONBridge.lua")));
        var files = new GameFiles(restarted);
        files.Tick();
        var again = new BridgeClient(files) { PollInterval = TimeSpan.FromMilliseconds(2), Timeout = TimeSpan.FromSeconds(2) };
        Assert.Equal(["rj", "kate"], (await again.SnapshotAsync(deathsAndZombies: true)).Deaths!.Select(d => d.Username));
    }

    [Fact]
    public async Task A_player_killed_by_an_animal_says_so()
    {
        _game.DoString("rj.attackedBy = AN_ANIMAL; DEATH_HANDLER(rj)");
        var death = Assert.Single((await _client.SnapshotAsync(deathsAndZombies: true)).Deaths!);
        Assert.Equal(("rj", "animal:bull"), (death.Username, death.Killer));
    }

    [Fact]
    public async Task The_animals_older_bridges_kept_as_deaths_of_Bob_are_dropped_once()
    {
        // a file of bridge v8/v9: no header, the deaths of animals as "Bob"
        var game = new Script();
        var folder = Path.Combine(AppContext.BaseDirectory, "Bridge");
        game.DoString(File.ReadAllText(Path.Combine(folder, "GameMock.lua")));
        game.Globals.Get("FILES").Table.Set("spiffocon_deaths.txt", DynValue.NewString(
            "1790000000000\tBob\t300\t300\t0\t\t0\t0\n1790000060000\trj\t100\t102\t0\tzombie\t12.3\t7\n1790000120000\tBob\t310\t305\t0\t\t0\t0\n"));
        game.DoString(File.ReadAllText(Path.Combine(folder, "SpiffoCONBridge.lua")));
        var files = new GameFiles(game);
        files.Tick();
        var client = new BridgeClient(files) { PollInterval = TimeSpan.FromMilliseconds(2), Timeout = TimeSpan.FromSeconds(2) };
        Assert.Equal(["rj"], (await client.SnapshotAsync(deathsAndZombies: true)).Deaths!.Select(d => d.Username));
        var file = game.Globals.Get("FILES").Table.Get("spiffocon_deaths.txt").String;
        Assert.StartsWith("# SpiffoCON bridge deaths v10\n", file);

        // once the file is a v10 one, a player really named Bob stays
        game.DoString("rj.username = 'Bob'; rj.getUsername = function() return 'Bob' end; DEATH_HANDLER(rj)");
        var restarted = new Script();
        restarted.DoString(File.ReadAllText(Path.Combine(folder, "GameMock.lua")));
        restarted.Globals.Get("FILES").Table.Set("spiffocon_deaths.txt", game.Globals.Get("FILES").Table.Get("spiffocon_deaths.txt"));
        restarted.DoString(File.ReadAllText(Path.Combine(folder, "SpiffoCONBridge.lua")));
        var files2 = new GameFiles(restarted);
        files2.Tick();
        var again = new BridgeClient(files2) { PollInterval = TimeSpan.FromMilliseconds(2), Timeout = TimeSpan.FromSeconds(2) };
        Assert.Equal(["rj", "Bob"], (await again.SnapshotAsync(deathsAndZombies: true)).Deaths!.Select(d => d.Username));
    }

    [Fact]
    public async Task Zombies_are_counted_per_area_of_ten_squares()
    {
        var cells = (await _client.SnapshotAsync(safehouses: true, deathsAndZombies: true)).ZombieCells!;
        Assert.Equal([(100, 100, 5), (140, 140, 1), (510, 510, 1)], cells.Select(c => (c.X, c.Y, c.N)).OrderBy(c => c.X));
    }

    [Fact]
    public async Task Wrecks_are_counted_then_removed_and_cars_and_occupied_wrecks_stay()
    {
        // the burnt car and the smashed pick-up: not the working truck, not the wreck kate sits in, not the far one
        Assert.Equal((2, 0), ((await _client.RemoveWrecksAsync(100, 100, 5, apply: false)).Found, 0));
        Assert.Empty(Told());
        var done = await _client.RemoveWrecksAsync(100, 100, 5, apply: true);
        Assert.Equal(2, done.Removed);
        Assert.Equal(["removeVehicle Base.CarNormalBurnt #8", "removeVehicle Base.PickUpTruckSmashedFront #9"], Told());
        Assert.Equal(0, (await _client.RemoveWrecksAsync(100, 100, 5, apply: false)).Found);
        Assert.Equal([7, 10, 11], (await _client.SnapshotAsync()).Vehicles.Select(v => v.Id!.Value).Order());
    }

    [Fact]
    public async Task A_vehicle_key_goes_into_the_players_inventory()
    {
        Assert.Equal("Pick-up Truck Key", await _client.GiveVehicleKeyAsync(7, "Base.PickUpTruck", "kate"));
        Assert.Equal(["sendAdd Pick-up Truck Key"], Told());
        Assert.Equal("Pick-up Truck Key", Assert.Single(await _client.InventoryAsync("kate")).Name);

        // the id is another vehicle by now, or the player left: nothing is given
        await Assert.ThrowsAsync<BridgeException>(() => _client.GiveVehicleKeyAsync(7, "Base.Van", "kate"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.GiveVehicleKeyAsync(7, "Base.PickUpTruck", "ghost"));
        Assert.Empty(Told());
    }

    [Fact]
    public async Task A_hair_style_is_listed_for_the_players_gender_then_given_and_sent()
    {
        var kate = await _client.HairStylesAsync("kate");
        Assert.True(kate.Female);
        Assert.Equal("", kate.Current);
        Assert.Equal(["Bald", "Hat", "Long2", "BunCurly"], kate.Styles.Select(s => s.Name));
        Assert.Equal(("Long", 3), (kate.Styles[2].Label, kate.Styles[2].Level));
        Assert.Null(kate.Styles[1].Label);

        // the variants drawn under hats are not offered, and a name comes once
        var rj = await _client.HairStylesAsync("rj");
        Assert.False(rj.Female);
        Assert.Equal(["Bald", "Messy", "Donny"], rj.Styles.Select(s => s.Name));

        Assert.Equal("", await _client.SetHairAsync("kate", "Long2"));
        Assert.Equal(["resetModel kate", "sendHumanVisual kate Long2"], Told());
        Assert.Equal("Long2", (await _client.HairStylesAsync("kate")).Current);
        Assert.Equal(DynValue.Nil, _game.DoString("return kate.visual.nonAttached"));

        // a style of the other gender, a hat variant, a player who left: nothing changes
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetHairAsync("rj", "Long2"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetHairAsync("rj", "HatPunkHat"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetHairAsync("ghost", "Messy"));
        Assert.Empty(Told());
        Assert.Equal("Messy", (await _client.HairStylesAsync("rj")).Current);
    }

    /// <summary>The two files of the protocol, kept in the Lua state; a write lets the bridge poll once.</summary>
    sealed class GameFiles(Script game) : IBridgeFiles
    {
        public string Description => "mock game";

        public Task<string?> ReadAsync(string name, CancellationToken ct = default)
        {
            lock (game)
            {
                var value = game.Globals.Get("FILES").Table.Get(name);
                return Task.FromResult(value.IsNil() ? null : value.String);
            }
        }

        public Task WriteAsync(string name, string text, CancellationToken ct = default)
        {
            lock (game)
            {
                game.Globals.Get("FILES").Table.Set(name, DynValue.NewString(text));
                Tick();
            }
            return Task.CompletedTask;
        }

        public void Tick()
        {
            lock (game)
            {
                game.Globals.Set("NOW", DynValue.NewNumber(game.Globals.Get("NOW").Number + 2000));
                game.Call(game.Globals.Get("SpiffoCONBridgePoll"));
            }
        }

        public void Dispose() { }
    }
}
