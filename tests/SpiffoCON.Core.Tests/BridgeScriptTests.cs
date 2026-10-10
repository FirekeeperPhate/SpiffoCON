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
        Assert.Equal(14, await _client.PingAsync());
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
        // bridge v12: the id addxp takes, and the experience as the game's skill bar counts it
        Assert.Equal([("Axe", 870.0, 120.0, 375.0), ("Woodwork", 300.0, 75.0, 225.0)], sheet.Skills.Select(s => (s.Id!, s.Xp!.Value, s.LevelXp!.Value, s.NextXp!.Value)));
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
    // only when the game names the animal as the last attacker: its own attacks do not set it (see the bridge)
    public async Task A_player_killed_by_an_animal_says_so()
    {
        _game.DoString("rj.attackedBy = AN_ANIMAL; DEATH_HANDLER(rj)");
        var death = Assert.Single((await _client.SnapshotAsync(deathsAndZombies: true)).Deaths!);
        Assert.Equal(("rj", "animal:bull"), (death.Username, death.Killer));
    }

    [Fact]
    public async Task The_animals_older_bridges_kept_as_deaths_of_Bob_are_dropped_once()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Bridge");
        // a server restart: a new script on the files the last one left
        async Task<(BridgeClient Client, Script Game)> StartAsync(Table? files, string? deathsFile = null)
        {
            var game = new Script();
            game.DoString(File.ReadAllText(Path.Combine(folder, "GameMock.lua")));
            if (files is not null)
                foreach (var pair in files.Pairs)
                    game.Globals.Get("FILES").Table.Set(pair.Key, pair.Value);
            if (deathsFile is not null)
                game.Globals.Get("FILES").Table.Set("spiffocon_deaths.txt", DynValue.NewString(deathsFile));
            game.DoString(File.ReadAllText(Path.Combine(folder, "SpiffoCONBridge.lua")));
            var gameFiles = new GameFiles(game);
            gameFiles.Tick();
            await Task.CompletedTask;
            return (new BridgeClient(gameFiles) { PollInterval = TimeSpan.FromMilliseconds(2), Timeout = TimeSpan.FromSeconds(2) }, game);
        }

        // a file of bridge v8/v9: no header, the deaths of animals as "Bob", and a player killed by "Bob": a
        // real player most likely (an animal's attack does not name it), so that one stays as it is
        var (client, game) = await StartAsync(null,
            "1790000000000\tBob\t300\t300\t0\t\t0\t0\n1790000060000\trj\t100\t102\t0\tzombie\t12.3\t7\n"
            + "1790000120000\tBob\t310\t305\t0\t\t0\t0\n1790000180000\tkate\t320\t300\t0\tBob\t30\t2\n");
        var deaths = (await client.SnapshotAsync(deathsAndZombies: true)).Deaths!;
        Assert.Equal([("rj", "zombie"), ("kate", "Bob")], deaths.Select(d => (d.Username, d.Killer)));
        Assert.StartsWith("# SpiffoCON bridge deaths v10\n", game.Globals.Get("FILES").Table.Get("spiffocon_deaths.txt").String);

        // once cleaned, a player really named Bob stays: after a restart...
        game.DoString("rj.username = 'Bob'; rj.getUsername = function() return 'Bob' end; DEATH_HANDLER(rj)");
        (client, game) = await StartAsync(game.Globals.Get("FILES").Table);
        Assert.Equal(["rj", "kate", "Bob"], (await client.SnapshotAsync(deathsAndZombies: true)).Deaths!.Select(d => d.Username));

        // ...and after a while on bridge v9, which rewrites the file without the header
        var v9File = string.Join("\n", game.Globals.Get("FILES").Table.Get("spiffocon_deaths.txt").String.Split('\n').Skip(1));
        (client, _) = await StartAsync(game.Globals.Get("FILES").Table, v9File);
        Assert.Equal(["rj", "kate", "Bob"], (await client.SnapshotAsync(deathsAndZombies: true)).Deaths!.Select(d => d.Username));
    }

    [Fact]
    public async Task The_Bob_clean_up_is_not_marked_done_when_the_cleaned_file_could_not_be_saved()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Bridge");
        var game = new Script();
        game.DoString(File.ReadAllText(Path.Combine(folder, "GameMock.lua")));
        game.Globals.Get("FILES").Table.Set("spiffocon_deaths.txt", DynValue.NewString("1790000000000\tBob\t300\t300\t0\t\t0\t0\n"));
        // the deaths file can't be written (a full disk, say)
        game.DoString("local write = getFileWriter; getFileWriter = function(name, ...) if name == 'spiffocon_deaths.txt' then error('disk full') end return write(name, ...) end");
        game.DoString(File.ReadAllText(Path.Combine(folder, "SpiffoCONBridge.lua")));
        var files = new GameFiles(game);
        files.Tick();
        var client = new BridgeClient(files) { PollInterval = TimeSpan.FromMilliseconds(2), Timeout = TimeSpan.FromSeconds(2) };
        Assert.Empty((await client.SnapshotAsync(deathsAndZombies: true)).Deaths!);
        Assert.True(game.Globals.Get("FILES").Table.Get("spiffocon_deaths_v10.txt").IsNil());
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

        // a tied style remembers the loose hair, as the game's own hair cut does ("Untie" gives it back)
        Assert.Equal("Long2", await _client.SetHairAsync("kate", "BunCurly"));
        Told();
        Assert.Equal("Long2", _game.DoString("return kate.visual.nonAttached").String);
        await _client.SetHairAsync("kate", "Long2");
        Told();
        Assert.Equal(DynValue.Nil, _game.DoString("return kate.visual.nonAttached"));
        // from hair shorter than the tied style: nothing remembered (the game's character screen picks one)
        await _client.SetHairAsync("kate", "Hat");
        await _client.SetHairAsync("kate", "BunCurly");
        Told();
        Assert.Equal(DynValue.Nil, _game.DoString("return kate.visual.nonAttached"));

        // a player who just died is still online: no new look for them, no heal, no cure
        _game.DoString("kate.isDead = function() return true end");
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetHairAsync("kate", "Long2"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.CureInfectionAsync("kate"));
        Assert.Empty(Told());

        // a style of the other gender, a hat variant, a player who left: nothing changes
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetHairAsync("rj", "Long2"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetHairAsync("rj", "HatPunkHat"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetHairAsync("ghost", "Messy"));
        Assert.Empty(Told());
        Assert.Equal("Messy", (await _client.HairStylesAsync("rj")).Current);
    }

    [Fact]
    public async Task Beards_and_colours_are_listed_and_set_for_men_and_colours_for_women()
    {
        var rj = await _client.HairStylesAsync("rj");
        Assert.Equal("Moustache", rj.Beard);
        Assert.Equal(["Moustache", "Full"], rj.Beards!.Select(b => b.Name));
        Assert.Equal(3, rj.Colors!.Count);
        Assert.Equal((0.83, 0.67, 0.27), (rj.Colors![0].R, rj.Colors[0].G, rj.Colors[0].B));
        Assert.Equal(0.5, rj.HairColor!.R);
        var kate = await _client.HairStylesAsync("kate");
        Assert.Null(kate.Beards);
        Assert.Null(kate.Beard);
        Assert.Equal(3, kate.Colors!.Count);

        await _client.SetBeardAsync("rj", "Full");
        Assert.Equal(["resetModel rj", "sendHumanVisual rj Messy"], Told());
        Assert.Equal("Full", (await _client.HairStylesAsync("rj")).Beard);
        await _client.SetBeardAsync("rj", "");
        Told();
        Assert.Equal("", (await _client.HairStylesAsync("rj")).Beard);

        await _client.SetHairColorAsync("kate", new BridgeColor { R = 0.1, G = 0.08, B = 0.05 });
        Assert.Equal(["resetModel kate", "sendHumanVisual kate "], Told());
        var colored = await _client.HairStylesAsync("kate");
        Assert.Equal((0.1, 0.08, 0.05), (colored.HairColor!.R, colored.HairColor.G, colored.HairColor.B));
        // natural colours too, as when a character is made: a cut to bald goes back to it
        Assert.Equal(0.1, _game.DoString("return kate.visual.naturalHair.r").Number);

        // a beard for a woman, an unknown beard, a colour out of range: nothing changes
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetBeardAsync("kate", "Full"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetBeardAsync("rj", "Braids"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.SetHairColorAsync("rj", new BridgeColor { R = 2, G = 0, B = 0 }));
        Assert.Empty(Told());
        Assert.Equal("", (await _client.HairStylesAsync("rj")).Beard);
        Assert.Equal(0.5, (await _client.HairStylesAsync("rj")).HairColor!.R);
        Assert.Equal(0.1, (await _client.HairStylesAsync("kate")).HairColor!.R);
    }

    [Fact]
    public async Task The_zombie_infection_is_cured_on_the_body_and_its_parts_and_sent()
    {
        Assert.True((await _client.PlayerDetailsAsync("kate")).Infected);
        Assert.Equal((true, false), await _client.CureInfectionAsync("kate"));
        Assert.Equal(["syncBodyPart Hand_L", "syncPlayerStats kate 12288", "sendDamage kate"], Told());
        // as a new character: no time or mortality, no infection or fever
        Assert.Equal((-1, -1), (Lua("kate.body.time"), Lua("kate.body.mortality")));
        Assert.Equal((0, 0), (Lua("kate.stats[CharacterStat.ZOMBIE_INFECTION]"), Lua("kate.stats[CharacterStat.ZOMBIE_FEVER]")));
        Assert.False(_game.DoString("return kate.parts[1].infected").Boolean);
        Assert.False((await _client.PlayerDetailsAsync("kate")).Infected);

        // someone not infected: nothing to send for the parts, and the reply says so
        Assert.Equal((false, false), await _client.CureInfectionAsync("rj"));
        Assert.Equal(["syncPlayerStats rj 12288", "sendDamage rj"], Told());
        await Assert.ThrowsAsync<BridgeException>(() => _client.CureInfectionAsync("ghost"));
    }

    [Fact]
    public async Task The_characters_name_is_with_the_player_and_their_death()
    {
        var players = (await _client.SnapshotAsync()).Players;
        Assert.Equal("Ray Jones", players.Single(p => p.Username == "rj").CharacterName);
        // no surname: no trailing space
        Assert.Equal("Kate", players.Single(p => p.Username == "kate").CharacterName);
        Assert.Equal("Ray Jones", (await _client.PlayerDetailsAsync("rj")).CharacterName);

        _game.DoString("rj.attackedBy = A_ZOMBIE; DEATH_HANDLER(rj)");
        Assert.Equal("Ray Jones", Assert.Single((await _client.SnapshotAsync(deathsAndZombies: true)).Deaths!).CharacterName);
        // kept in the file as a ninth column
        Assert.Contains("\t7\tRay Jones\n", _game.Globals.Get("FILES").Table.Get("spiffocon_deaths.txt").String);

        // and read back after a restart
        var restarted = new Script();
        var folder = Path.Combine(AppContext.BaseDirectory, "Bridge");
        restarted.DoString(File.ReadAllText(Path.Combine(folder, "GameMock.lua")));
        restarted.Globals.Get("FILES").Table.Set("spiffocon_deaths.txt", _game.Globals.Get("FILES").Table.Get("spiffocon_deaths.txt"));
        restarted.DoString(File.ReadAllText(Path.Combine(folder, "SpiffoCONBridge.lua")));
        var files = new GameFiles(restarted);
        files.Tick();
        var again = new BridgeClient(files) { PollInterval = TimeSpan.FromMilliseconds(2), Timeout = TimeSpan.FromSeconds(2) };
        Assert.Equal("Ray Jones", Assert.Single((await again.SnapshotAsync(deathsAndZombies: true)).Deaths!).CharacterName);
    }

    [Fact]
    public async Task The_sheet_has_the_body_part_by_part_and_what_is_worn_held_and_attached()
    {
        // one round trip for the inventory and the sheet
        var (items, kate) = await _client.CharacterAsync("kate", details: true);
        Assert.NotNull(kate);
        Assert.Equal(["Left Hand", "Upper Torso"], kate.Parts!.Select(p => p.Name));
        var parts = kate.Parts!;
        Assert.Equal((62, "bitten, bleeding, zombie infection, dirty bandage"), (parts[0].Health, string.Join(", ", parts[0].Conditions)));
        Assert.Equal((100, 0), (parts[1].Health, parts[1].Conditions.Count));
        Assert.Empty(kate.Equipment!);
        Assert.Equal(7.3, kate.Weight!.Value, 3);
        Assert.Equal((15, true), (kate.MaxWeight, kate.Asleep));
        Assert.Empty(items);

        var (rjItems, rj) = await _client.CharacterAsync("rj", details: true);
        Assert.NotEmpty(rjItems);
        Assert.Equal(
            [("hand", "Both hands", "Axe", 0.7), ("worn", "Jacket", "Padded Jacket", 1.0), ("worn", "base:shoes", "Black Shoes", null),
                ("attached", "Belt Left", "Hunting Knife", 0.5)],
            rj!.Equipment!.Select(e => (e.Kind, e.Slot, e.Name, e.Condition is { } c ? Math.Round(c, 2) : (double?)null)));
        Assert.Equal("Base.Axe", rj.Equipment![0].FullType);
        Assert.All(rj.Parts!, p => Assert.Empty(p.Conditions));

        // without the sheet (an older bridge): the inventory only
        var (_, none) = await _client.CharacterAsync("rj", details: false);
        Assert.Null(none);
        await Assert.ThrowsAsync<BridgeException>(() => _client.CharacterAsync("ghost", details: true));
    }

    [Fact]
    public async Task A_complete_repair_puts_back_the_parts_that_are_gone_and_names_them()
    {
        // the list says what is gone: a part that takes no item (a seat) is not "missing"
        var truck = (await _client.SnapshotAsync()).Vehicles.Single(v => v.Id == 7);
        Assert.Equal(["Front Left Tire", "Rear Right Window"], truck.Missing);
        Assert.Equal("2: Front Left Tire, Rear Right Window", truck.MissingText);
        Assert.Empty((await _client.SnapshotAsync()).Vehicles.Single(v => v.Id == 8).Missing!);

        // the game's own repair restores the wheel; the window it leaves out is installed as a mechanic's install
        // ends (the item, the part's install.complete, sent to the players)
        var done = await _client.RepairVehicleAsync(7, "Base.PickUpTruck");
        Assert.Equal(["Front Left Tire", "Rear Right Window"], done.Missing);
        Assert.Equal(["Rear Right Window"], done.Installed);
        Assert.Empty(done.StillMissing);
        Assert.Equal(
            ["repair 7", "createPartInventoryItem WindowRearRight", "callLua Vehicles.InstallComplete.WindowRearRight WindowRearRight", "transmitPartItem WindowRearRight"],
            Told());
        Assert.Equal(" 2 missing parts put back (Front Left Tire, Rear Right Window).", done.Describe());
        // every part with an item at 100, the new window too (new parts come worn)
        Assert.Equal(300, Lua("VEHICLES[1].parts[1].condition + VEHICLES[1].parts[2].condition + VEHICLES[1].parts[3].condition"));
        Assert.Empty((await _client.SnapshotAsync()).Vehicles.Single(v => v.Id == 7).Missing!);

        // nothing missing: a plain repair, nothing to name
        var again = await _client.RepairVehicleAsync(7, "Base.PickUpTruck");
        Assert.Equal("", again.Describe());
        Assert.Equal(["repair 7"], Told());
    }

    [Fact]
    public async Task The_vehicle_a_player_is_in_is_repaired_and_what_can_not_be_put_back_is_said()
    {
        await Assert.ThrowsAsync<BridgeException>(() => _client.RepairVehicleOfAsync("kate"));   // on foot
        await Assert.ThrowsAsync<BridgeException>(() => _client.RepairVehicleOfAsync("ghost"));
        Assert.Empty(Told());

        // a part no item can be made for stays missing, and the reply says so
        _game.DoString("""
            local truck = VEHICLES[1]
            truck.parts[3].kind = "never"
            kate.getVehicle = function() return truck end
            """);
        var done = await _client.RepairVehicleOfAsync("kate");
        Assert.Equal(("Base.PickUpTruck", 7), (done.Vehicle, done.Id));
        Assert.Equal(["Front Left Tire", "Rear Right Window"], done.Missing);
        Assert.Empty(done.Installed);
        Assert.Equal(["Rear Right Window"], done.StillMissing);
        Assert.Equal(" 1 missing part put back (Front Left Tire). Still missing: Rear Right Window.", done.Describe());
    }

    [Fact]
    public async Task A_vehicle_is_read_part_by_part_as_the_mechanics_window_shows_it()
    {
        _game.DoString("""
            local truck = VEHICLES[1]
            truck.seated[0] = rj
            truck.parts[2].content, truck.parts[2].amount, truck.parts[2].capacity = "Air", 0, 35
            truck.parts[1].item.getCurrentUsesFloat = function() return 0.5 end
            rj.getVehicle = function() return truck end
            """);
        var truck = await _client.VehicleDetailsAsync(7, "Base.PickUpTruck");
        Assert.Equal((7, "Base.PickUpTruck", "Chevalier D6", "Heavy-Duty"), (truck.Id, truck.Script, truck.Name, truck.Kind));
        Assert.Equal((1200, 340, 71, 95, 0.25), (truck.Mass, truck.EnginePower, truck.EngineQuality, truck.EngineLoudness, truck.Rust));
        Assert.Equal((false, true), (truck.Hotwired, truck.KeyInIgnition));
        Assert.Equal([(0, "rj")], truck.Seats.Select(s => (s.Seat, s.Username)));
        // every part counts for the overall condition, one that is gone as 0: (55 + 0 + 0 + 0) / 4
        Assert.Equal(13.75, truck.Condition);

        // the seat is "nodisplay": the game's window does not list it either
        Assert.Equal(["Battery", "TireFrontLeft", "WindowRearRight"], truck.Parts.Select(p => p.Id));
        var battery = truck.Parts[0];
        Assert.Equal(("Battery", "engine", 55, false, true, "Battery", 0.5), (battery.Name, battery.Category, battery.Condition, battery.Missing, battery.TakesItem, battery.Item, battery.Charge));
        var tyre = truck.Parts[1];
        Assert.Equal(("Front Left Tire", "tire", "Tires", true, (string?)null), (tyre.Name, tyre.Category, tyre.CategoryName, tyre.Missing, tyre.Item));
        Assert.Equal(("Air", 0, 35), (tyre.Content, tyre.Amount, tyre.Capacity));
        // a category the game has no name for is shown as it is
        Assert.Equal("door", truck.Parts[2].CategoryName);

        // the players list says which vehicle, for the window
        var players = (await _client.SnapshotAsync()).Players;
        Assert.Equal(7, players.Single(p => p.Username == "rj").VehicleId);
        Assert.Null(players.Single(p => p.Username == "kate").VehicleId);

        // a burnt one is named after the model it was; an id that now is another model is refused
        Assert.Equal("Burnt Dash Rancher", (await _client.VehicleDetailsAsync(10)).Name);
        await Assert.ThrowsAsync<BridgeException>(() => _client.VehicleDetailsAsync(7, "Base.CarNormal"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.VehicleDetailsAsync(99));
    }

    [Fact]
    public async Task One_part_is_repaired_or_put_back_and_the_rest_is_left_as_it_is()
    {
        // a worn part: to 100
        var battery = await _client.RepairPartAsync(7, "Base.PickUpTruck", "Battery");
        Assert.Equal(("Battery", false, false, 55, 100), (battery.Part, battery.WasMissing, battery.Missing, battery.Before, battery.Condition));
        Assert.Empty(Told());

        // one that is gone and the game's repair leaves out: installed as by a mechanic, then whole
        var window = await _client.RepairPartAsync(7, "Base.PickUpTruck", "WindowRearRight");
        Assert.Equal(("Rear Right Window", true, false, 100), (window.Part, window.WasMissing, window.Missing, window.Condition));
        Assert.Equal(
            ["createPartInventoryItem WindowRearRight", "callLua Vehicles.InstallComplete.WindowRearRight WindowRearRight", "transmitPartItem WindowRearRight"],
            Told());

        // the wheel nobody asked for is still gone
        var truck = await _client.VehicleDetailsAsync(7);
        Assert.Equal(["TireFrontLeft"], truck.Parts.Where(p => p.Missing).Select(p => p.Id));

        // one no item can be made for stays gone, and the reply says so
        _game.DoString("VEHICLES[1].parts[2].kind = 'never'");
        var tyre = await _client.RepairPartAsync(7, "Base.PickUpTruck", "TireFrontLeft");
        Assert.Equal((true, true), (tyre.WasMissing, tyre.Missing));

        await Assert.ThrowsAsync<BridgeException>(() => _client.RepairPartAsync(7, "Base.PickUpTruck", "Wings"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.RepairPartAsync(7, "Base.CarNormal", "Battery"));
    }

    [Fact]
    public async Task Containers_that_lost_their_liquid_part_are_counted_then_replaced_where_they_are()
    {
        _game.DoString("BROKEN_FLUIDS()");

        // counting touches nothing: rj carries three (one in a bag, one on the belt), kate one; near rj one lies
        // on a table and one is in a crate, with a pot of a type the game no longer makes. The whole bucket put
        // down next to them has its part on the object, as the game keeps it: not one of them
        var count = await _client.FixFluidsAsync(apply: false, radius: 5);
        Assert.Equal((7, 0, 1, 0), (count.Found, count.Fixed, count.Skipped, count.Failed));
        Assert.Equal((4, 1, 2), (count.InInventories, count.OnSquares, count.InContainers));
        Assert.Equal((2, 5), (count.Players, count.Radius));
        Assert.Equal("4 × Bucket, 2 × Water Bottle, 1 × Pot", count.Describe());
        Assert.Equal(["kate", "rj", "rj"], count.Items.Where(i => i.Where != "").Select(i => i.Where).Order());
        Assert.Empty(Told());

        // replaced: out and in for the ones carried or in the crate; the one on the table put down again at
        // the same spot, turned the same way, still "placed by a player"
        var done = await _client.FixFluidsAsync(apply: true, radius: 5);
        Assert.Equal((7, 5, 1, 1), (done.Found, done.Fixed, done.Skipped, done.Failed));
        var told = Told();
        Assert.Equal(4, told.Count(l => l.StartsWith("sendRemove ")));
        Assert.Equal(4, told.Count(l => l.StartsWith("sendAdd new ")));
        Assert.Contains("removeGround Bucket", told);
        Assert.Single(told, l => l.StartsWith("sendPlaced new Base.Bucket "));
        Assert.Equal(0.95, Lua("(function() local o = GROUND_AT(101, 103, 1)[1].offsets return o[1] + o[2] + o[3] end)()"), 6);
        Assert.Equal(1, Lua("#GROUND_AT(101, 103, 1)"));
        Assert.Equal(90, Lua("GROUND_AT(101, 103, 1)[1].getItem().rotation[3]"));
        Assert.True(_game.DoString("local o = GROUND_AT(101, 103, 1)[1] return o.fluid ~= nil and o.extended and o.keep").Boolean);
        // the whole one next to it was left alone, and the crate still has its four things
        Assert.Equal("Empty Bucket", _game.DoString("return GROUND_AT(101, 102, 0)[1].name").String);
        Assert.Equal(4, Lua("CRATE.count()"));

        // what is left: the bottle on the belt and the pot nothing can be made for
        var after = await _client.FixFluidsAsync(apply: false, radius: 5);
        Assert.Equal((2, 1), (after.Found, after.Skipped));
        Assert.Equal("1 × Pot, 1 × Water Bottle", after.Describe());

        // a wider look finds the one that was too far; one player only: what they carry, and around them
        Assert.Equal(3, (await _client.FixFluidsAsync(apply: false, radius: 20)).Found);
        _game.DoString("kate.inventory:AddItem(FLUID_ITEM('Base.Bucket', 'Bucket', false))");
        var kate = await _client.FixFluidsAsync(apply: true, radius: 20, username: "kate");
        Assert.Equal((1, 1, 1, 0), (kate.Players, kate.Found, kate.Fixed, kate.Loaded));
        await Assert.ThrowsAsync<BridgeException>(() => _client.FixFluidsAsync(apply: true, radius: 5, username: "ghost"));
    }

    [Fact]
    public async Task The_inventory_says_the_state_of_the_items_and_they_can_be_repaired_cleaned_filled_and_recharged()
    {
        _game.DoString("GEAR()");
        const string bag = "Inventory > Tool Bag";
        async Task<BridgeItem> Row(string fullType) =>
            (await _client.InventoryAsync("rj")).Single(i => i.Container == bag && i.FullType == fullType);

        // a row is the worst of its items; what does not apply is not there
        var axes = await Row("Base.WoodAxe");
        Assert.Equal((2, 0.4, true, true), (axes.Count, axes.Condition, axes.Washable, axes.Dirty));
        Assert.Equal(((double?)null, (double?)null), (axes.Uses, axes.Fill));
        Assert.Equal((0.5, true, true), ((await Row("Base.Jacket_Padding")) is var j ? (j.Condition, j.Washable, j.Dirty) : default));
        Assert.Equal((0.3, (double?)null), ((await Row("Base.Battery")) is var b ? (b.Uses, b.Condition) : default));
        Assert.Equal((0.25, true), ((await Row("Base.Bucket")) is var w ? (w.Fill, w.Water) : default));
        Assert.False((await Row("Base.PetrolCan")).Water);
        var nails = await Row("Base.Nails");
        Assert.Equal(((double?)null, (double?)null, (double?)null, (bool?)null), (nails.Condition, nails.Uses, nails.Fill, nails.Washable));
        Told();

        // repair: both axes as new, each told to the player's game
        Assert.Equal((2, 0), await _client.ItemActionAsync("rj", BridgeClient.ItemAction.Repair, "Base.WoodAxe", bag));
        Assert.Equal(["syncFields Wood Axe", "syncFields Wood Axe"], Told());
        Assert.True(_game.DoString("local ok = true for _, it in ipairs(GEAR_BAG.getItems().items) do if it.fullType == 'Base.WoodAxe' then "
            + "ok = ok and it.condition == 10 and it.head == 10 and it.sharp == 1 and it.repaired == 0 and it.broken == false end end return ok").Boolean);
        // still bloody: that is Clean
        Assert.Equal((1.0, true), ((await Row("Base.WoodAxe")) is var a ? (a.Condition, a.Dirty) : default));
        Assert.Equal((2, 0), await _client.ItemActionAsync("rj", BridgeClient.ItemAction.Clean, "Base.WoodAxe", bag));
        Assert.False((await Row("Base.WoodAxe")).Dirty);
        Told();

        // clothes: mended and washed at once, and what the others see of the player is sent again
        Assert.Equal((1, 0), await _client.ItemActionAsync("rj", BridgeClient.ItemAction.Repair, "Base.Jacket_Padding", bag));
        Assert.Equal(["syncFields Padded Jacket", "syncVisuals rj"], Told());
        Assert.Equal(0, Lua("(function() for _, it in ipairs(GEAR_BAG.getItems().items) do if it.fullType == 'Base.Jacket_Padding' then return it.holes + it.dirt end end end)()"));
        Assert.Equal((1.0, false), ((await Row("Base.Jacket_Padding")) is var m ? (m.Condition, m.Dirty) : default));

        // a battery full again; a bucket to the top with water, then emptied; a gas can takes no water
        Assert.Equal((1, 0), await _client.ItemActionAsync("rj", BridgeClient.ItemAction.Recharge, "Base.Battery", bag));
        Assert.Equal(1.0, (await Row("Base.Battery")).Uses);
        Assert.Equal((1, 0), await _client.ItemActionAsync("rj", BridgeClient.ItemAction.Fill, "Base.Bucket", bag));
        Assert.Equal(1.0, (await Row("Base.Bucket")).Fill);
        Assert.Equal((1, 0), await _client.ItemActionAsync("rj", BridgeClient.ItemAction.Empty, "Base.Bucket", bag));
        Assert.Equal(0.0, (await Row("Base.Bucket")).Fill);
        Assert.Equal((0, 1), await _client.ItemActionAsync("rj", BridgeClient.ItemAction.Fill, "Base.PetrolCan", bag));
        Assert.Equal(0.5, (await Row("Base.PetrolCan")).Fill);

        // what does not apply is left and said; what is not there is refused
        Assert.Equal((0, 1), await _client.ItemActionAsync("rj", BridgeClient.ItemAction.Recharge, "Base.Nails", bag));
        await Assert.ThrowsAsync<BridgeException>(() => _client.ItemActionAsync("rj", BridgeClient.ItemAction.Repair, "Base.WoodAxe", "Inventory"));
        await Assert.ThrowsAsync<BridgeException>(() => _client.ItemActionAsync("ghost", BridgeClient.ItemAction.Repair, "Base.WoodAxe", bag));
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
