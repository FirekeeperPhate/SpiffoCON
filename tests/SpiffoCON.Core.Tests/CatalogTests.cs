using SpiffoCON.Core.Catalog;
using SpiffoCON.Core.Commands;

namespace SpiffoCON.Core.Tests;

public sealed class CatalogTests : IDisposable
{
    readonly string _temp = Directory.CreateTempSubdirectory("spiffocon-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void Parser_reads_blocks_values_and_comments()
    {
        var root = ScriptParser.Parse("""
            module Base
            {
                imports { Base }
                /* a comment, with a comma { and a brace */
                item Axe
                {
                    DisplayCategory = ToolWeapon,
                    Weight = 3,
                    Tags = base:choptree;base:axe
                    Icon = Axe
                }
                // line comment
                template vehicle Van
                {
                    model { file = Vehicles_Van, offset = 0.0 0.2 0.0, }
                }
            }
            """);

        var module = Assert.Single(root.Children);
        Assert.Equal(("module", "Base"), (module.Type, module.Name));
        var axe = module.Children.Single(b => b.Type == "item");
        Assert.Equal("Axe", axe.Name);
        Assert.Equal("3", axe.Get("weight"));
        Assert.Equal("base:choptree;base:axe", axe.Get("Tags"));
        Assert.Equal("Axe", axe.Get("Icon"));
        var van = module.Children.Single(b => b.Type == "template vehicle");
        Assert.Equal("0.0 0.2 0.0", van.Children.Single().Get("offset"));
    }

    [Fact]
    public void Parser_survives_unbalanced_braces()
    {
        var root = ScriptParser.Parse("module Base { item Broken { Weight = 1, ");
        Assert.Equal("1", root.Children[0].Children[0].Get("Weight"));
    }

    [Fact]
    public void Builder_merges_mod_overrides_and_resolves_names()
    {
        var game = MakeRoot("game", scripts: """
            module Base {
                item Axe { DisplayCategory = ToolWeapon, ItemType = base:weapon, Icon = Axe, Weight = 3, }
                item ZedDmg_Mouth { ItemType = base:clothing, }
                template vehicle PickUpTruckLights { carModelName = PickUpTruck, }
                vehicle PickUpTruckLightsFossoil { template! = PickUpTruckLights, }
                vehicle CarNormal { }
                vehicle CarNormalBurnt { }
            }
            """,
            translations: new()
            {
                ["ItemName.json"] = """{ "Base.Axe": "Firefighter Axe" }""",
                ["IG_UI.json"] = """{ "IGUI_VehicleNamePickUpTruck": "Chevalier D6", "IGUI_VehicleNameCarNormal": "Chevalier Nyala" }""",
            });
        var mod = MakeRoot("mod", scripts: """
            module Base { item Axe { Weight = 2.5, } }
            module MyMod { item Katana { DisplayName = Katana (old style), ItemType = base:weapon, Icon = Katana, } }
            """,
            icons: ["Item_Katana.png"]);

        var builder = new CatalogBuilder();
        builder.AddContentRoot(game, CatalogSource.Vanilla);
        var source = new CatalogSource("My Mod", "mymod", "123");
        builder.AddContentRoot(mod, source);
        var entries = builder.Build().ToDictionary(e => e.FullType);

        var axe = entries["Base.Axe"];
        Assert.Equal(("Firefighter Axe", "Tool Weapon", 2.5f), (axe.DisplayName, axe.Category, axe.Weight));
        Assert.True(axe.Source.IsVanilla);
        Assert.Equal(source, axe.ChangedBy);

        var katana = entries["MyMod.Katana"];
        Assert.Equal(("Katana (old style)", "Weapon", source), (katana.DisplayName, katana.Category, katana.Source));
        Assert.EndsWith("Item_Katana.png", katana.IconPath);
        Assert.False(katana.Hidden);

        Assert.True(entries["Base.ZedDmg_Mouth"].Hidden); // untranslated base-game item
        Assert.Equal("Chevalier D6", entries["Base.PickUpTruckLightsFossoil"].DisplayName);
        Assert.Equal("Burnt Chevalier Nyala", entries["Base.CarNormalBurnt"].DisplayName);
        Assert.False(entries.ContainsKey("Base.PickUpTruckLights")); // templates are not spawnable
    }

    [Fact]
    public void Legacy_translation_files_are_read()
    {
        var root = MakeRoot("legacy", scripts: "module M { item Thing { } }",
            translations: new() { ["ItemName_EN.txt"] = "ItemName_EN = {\n    ItemName_M.Thing = \"Old \\\"Thing\\\"\",\n}" });
        var builder = new CatalogBuilder();
        builder.AddContentRoot(root, new CatalogSource("x", "x"));
        Assert.Equal("Old \"Thing\"", builder.Build().Single().DisplayName);
    }

    [Fact]
    public void Snapshot_round_trips()
    {
        var builder = new CatalogBuilder();
        builder.AddScript("module Base { item Axe { Weight = 3, } vehicle Van { } }", CatalogSource.Vanilla);
        var copy = new CatalogBuilder();
        copy.ImportSnapshot(builder.ExportSnapshot());
        Assert.Equal(builder.Build(), copy.Build());
    }

    [Fact]
    public void Bundled_vanilla_catalog_loads()
    {
        var entries = CatalogService.LoadVanilla();
        Assert.True(entries.Count(e => e.Kind == CatalogKind.Item) > 4000);
        Assert.Contains(entries, e => e.FullType == "Base.CarNormal" && e.DisplayName == "Chevalier Nyala");
    }

    [Fact]
    public void Mod_folders_pick_common_and_newest_42_version()
    {
        var mod = Path.Combine(_temp, "ws", "111", "mods", "cool");
        foreach (var v in new[] { "42.0", "42.13", "42.20", "43" })
        {
            Directory.CreateDirectory(Path.Combine(mod, v));
            File.WriteAllText(Path.Combine(mod, v, "mod.info"), $"name=Cool {v}\nid=coolmod\nrequire=\\damnlib,\\other\n");
        }
        Directory.CreateDirectory(Path.Combine(mod, "common"));

        var latest = Assert.Single(ModInfo.Scan(Path.Combine(_temp, "ws", "111"), "111", new Version(42, 20, 1)));
        Assert.Equal(("coolmod", "Cool 42.20", "111"), (latest.Id, latest.Name, latest.WorkshopId));
        Assert.Equal(["damnlib", "other"], latest.Requires);
        Assert.Equal([Path.Combine(mod, "common"), Path.Combine(mod, "42.20")], latest.ContentRoots);

        var older = ModInfo.Read(mod, "111", new Version(42, 15));
        Assert.EndsWith("42.13", older!.ContentRoots[^1]);
    }

    [Fact]
    public void Server_options_from_showoptions()
    {
        var options = ServerOptions.Parse("List of Server Options:\n* Mods=\\damnlib;\\49powerWagon;;\\damnlib\n* WorkshopItems=3171167894;2900580391\n* PVP=true\n");
        Assert.Equal(["damnlib", "49powerWagon"], options.Mods);
        Assert.Equal(["3171167894", "2900580391"], options.WorkshopItems);
        Assert.Equal("true", options["pvp"]);
    }

    [Fact]
    public void Player_commands()
    {
        Assert.Equal(["Rick", "Daryl Dixon"], PlayerCommands.ParsePlayers("Players connected (2): \n-Rick\n-Daryl Dixon\n"));
        Assert.Equal("additem \"Daryl Dixon\" \"Base.Axe\" 2", PlayerCommands.AddItem("Daryl Dixon", "Base.Axe", 2));
        Assert.Equal("addvehicle \"Base.Van\" \"Rick\"", PlayerCommands.AddVehicle("Base.Van", "Rick"));
        Assert.Equal(CommandOutcome.Success, PlayerCommands.InterpretAddItem("Item Base.Axe Added in Rick's inventory."));
        Assert.Equal(CommandOutcome.Failed, PlayerCommands.InterpretAddItem("No such user"));
        Assert.Equal(CommandOutcome.Success, PlayerCommands.InterpretAddVehicle("Vehicle spawned"));
        Assert.Equal(CommandOutcome.Failed, PlayerCommands.InterpretAddVehicle("Unknown vehicle script \"Base.Nope\""));
    }

    string MakeRoot(string name, string scripts, Dictionary<string, string>? translations = null, string[]? icons = null)
    {
        var root = Path.Combine(_temp, name);
        Directory.CreateDirectory(Path.Combine(root, "media", "scripts"));
        File.WriteAllText(Path.Combine(root, "media", "scripts", "items.txt"), scripts);
        var translate = Path.Combine(root, "media", "lua", "shared", "Translate", "EN");
        Directory.CreateDirectory(translate);
        foreach (var (file, text) in translations ?? [])
            File.WriteAllText(Path.Combine(translate, file), text);
        var textures = Path.Combine(root, "media", "textures", "sub");
        Directory.CreateDirectory(textures);
        foreach (var icon in icons ?? [])
            File.WriteAllBytes(Path.Combine(textures, icon), [0x89, 0x50]);
        return root;
    }
}
